using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

/// <summary>Reads subscription URLs without exposing their credentials.</summary>
internal static class ProxyNativeUriParser
{
    public static bool TryGetMetadata(
        string value,
        out string address,
        out int port,
        out string name,
        out string error)
    {
        address = string.Empty;
        port = 0;
        name = "原生订阅节点";
        error = "原生订阅 URL 无效。";
        var candidate = value.Trim();
        var marker = candidate.IndexOf("://", StringComparison.Ordinal);
        if (marker < 0)
        {
            error = "原生订阅 URL 缺少协议。";
            return false;
        }

        var scheme = candidate[..marker].ToLowerInvariant();
        if (scheme == "vmess")
        {
            var encoded = candidate[(marker + 3)..].Trim().Trim('/');
            var fragmentMarker = encoded.IndexOf('#');
            if (fragmentMarker >= 0) encoded = encoded[..fragmentMarker];
            JsonObject? objectNode = null;
            try
            {
                if (TryDecodeBase64(encoded, out var json)) objectNode = JsonNode.Parse(json)?.AsObject();
            }
            catch (JsonException) { }
            if (objectNode is null)
            {
                error = "VMess 节点内容不是有效的 Base64 JSON。";
                return false;
            }

            address = ReadString(objectNode, "add");
            port = ReadPort(objectNode["port"]);
            name = ReadString(objectNode, "ps");
            if (string.IsNullOrWhiteSpace(name)) name = address;
            return ValidateEndpoint(address, port, out error);
        }

        if (scheme == "ss" || scheme == "shadowsocks")
        {
            if (!TryParseSs(candidate, marker, out address, out port, out name))
            {
                error = "SS 节点格式无效。";
                return false;
            }
            return ValidateEndpoint(address, port, out error);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Host.Length == 0 || (uri.Port is < 1 or > 65535 && scheme is not ("hysteria2" or "hy2")))
        {
            error = "节点必须包含有效主机和端口。";
            return false;
        }

        address = uri.Host;
        port = uri.Port < 0 && scheme is ("hysteria2" or "hy2") ? 443 : uri.Port;
        var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        name = string.IsNullOrWhiteSpace(fragment) ? address : fragment;
        return ValidateEndpoint(address, port, out error);
    }

    private static bool TryParseSs(string value, int marker, out string address, out int port, out string name)
    {
        address = string.Empty;
        port = 0;
        name = "SS 节点";
        var payload = value[(marker + 3)..];
        var hash = payload.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(payload[(hash + 1)..]);
            payload = payload[..hash];
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0)
        {
            address = uri.Host;
            port = uri.Port;
            return true;
        }

        if (!TryDecodeBase64(payload.Trim('/'), out var decoded)) return false;
        var at = decoded.LastIndexOf('@');
        if (at < 0) return false;
        var endpoint = decoded[(at + 1)..];
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], out port)) return false;
        address = endpoint[..colon].Trim('[', ']');
        return true;
    }

    private static bool ValidateEndpoint(string address, int port, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl) || address.Contains('/'))
        {
            error = "节点地址无效。";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "节点端口必须在 1-65535。";
            return false;
        }
        return true;
    }

    internal static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        var normalized = value.Replace("-", "+", StringComparison.Ordinal)
            .Replace("_", "/", StringComparison.Ordinal)
            .Trim();
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ReadString(JsonObject obj, string key) => obj[key]?.GetValue<string>()?.Trim() ?? string.Empty;

    private static int ReadPort(JsonNode? value)
    {
        if (value is null) return 0;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var integer)) return integer;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }
}

/// <summary>
/// Starts one loopback-only Xray HTTP inbound for each native subscription node.
/// Xray is never downloaded or accepted from an untrusted URL; only an explicit
/// setting, PATH, an executable beside the manager, or an already-running xray is used.
/// </summary>
internal sealed class ProxyCoreService : IDisposable
{
    private readonly string _rootPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningCore> _running = new(StringComparer.OrdinalIgnoreCase);

    public ProxyCoreService(string rootPath) => _rootPath = Path.GetFullPath(rootPath);

    public bool TryEnsureLocalProxy(ProxyNodeRecord node, out Uri localProxy, out string error)
    {
        localProxy = null!;
        error = string.Empty;
        if (!node.IsNativeProtocol)
        {
            error = "该节点不需要原生协议核心。";
            return false;
        }
        var hasNativeOutbound = !string.IsNullOrWhiteSpace(node.EncryptedNativeOutbound);
        var readable = hasNativeOutbound
            ? ProxyNodeStore.TryUnprotect(node.EncryptedNativeOutbound, out var nativeUri)
            : node.TryGetNativeUri(out nativeUri);
        if (!readable)
        {
            error = "原生节点凭据无法解密，已安全拒绝请求。";
            return false;
        }

        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(nativeUri)))[..24];
        lock (_gate)
        {
            var requiresSingBox = hasNativeOutbound || node.EffectiveScheme.ToLowerInvariant() is "hysteria2" or "hy2";
            var preferSingBox = requiresSingBox || RequiresInsecureTls(nativeUri);
            if (_running.TryGetValue(node.NodeId, out var existing) &&
                existing.Fingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                (!preferSingBox || existing.UsesSingBox) &&
                !existing.Process.HasExited)
            {
                localProxy = new Uri($"http://127.0.0.1:{existing.Port}");
                return true;
            }
            StopCore(existing);
            _running.Remove(node.NodeId);

            if (!TryFindCorePath(preferSingBox, out var corePath, out var usesSingBox))
            {
                error = "未找到 Xray/sing-box 核心。请在设置中指定 ProxyCorePath，或将 xray 放入 PATH。";
                return false;
            }
            if (requiresSingBox && !usesSingBox)
            {
                error = "Hysteria2 需要 sing-box 核心，请设置 ProxyCorePath 为 sing-box.exe。";
                return false;
            }
            JsonObject? outbound = null;
            JsonObject? singBoxConfig = null;
            if (usesSingBox)
            {
                if (hasNativeOutbound)
                {
                    try
                    {
                        var native = JsonNode.Parse(nativeUri)?.AsObject();
                        if (native == null || native["type"]?.ToString() != node.EffectiveScheme ||
                            string.IsNullOrWhiteSpace(native["server"]?.ToString()) ||
                            Int(native, "server_port") is < 1 or > 65535 || native["detour"] != null)
                        { error = "独立节点的原生配置无效。"; return false; }
                        native["tag"] = "proxy";
                        singBoxConfig = new JsonObject
                        {
                            ["log"] = new JsonObject { ["level"] = "error" },
                            ["outbounds"] = new JsonArray { native },
                            ["route"] = new JsonObject { ["final"] = "proxy" }
                        };
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                    { error = "独立节点的原生配置无法读取。"; return false; }
                }
                else if (!TryBuildSingBoxConfig(node.EffectiveScheme, nativeUri, out singBoxConfig, out error)) return false;
            }
            else if (!TryBuildOutbound(node.EffectiveScheme, nativeUri, out outbound, out error)) return false;
            var port = GetFreePort();
            var runtimeDir = Path.Combine(_rootPath, ".proxy-runtime");
            Directory.CreateDirectory(runtimeDir);
            var configPath = Path.Combine(runtimeDir, Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var config = usesSingBox
                    ? AddSingBoxInbound(singBoxConfig!, port)
                    : new JsonObject
                    {
                        ["log"] = new JsonObject { ["loglevel"] = "none" },
                        ["inbounds"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["listen"] = "127.0.0.1",
                                ["port"] = port,
                                ["protocol"] = "http",
                                ["settings"] = new JsonObject()
                            }
                        },
                        ["outbounds"] = new JsonArray { outbound }
                    };
                var configJson = config.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                if (!usesSingBox) File.WriteAllText(configPath, configJson, new UTF8Encoding(false));
                var psi = new ProcessStartInfo(corePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = usesSingBox,
                    WorkingDirectory = Path.GetDirectoryName(corePath) ?? AppContext.BaseDirectory
                };
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(usesSingBox ? "stdin" : configPath);
                var process = Process.Start(psi);
                if (process is null)
                {
                    error = "代理核心启动失败。";
                    return false;
                }
                _ = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (usesSingBox)
                {
                    process.StandardInput.Write(configJson);
                    process.StandardInput.Close();
                }
                // A dead subscription should fail quickly in the UI. The gateway
                // has its own resolver/core instance, so this only shortens an
                // interactive node probe and never changes the rotation path.
                if (!WaitForPort(port, TimeSpan.FromSeconds(4)))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    error = "代理核心未在本地回环端口启动。";
                    return false;
                }
                try { File.Delete(configPath); } catch { }
                _running[node.NodeId] = new RunningCore(process, port, fingerprint, usesSingBox);
                localProxy = new Uri($"http://127.0.0.1:{port}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = "原生代理核心启动失败。";
                try { File.Delete(configPath); } catch { }
                return false;
            }
            finally
            {
                try { File.Delete(configPath); } catch { }
            }
        }
    }

    private bool TryFindCorePath(bool preferSingBox, out string path, out bool usesSingBox)
    {
        var settings = new ThemeService(_rootPath).LoadSettings();
        var configured = settings.ProxyCorePath?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
                if (File.Exists(expanded))
                {
                    path = expanded;
                    usesSingBox = IsSingBoxPath(path);
                    return true;
                }
            }
            catch { }
        }

        var candidates = new List<string>();
        var environment = Environment.GetEnvironmentVariable("CODEX_PROXY_CORE");
        if (!string.IsNullOrWhiteSpace(environment)) candidates.Add(environment.Trim());
        if (preferSingBox)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "sing-box.exe"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "cores", "sing-box.exe"));
            candidates.Add(Path.Combine(_rootPath, "sing-box.exe"));
            try
            {
                foreach (var process in Process.GetProcessesByName("sing-box"))
                {
                    try { if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName)) candidates.Add(process.MainModule.FileName); }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "xray.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "cores", "xray.exe"));
        candidates.Add(Path.Combine(_rootPath, "xray.exe"));
        try
        {
            foreach (var process in Process.GetProcessesByName("xray"))
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(runningPath))
                    {
                        if (preferSingBox)
                        {
                            var xrayDirectory = Path.GetDirectoryName(runningPath);
                            var binDirectory = string.IsNullOrWhiteSpace(xrayDirectory) ? null : Directory.GetParent(xrayDirectory)?.FullName;
                            if (!string.IsNullOrWhiteSpace(binDirectory))
                            {
                                candidates.Add(Path.Combine(binDirectory, "sing_box", "sing-box.exe"));
                                candidates.Add(Path.Combine(binDirectory, "sing-box", "sing-box.exe"));
                            }
                        }
                        candidates.Add(runningPath);
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }
        var pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box"));
            candidates.Add(Path.Combine(dir, OperatingSystem.IsWindows() ? "xray.exe" : "xray"));
        }
        path = candidates.Select(candidate =>
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)); } catch { return string.Empty; }
        }).FirstOrDefault(File.Exists) ?? string.Empty;
        usesSingBox = IsSingBoxPath(path);
        return path.Length > 0;
    }

    private static bool IsSingBoxPath(string path) =>
        Path.GetFileNameWithoutExtension(path).Equals("sing-box", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(path).Equals("singbox", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresInsecureTls(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        var query = ParseQuery(uri.Query);
        return query.TryGetValue("allowInsecure", out var value) && IsTruthy(value) &&
            !query.GetValueOrDefault("security", "tls").Equals("reality", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string value) => value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException) { Thread.Sleep(80); }
        }
        return false;
    }

    private static bool TryBuildOutbound(string scheme, string raw, out JsonObject outbound, out string error)
    {
        outbound = new JsonObject();
        error = string.Empty;
        if (scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase))
        {
            var marker = raw.IndexOf("://", StringComparison.Ordinal);
            var encoded = marker >= 0 ? raw[(marker + 3)..].Trim('/') : string.Empty;
            var fragmentMarker = encoded.IndexOf('#');
            if (fragmentMarker >= 0) encoded = encoded[..fragmentMarker];
            JsonObject? vmess = null;
            try
            {
                if (marker >= 0 && ProxyNativeUriParser.TryDecodeBase64(encoded, out var json)) vmess = JsonNode.Parse(json)?.AsObject();
            }
            catch (JsonException) { }
            if (vmess is null)
            {
                error = "VMess 配置解码失败。";
                return false;
            }
            var address = Read(vmess, "add");
            var port = Int(vmess, "port");
            var id = Read(vmess, "id");
            if (address.Length == 0 || port is < 1 or > 65535 || id.Length == 0) { error = "VMess 配置缺少地址、端口或 UUID。"; return false; }
            outbound = new JsonObject
            {
                ["protocol"] = "vmess",
                ["settings"] = new JsonObject { ["vnext"] = new JsonArray { new JsonObject { ["address"] = address, ["port"] = port, ["users"] = new JsonArray { new JsonObject { ["id"] = id, ["alterId"] = Int(vmess, "aid"), ["security"] = Read(vmess, "scy") is { Length: > 0 } vmessSecurity ? vmessSecurity : "auto" } } } } },
                ["streamSettings"] = BuildStreamSettings(Read(vmess, "net"), Read(vmess, "tls"), Read(vmess, "sni"), Read(vmess, "host"), Read(vmess, "path"), "", Read(vmess, "pbk"), Read(vmess, "sid"), Read(vmess, "fp"), Read(vmess, "spx"), Read(vmess, "alpn"))
            };
            return true;
        }

        if (scheme is "ss" or "shadowsocks")
        {
            if (!TryParseShadowsocks(raw, out var ssAddress, out var ssPort, out var ssMethod, out var ssPassword, out error)) return false;
            outbound = new JsonObject
            {
                ["protocol"] = "shadowsocks",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["address"] = ssAddress, ["port"] = ssPort, ["method"] = ssMethod, ["password"] = ssPassword } } }
            };
            return true;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port <= 0)
        {
            error = "原生节点 URL 无法解析。";
            return false;
        }
        var userInfo = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty);
        var secret = userInfo;
        var user = userInfo;
        var colon = userInfo.IndexOf(':');
        if (colon >= 0) { user = userInfo[..colon]; secret = userInfo[(colon + 1)..]; }
        var query = ParseQuery(uri.Query);
        var network = query.GetValueOrDefault("type", "tcp");
        var security = query.GetValueOrDefault("security", query.GetValueOrDefault("tls", scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase) ? "tls" : "none"));
        var sni = query.GetValueOrDefault("sni", query.GetValueOrDefault("peer", query.GetValueOrDefault("host", uri.Host)));
        var stream = BuildStreamSettings(network, security, sni, query.GetValueOrDefault("host", ""), query.GetValueOrDefault("path", "/"), query.GetValueOrDefault("serviceName", ""), query.GetValueOrDefault("pbk", ""), query.GetValueOrDefault("sid", ""), query.GetValueOrDefault("fp", ""), query.GetValueOrDefault("spx", ""), query.GetValueOrDefault("alpn", ""));

        if (scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
        {
            if (user.Length == 0) { error = "VLESS URL 缺少 UUID。"; return false; }
            outbound = new JsonObject { ["protocol"] = "vless", ["settings"] = new JsonObject { ["vnext"] = new JsonArray { new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port, ["users"] = new JsonArray { new JsonObject { ["id"] = user, ["encryption"] = "none", ["flow"] = query.GetValueOrDefault("flow", "") } } } } }, ["streamSettings"] = stream };
            return true;
        }
        if (scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase))
        {
            if (secret.Length == 0) { error = "Trojan URL 缺少密码。"; return false; }
            outbound = new JsonObject { ["protocol"] = "trojan", ["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port, ["password"] = secret } } }, ["streamSettings"] = stream };
            return true;
        }
        error = "原生协议不受支持。";
        return false;
    }

    private static JsonObject AddSingBoxInbound(JsonObject config, int port)
    {
        config["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "http",
                ["tag"] = "in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = port
            }
        };
        return config;
    }

    private static bool TryBuildSingBoxConfig(string scheme, string raw, out JsonObject config, out string error)
    {
        config = new JsonObject();
        error = string.Empty;
        JsonObject outbound;
        if (scheme.ToLowerInvariant() is "hysteria2" or "hy2")
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var hy) ||
                string.IsNullOrWhiteSpace(hy.Host) || string.IsNullOrWhiteSpace(hy.UserInfo))
            {
                error = "Hysteria2 节点缺少地址或密码。";
                return false;
            }
            var query = ParseQuery(hy.Query);
            if (query.Keys.Any(key => key is "pinSHA256" or "mport"))
            {
                error = "当前不支持 Hysteria2 证书指纹或端口跳跃参数；未忽略配置。";
                return false;
            }
            outbound = new JsonObject
            {
                ["type"] = "hysteria2", ["tag"] = "proxy", ["server"] = hy.Host,
                ["server_port"] = hy.Port < 0 ? 443 : hy.Port,
                ["password"] = Uri.UnescapeDataString(hy.UserInfo)
            };
            var obfs = query.GetValueOrDefault("obfs", "");
            if (obfs.Length > 0)
            {
                if (obfs != "salamander" || string.IsNullOrEmpty(query.GetValueOrDefault("obfs-password")))
                {
                    error = "Hysteria2 混淆配置无效。";
                    return false;
                }
                outbound["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = query["obfs-password"] };
            }
            AddSingBoxTls(outbound, "tls", query.GetValueOrDefault("sni", hy.Host), "", "", "",
                query.GetValueOrDefault("alpn", ""),
                IsTruthy(query.GetValueOrDefault("insecure", query.GetValueOrDefault("allowInsecure", ""))));
        }
        else if (scheme.Equals("ss", StringComparison.OrdinalIgnoreCase) || scheme.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseShadowsocks(raw, out var ssAddress, out var ssPort, out var ssMethod, out var ssPassword, out error)) return false;
            outbound = new JsonObject
            {
                ["type"] = "shadowsocks",
                ["tag"] = "proxy",
                ["server"] = ssAddress,
                ["server_port"] = ssPort,
                ["method"] = ssMethod,
                ["password"] = ssPassword
            };
        }
        else if (scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase))
        {
            var marker = raw.IndexOf("://", StringComparison.Ordinal);
            var encoded = marker >= 0 ? raw[(marker + 3)..].Trim('/') : string.Empty;
            var fragmentMarker = encoded.IndexOf('#');
            if (fragmentMarker >= 0) encoded = encoded[..fragmentMarker];
            JsonObject? vmess = null;
            try
            {
                if (ProxyNativeUriParser.TryDecodeBase64(encoded, out var json)) vmess = JsonNode.Parse(json)?.AsObject();
            }
            catch (JsonException) { }
            if (vmess is null) { error = "VMess 配置解码失败。"; return false; }
            var address = Read(vmess, "add");
            var port = Int(vmess, "port");
            var id = Read(vmess, "id");
            if (address.Length == 0 || port is < 1 or > 65535 || id.Length == 0) { error = "VMess 配置缺少地址、端口或 UUID。"; return false; }
            outbound = new JsonObject
            {
                ["type"] = "vmess",
                ["tag"] = "proxy",
                ["server"] = address,
                ["server_port"] = port,
                ["uuid"] = id,
                ["security"] = Read(vmess, "scy") is { Length: > 0 } vmessSecurity ? vmessSecurity : "auto",
                ["alter_id"] = Int(vmess, "aid")
            };
            AddSingBoxTls(outbound, Read(vmess, "tls"), Read(vmess, "sni"), "", "", "", Read(vmess, "alpn"), false);
            AddSingBoxTransport(outbound, Read(vmess, "net"), Read(vmess, "host"), Read(vmess, "path"), "");
        }
        else
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port <= 0)
            {
                error = "原生节点 URL 无法解析。";
                return false;
            }
            var userInfo = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty);
            var secret = userInfo;
            var user = userInfo;
            var colon = userInfo.IndexOf(':');
            if (colon >= 0) { user = userInfo[..colon]; secret = userInfo[(colon + 1)..]; }
            var query = ParseQuery(uri.Query);
            var network = query.GetValueOrDefault("type", "tcp");
            var security = query.GetValueOrDefault("security", query.GetValueOrDefault("tls", scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase) ? "tls" : "none"));
            var sni = query.GetValueOrDefault("sni", query.GetValueOrDefault("peer", query.GetValueOrDefault("host", uri.Host)));
            if (scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase))
            {
                if (secret.Length == 0) { error = "Trojan URL 缺少密码。"; return false; }
                outbound = new JsonObject { ["type"] = "trojan", ["tag"] = "proxy", ["server"] = uri.Host, ["server_port"] = uri.Port, ["password"] = secret };
            }
            else if (scheme.Equals("vless", StringComparison.OrdinalIgnoreCase))
            {
                if (user.Length == 0) { error = "VLESS URL 缺少 UUID。"; return false; }
                outbound = new JsonObject { ["type"] = "vless", ["tag"] = "proxy", ["server"] = uri.Host, ["server_port"] = uri.Port, ["uuid"] = user };
                var flow = query.GetValueOrDefault("flow", "");
                if (!string.IsNullOrWhiteSpace(flow)) outbound["flow"] = flow;
            }
            else { error = "原生协议不受支持。"; return false; }
            var insecure = IsTruthy(query.GetValueOrDefault("allowInsecure", ""));
            AddSingBoxTls(outbound, security, sni, query.GetValueOrDefault("pbk", ""), query.GetValueOrDefault("sid", ""), query.GetValueOrDefault("fp", ""), query.GetValueOrDefault("alpn", ""), insecure);
            AddSingBoxTransport(outbound, network, query.GetValueOrDefault("host", ""), query.GetValueOrDefault("path", "/"), query.GetValueOrDefault("serviceName", ""));
        }

        config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["outbounds"] = new JsonArray { outbound, new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
            ["route"] = new JsonObject { ["final"] = "proxy" }
        };
        return true;
    }

    private static void AddSingBoxTls(JsonObject outbound, string security, string sni, string publicKey, string shortId, string fingerprint, string alpn, bool insecure)
    {
        if (string.IsNullOrWhiteSpace(security) || security.Equals("none", StringComparison.OrdinalIgnoreCase) || security.Equals("false", StringComparison.OrdinalIgnoreCase)) return;
        var tls = new JsonObject { ["enabled"] = true };
        if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;
        if (insecure) tls["insecure"] = true;
        if (!string.IsNullOrWhiteSpace(alpn)) tls["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            var reality = new JsonObject();
            if (!string.IsNullOrWhiteSpace(publicKey)) reality["public_key"] = publicKey;
            if (!string.IsNullOrWhiteSpace(shortId)) reality["short_id"] = shortId;
            if (reality.Count > 0) tls["reality"] = reality;
            if (!string.IsNullOrWhiteSpace(fingerprint)) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
        }
        outbound["tls"] = tls;
    }

    private static void AddSingBoxTransport(JsonObject outbound, string network, string host, string path, string serviceName)
    {
        if (network.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            var ws = new JsonObject { ["type"] = "ws", ["path"] = string.IsNullOrWhiteSpace(path) ? "/" : path };
            if (!string.IsNullOrWhiteSpace(host)) ws["headers"] = new JsonObject { ["Host"] = host };
            outbound["transport"] = ws;
        }
        else if (network.Equals("grpc", StringComparison.OrdinalIgnoreCase))
        {
            outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = serviceName };
        }
    }

    private static bool TryParseShadowsocks(string raw, out string address, out int port, out string method, out string password, out string error)
    {
        address = method = password = string.Empty;
        port = 0;
        error = "SS URL 格式无效。";
        var marker = raw.IndexOf("://", StringComparison.Ordinal);
        if (marker < 0) return false;
        var payload = raw[(marker + 3)..];
        var hash = payload.IndexOf('#');
        if (hash >= 0) payload = payload[..hash];
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0)
        {
            address = uri.Host;
            port = uri.Port;
            var userInfo = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty);
            if (userInfo.IndexOf(':') < 0 && ProxyNativeUriParser.TryDecodeBase64(userInfo, out var decoded)) userInfo = decoded;
            var colon = userInfo.IndexOf(':');
            if (colon > 0) { method = userInfo[..colon]; password = userInfo[(colon + 1)..]; }
        }
        else if (ProxyNativeUriParser.TryDecodeBase64(payload.Trim('/'), out var decoded))
        {
            var at = decoded.LastIndexOf('@');
            var colon = decoded.LastIndexOf(':');
            if (at <= 0 || colon <= at) return false;
            var credentials = decoded[..at];
            var credentialColon = credentials.IndexOf(':');
            if (credentialColon <= 0) return false;
            method = credentials[..credentialColon]; password = credentials[(credentialColon + 1)..];
            address = decoded[(at + 1)..colon].Trim('[', ']');
            if (!int.TryParse(decoded[(colon + 1)..], out port)) return false;
        }
        if (string.IsNullOrWhiteSpace(address) || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(method) || password.Length == 0)
        {
            error = "SS URL 缺少有效地址、端口或 method:password。";
            return false;
        }
        return true;
    }

    private static JsonObject BuildStreamSettings(string network, string security, string sni, string host, string path, string serviceName, string publicKey = "", string shortId = "", string fingerprint = "", string spiderX = "", string alpn = "")
    {
        var stream = new JsonObject { ["network"] = string.IsNullOrWhiteSpace(network) ? "tcp" : network };
        if (!string.IsNullOrWhiteSpace(security) && !security.Equals("none", StringComparison.OrdinalIgnoreCase) && !security.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            stream["security"] = security.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "reality" : "tls";
            // Xray 26.x removed the legacy allowInsecure field. Omitting it
            // keeps the generated config valid and uses normal system roots.
            var settings = new JsonObject { ["serverName"] = sni };
            if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(publicKey)) settings["publicKey"] = publicKey;
                if (!string.IsNullOrWhiteSpace(shortId)) settings["shortId"] = shortId;
                if (!string.IsNullOrWhiteSpace(fingerprint)) settings["fingerprint"] = fingerprint;
                if (!string.IsNullOrWhiteSpace(spiderX)) settings["spiderX"] = spiderX;
            }
            else if (!string.IsNullOrWhiteSpace(alpn)) settings["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            stream[security.Equals("reality", StringComparison.OrdinalIgnoreCase) ? "realitySettings" : "tlsSettings"] = settings;
        }
        if (network.Equals("ws", StringComparison.OrdinalIgnoreCase)) stream["wsSettings"] = new JsonObject { ["path"] = string.IsNullOrWhiteSpace(path) ? "/" : path, ["headers"] = new JsonObject { ["Host"] = host } };
        if (network.Equals("grpc", StringComparison.OrdinalIgnoreCase)) stream["grpcSettings"] = new JsonObject { ["serviceName"] = serviceName };
        return stream;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            try
            {
                var key = Uri.UnescapeDataString(pieces[0]);
                if (key.Length == 0) continue;
                result[key] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            }
            catch (UriFormatException) { }
        }
        return result;
    }
    private static string Read(JsonObject obj, string key) => obj[key]?.ToString()?.Trim() ?? string.Empty;
    private static int Int(JsonObject obj, string key) => int.TryParse(Read(obj, key), out var value) ? value : 0;

    private void StopCore(RunningCore? core)
    {
        if (core is null) return;
        try { if (!core.Process.HasExited) core.Process.Kill(entireProcessTree: true); } catch { }
        core.Process.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var core in _running.Values) StopCore(core);
            _running.Clear();
        }
    }

    private sealed record RunningCore(Process Process, int Port, string Fingerprint, bool UsesSingBox);

    internal static void ValidateNativeNodeConfig()
    {
        const string uri = "hy2://example%3Apassword@192.0.2.10?sni=example.test&obfs=salamander&obfs-password=test";
        if (!ProxyNativeUriParser.TryGetMetadata(uri, out _, out var port, out _, out _) || port != 443 ||
            !TryBuildSingBoxConfig("hy2", uri, out var config, out _) ||
            config["outbounds"]?[0]?["password"]?.ToString() != "example:password" ||
            config["outbounds"]?[0]?["tls"]?["insecure"] != null ||
            config["outbounds"]?[0]?["obfs"]?["type"]?.ToString() != "salamander" ||
            TryBuildSingBoxConfig("hy2", uri + "&pinSHA256=unsupported", out _, out _))
            throw new InvalidOperationException("Hysteria2 configuration projection regression.");
    }
}
