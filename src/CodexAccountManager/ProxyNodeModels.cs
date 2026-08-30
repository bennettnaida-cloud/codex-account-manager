using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAccountManager;

public enum ProxyNodeProtocol { Http, Https, Socks5, Vless, Vmess, Trojan, Shadowsocks }
public enum ProxyBindingMode { InheritGlobal, FixedNode, Disabled }
public enum ProxyFallbackPolicy { FailClosed, InheritGlobal }

public sealed class ProxyNodeRecord
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "未命名节点";
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "http";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("encryptedPassword")] public string? EncryptedPassword { get; set; }
    // Native subscription protocols are kept encrypted until ProxyCoreService builds
    // an ephemeral Xray config.  The raw URI is never rendered or written to logs.
    [JsonPropertyName("encryptedNativeUri")] public string? EncryptedNativeUri { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("lastHealthTestAtUtc")] public DateTimeOffset? LastHealthTestAtUtc { get; set; }
    [JsonPropertyName("lastTcpMilliseconds")] public double? LastTcpMilliseconds { get; set; }
    [JsonPropertyName("lastTlsMilliseconds")] public double? LastTlsMilliseconds { get; set; }
    [JsonPropertyName("lastFirstResponseMilliseconds")] public double? LastFirstResponseMilliseconds { get; set; }
    [JsonPropertyName("lastExitIp")] public string? LastExitIp { get; set; }
    // The last IP is retained for change detection, while this timestamp lets the UI
    // distinguish a current observation from a stale value after a failed probe.
    [JsonPropertyName("lastExitIpObservedAtUtc")] public DateTimeOffset? LastExitIpObservedAtUtc { get; set; }
    [JsonPropertyName("exitIpChanged")] public bool ExitIpChanged { get; set; }
    // Human-readable, non-sensitive probe result used by the node list. It never
    // contains credentials or the full native subscription URI.
    [JsonPropertyName("lastHealthError")] public string? LastHealthError { get; set; }

    [JsonIgnore] public string DisplayUrl => $"{Scheme}://{Address}:{Port}";
    /// <summary>Compact endpoint for the node grid; never includes a scheme or credentials.</summary>
    [JsonIgnore] public string DisplayAddress =>
        Uri.CheckHostName(Address) == UriHostNameType.IPv6 ? $"[{Address}]:{Port}" : $"{Address}:{Port}";
    [JsonIgnore] public bool HasPassword => !string.IsNullOrWhiteSpace(EncryptedPassword);
    [JsonIgnore] public string? Password { get; set; }
    [JsonIgnore] public bool IsNativeProtocol => Scheme.Equals("vless", StringComparison.OrdinalIgnoreCase) ||
        Scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase) ||
        Scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase) ||
        Scheme.Equals("ss", StringComparison.OrdinalIgnoreCase) ||
        Scheme.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase);

    public void SetPassword(string? password)
    {
        EncryptedPassword = string.IsNullOrEmpty(password) ? null : ProxyNodeStore.Protect(password);
        Password = null;
    }

    public void SetNativeUri(string? uri)
    {
        EncryptedNativeUri = string.IsNullOrWhiteSpace(uri) ? null : ProxyNodeStore.Protect(uri.Trim());
    }

    public bool TryGetNativeUri(out string uri) => ProxyNodeStore.TryUnprotect(EncryptedNativeUri, out uri);

    public bool TryGetProtocol(out ProxyNodeProtocol protocol) =>
        Enum.TryParse(Scheme.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase) ? "ss" : Scheme, true, out protocol) &&
        protocol is ProxyNodeProtocol.Http or ProxyNodeProtocol.Https or ProxyNodeProtocol.Socks5 or
            ProxyNodeProtocol.Vless or ProxyNodeProtocol.Vmess or ProxyNodeProtocol.Trojan or ProxyNodeProtocol.Shadowsocks;

    public bool TryValidate(out string error)
    {
        error = "";
        if (!TryGetProtocol(out _)) { error = "协议必须是 HTTP、HTTPS、SOCKS5、VLESS、VMess、Trojan 或 SS。"; return false; }
        if (string.IsNullOrWhiteSpace(Address) || Address.Any(char.IsControl) || Address.Contains('/')) { error = "节点地址无效。"; return false; }
        if (Port is < 1 or > 65535) { error = "端口必须在 1-65535。"; return false; }
        if (IsNativeProtocol && string.IsNullOrWhiteSpace(EncryptedNativeUri)) { error = "原生协议缺少受保护的订阅配置。"; return false; }
        return true;
    }

    public Uri BuildUri()
    {
        if (!TryValidate(out var error)) throw new InvalidDataException(error);
        if (IsNativeProtocol) throw new InvalidOperationException("原生订阅节点必须先由 Xray/sing-box 转换为本地回环代理。");
        return new Uri($"{Scheme.ToLowerInvariant()}://{Address}:{Port}", UriKind.Absolute);
    }
}

public sealed class AccountProxyBinding
{
    [JsonPropertyName("accountKey")] public string AccountKey { get; set; } = "";
    [JsonPropertyName("mode")] public ProxyBindingMode Mode { get; set; } = ProxyBindingMode.InheritGlobal;
    [JsonPropertyName("nodeId")] public string? NodeId { get; set; }
    [JsonPropertyName("fallbackPolicy")] public ProxyFallbackPolicy FallbackPolicy { get; set; } = ProxyFallbackPolicy.FailClosed;
    [JsonPropertyName("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProxyImportInvalidEntry
{
    public int LineNumber { get; init; }
    public string Input { get; init; } = "";
    public string Reason { get; init; } = "格式无效。";
}
public sealed class ProxyImportPreview
{
    public IReadOnlyList<ProxyNodeRecord> Nodes { get; init; } = [];
    public IReadOnlyList<ProxyImportInvalidEntry> InvalidEntries { get; init; } = [];
    public int DuplicateCount { get; init; }
}

internal sealed class ProxyNodesDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProxyNodeRecord> Nodes { get; set; } = [];
}
internal sealed class ProxyBindingsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<AccountProxyBinding> Bindings { get; set; } = [];
}

public sealed class ProxyNodeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public const int CurrentSchemaVersion = 1;
    public string RootPath { get; }
    public string NodesPath => Path.Combine(RootPath, "proxy-nodes.json");
    public string BindingsPath => Path.Combine(RootPath, "account-proxy-bindings.json");
    public bool NodesLoadFailed { get; private set; }
    public bool BindingsLoadFailed { get; private set; }
    public ProxyNodeStore() : this(new AccountStore().RootPath) { }
    public ProxyNodeStore(string rootPath) => RootPath = Path.GetFullPath(rootPath);

    public List<ProxyNodeRecord> LoadNodes()
    {
        try
        {
            if (!File.Exists(NodesPath)) return [];
            var doc = JsonSerializer.Deserialize<ProxyNodesDocument>(File.ReadAllText(NodesPath));
            return doc?.Nodes?.Where(n => n != null && !string.IsNullOrWhiteSpace(n.NodeId)).ToList() ?? [];
        }
        catch { NodesLoadFailed = true; return []; }
    }
    public List<AccountProxyBinding> LoadBindings()
    {
        try
        {
            if (!File.Exists(BindingsPath)) return [];
            var doc = JsonSerializer.Deserialize<ProxyBindingsDocument>(File.ReadAllText(BindingsPath));
            return doc?.Bindings?.Where(b => !string.IsNullOrWhiteSpace(b.AccountKey)).ToList() ?? [];
        }
        catch { BindingsLoadFailed = true; return []; }
    }
    public void SaveNodes(IEnumerable<ProxyNodeRecord> nodes) => AtomicWrite(NodesPath, new ProxyNodesDocument { Nodes = nodes.ToList() });
    public void SaveBindings(IEnumerable<AccountProxyBinding> bindings) => AtomicWrite(BindingsPath, new ProxyBindingsDocument { Bindings = bindings.ToList() });
    public void SetBinding(AccountProxyBinding binding)
    {
        var list = LoadBindings();
        list.RemoveAll(x => x.AccountKey.Equals(binding.AccountKey, StringComparison.Ordinal));
        binding.UpdatedAtUtc = DateTimeOffset.UtcNow;
        list.Add(binding);
        SaveBindings(list);
    }
    public AccountProxyBinding? GetBinding(string accountKey) => LoadBindings().FirstOrDefault(x => x.AccountKey.Equals(accountKey, StringComparison.Ordinal));
    public void RemoveBinding(string accountKey) => SaveBindings(LoadBindings().Where(x => !x.AccountKey.Equals(accountKey, StringComparison.Ordinal)));
    public void SetNode(ProxyNodeRecord node)
    {
        if (!node.TryValidate(out var error)) throw new InvalidDataException(error);
        var list = LoadNodes();
        list.RemoveAll(x => x.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase));
        list.Add(node);
        SaveNodes(list);
    }
    internal void MarkRuntimeFailure(string nodeId, string reason)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        var list = LoadNodes();
        var node = list.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (node == null) return;
        node.LastHealthTestAtUtc = DateTimeOffset.UtcNow;
        node.LastTcpMilliseconds = null;
        node.LastTlsMilliseconds = null;
        node.LastFirstResponseMilliseconds = null;
        node.LastHealthError = string.IsNullOrWhiteSpace(reason)
            ? "运行时请求失败。"
            : reason;
        // Keep LastExitIp for historical change detection, but LastHealthError makes
        // the sidebar render it as “上次 …” rather than claiming it is current.
        SaveNodes(list);
    }
    public void RemoveNode(string nodeId)
    {
        SaveNodes(LoadNodes().Where(x => !x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)));
        SaveBindings(LoadBindings().Select(binding => binding.NodeId?.Equals(nodeId, StringComparison.OrdinalIgnoreCase) == true
            ? new AccountProxyBinding { AccountKey = binding.AccountKey, Mode = ProxyBindingMode.InheritGlobal, FallbackPolicy = binding.FallbackPolicy }
            : binding));
    }
    public ProxyImportPreview PreviewImport(string? text)
    {
        var nodes = new List<ProxyNodeRecord>();
        var invalid = new List<ProxyImportInvalidEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicate = 0;
        var lines = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!TryParse(line, out var node, out var error)) { invalid.Add(new() { LineNumber = i + 1, Input = Mask(line), Reason = error }); continue; }
            var key = DedupKey(node);
            if (!seen.Add(key)) { duplicate++; continue; }
            nodes.Add(node);
        }
        return new ProxyImportPreview { Nodes = nodes, InvalidEntries = invalid, DuplicateCount = duplicate };
    }
    public int ImportText(string? text)
    {
        var preview = PreviewImport(text);
        var existing = LoadNodes();
        var keys = new HashSet<string>(existing.Select(DedupKey), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var node in preview.Nodes)
        {
            if (keys.Add(DedupKey(node))) { existing.Add(node); added++; }
        }
        SaveNodes(existing);
        return added;
    }
    private static bool TryParse(string value, out ProxyNodeRecord node, out string error)
    {
        node = new ProxyNodeRecord(); error = "";
        var schemeMarker = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeMarker <= 0) { error = "无法解析代理 URL。"; return false; }
        var scheme = value[..schemeMarker].ToLowerInvariant();
        if (scheme is ("vless" or "vmess" or "trojan" or "ss" or "shadowsocks"))
        {
            if (!ProxyNativeUriParser.TryGetMetadata(value, out var nativeAddress, out var nativePort, out var nativeName, out error)) return false;
            node.Name = nativeName;
            node.Scheme = scheme == "shadowsocks" ? "ss" : scheme;
            node.Address = nativeAddress;
            node.Port = nativePort;
            node.SetNativeUri(value);
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) { error = "无法解析代理 URL。"; return false; }
        if (scheme is not ("http" or "https" or "socks5")) { error = "仅支持 http、https、socks5 URL；原生订阅协议需通过本地 Xray/sing-box 核心。"; return false; }
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535) { error = "地址或端口无效。"; return false; }
        node.Name = uri.Host + ":" + uri.Port;
        node.Scheme = uri.Scheme.ToLowerInvariant(); node.Address = uri.Host; node.Port = uri.Port;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            node.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2) node.EncryptedPassword = Protect(Uri.UnescapeDataString(parts[1]));
        }
        return true;
    }
    private static string DedupKey(ProxyNodeRecord node) => node.IsNativeProtocol
        ? $"native|{node.Scheme}|{node.EncryptedNativeUri}"
        : $"{node.Scheme}|{node.Address}|{node.Port}|{node.Username}";

    private static string Mask(string value)
    {
        var marker = value.IndexOf("://", StringComparison.Ordinal);
        if (marker > 0)
        {
            var scheme = value[..marker];
            // Native URLs (VMess Base64 in particular) can contain UUIDs and
            // passwords even when URI parsing fails; never echo their payload.
            if (scheme.Equals("vmess", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("vless", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("trojan", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("ss", StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase))
                return scheme + "://***";
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            return $"{uri.Scheme}://***@{uri.Host}:{uri.Port}";
        return value.Length > 160 ? value[..160] + "…" : value;
    }
    internal static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    internal static bool TryUnprotect(string? value, out string password)
    {
        password = ""; if (string.IsNullOrWhiteSpace(value)) return true;
        try { password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); return true; } catch { return false; }
    }
    private static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false)); File.Move(tmp, path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-proxy-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProxyNodeStore(root);
            var preview = store.PreviewImport("http://user:pass@example.com:8080\nhttp://user:pass@example.com:8080\nsocks5://127.0.0.1:1080\nvless://bad");
            if (preview.Nodes.Count != 2 || preview.DuplicateCount != 1 || preview.InvalidEntries.Count != 1 || !preview.Nodes[0].HasPassword || !TryUnprotect(preview.Nodes[0].EncryptedPassword, out var pass) || pass != "pass")
                throw new InvalidOperationException("Proxy URL parsing/import validation failed.");
            var vmessPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":2,\"ps\":\"vmess-test\",\"add\":\"127.0.0.1\",\"port\":443,\"id\":\"00000000-0000-0000-0000-000000000001\",\"aid\":0,\"net\":\"tcp\",\"tls\":\"tls\"}"));
            var nativePreview = store.PreviewImport(
                "vless://00000000-0000-0000-0000-000000000002@127.0.0.1:443?encryption=none&security=tls&type=tcp#vless-test\n" +
                "vmess://" + vmessPayload + "\n" +
                "trojan://password@127.0.0.1:443?security=tls#trojan-test\n" +
                "ss://aes-128-gcm:password@127.0.0.1:8388#ss-test");
            if (nativePreview.Nodes.Count != 4 || nativePreview.InvalidEntries.Count != 0 || nativePreview.Nodes.Any(node => !node.IsNativeProtocol || !node.TryGetNativeUri(out _)))
                throw new InvalidOperationException("Native subscription protocol parsing failed.");
            store.SaveNodes(preview.Nodes);
            var account = new AccountRecord { Name = "proxy-self-test", CodexHome = Path.Combine(root, "home") };
            var key = AccountProxyResolver.AccountKeyFor(account);
            store.SetBinding(new AccountProxyBinding { AccountKey = key, Mode = ProxyBindingMode.Disabled });
            var settings = new AppSettings { PatGatewayProxyAddress = "127.0.0.1", PatGatewayProxyPort = 18080, PatGatewayProxyAutoDetect = false };
            new ThemeService(root).SaveSettings(settings);
            if (new AccountProxyResolver(root).Resolve(key).Success) throw new InvalidOperationException("Disabled account proxy must fail closed.");
            var accountB = new AccountRecord { Name = "proxy-self-test-b", CodexHome = Path.Combine(root, "home-b") };
            store.SetBinding(new AccountProxyBinding { AccountKey = key, Mode = ProxyBindingMode.FixedNode, NodeId = preview.Nodes[0].NodeId });
            store.SetBinding(new AccountProxyBinding { AccountKey = AccountProxyResolver.AccountKeyFor(accountB), Mode = ProxyBindingMode.FixedNode, NodeId = preview.Nodes[1].NodeId });
            var resolver = new AccountProxyResolver(root);
            var resolvedA = resolver.Resolve(key); var resolvedB = resolver.Resolve(AccountProxyResolver.AccountKeyFor(accountB));
            if (!resolvedA.Success || !resolvedB.Success || resolvedA.NodeId == resolvedB.NodeId || resolvedA.PoolKey == resolvedB.PoolKey) throw new InvalidOperationException("Per-account proxy routing was not isolated.");
            store.SetBinding(new AccountProxyBinding
            {
                AccountKey = key,
                Mode = ProxyBindingMode.FixedNode,
                NodeId = preview.Nodes[0].NodeId,
                FallbackPolicy = ProxyFallbackPolicy.InheritGlobal
            });
            var globalResolution = resolver.ResolveGlobal();
            if (!globalResolution.Success || globalResolution.NodeId != null ||
                !resolver.AllowsRuntimeGlobalFallback(key, preview.Nodes[0].NodeId) ||
                resolver.AllowsRuntimeGlobalFallback(key, preview.Nodes[1].NodeId))
                throw new InvalidOperationException("Account proxy runtime global fallback policy failed.");
            store.SetBinding(new AccountProxyBinding
            {
                AccountKey = key,
                Mode = ProxyBindingMode.FixedNode,
                NodeId = preview.Nodes[0].NodeId,
                FallbackPolicy = ProxyFallbackPolicy.FailClosed
            });
            if (resolver.AllowsRuntimeGlobalFallback(key, preview.Nodes[0].NodeId))
                throw new InvalidOperationException("Fail-closed account proxy unexpectedly allowed runtime fallback.");
            var runtimeNode = preview.Nodes[0];
            runtimeNode.LastExitIp = "198.51.100.10";
            runtimeNode.LastExitIpObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            runtimeNode.LastHealthTestAtUtc = runtimeNode.LastExitIpObservedAtUtc;
            store.SetNode(runtimeNode);
            resolver.MarkRuntimeFailure(runtimeNode.NodeId, "运行时请求失败，已回退全局代理。");
            var failedNode = store.LoadNodes().First(node => node.NodeId.Equals(runtimeNode.NodeId, StringComparison.OrdinalIgnoreCase));
            if (failedNode.LastHealthError == null || failedNode.LastExitIp != "198.51.100.10" ||
                failedNode.LastFirstResponseMilliseconds != null)
                throw new InvalidOperationException("Runtime proxy failure did not preserve historical IP safely.");
            store.RemoveBinding(key); if (!resolver.Resolve(key).Success || resolver.Resolve(key).NodeId != null) throw new InvalidOperationException("Unbound account did not inherit global proxy.");
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    }
}

public sealed record ProxyResolution(bool Success, Uri? ProxyUri, string? NodeId, string PoolKey, string Error)
{
    public static ProxyResolution Fail(string error) => new(false, null, null, "", error);
}

public sealed class AccountProxyResolver : IDisposable
{
    private readonly ProxyNodeStore _store;
    private readonly ThemeService _theme;
    private readonly ProxyCoreService _core;
    public AccountProxyResolver(string rootPath) { _store = new(rootPath); _theme = new(rootPath); _core = new(rootPath); }
    public ProxyResolution Resolve(string? accountKey)
    {
        var binding = string.IsNullOrWhiteSpace(accountKey) ? null : _store.GetBinding(accountKey);
        if (_store.BindingsLoadFailed && File.Exists(_store.BindingsPath)) return ProxyResolution.Fail("账号代理绑定配置损坏；为防止意外直连，网关已安全停止请求。");
        if (binding?.Mode == ProxyBindingMode.Disabled) return ProxyResolution.Fail("该账号已禁用代理，网关拒绝直连。");
        if (binding?.Mode == ProxyBindingMode.FixedNode)
        {
            if (_store.NodesLoadFailed && File.Exists(_store.NodesPath)) return ProxyResolution.Fail("代理节点配置损坏；为防止意外直连，网关已安全停止请求。");
            var node = _store.LoadNodes().FirstOrDefault(n => n.NodeId.Equals(binding.NodeId, StringComparison.OrdinalIgnoreCase));
            if (node != null && node.Enabled && node.TryValidate(out _))
            {
                var fixedResolution = Build(node);
                if (fixedResolution.Success || binding.FallbackPolicy == ProxyFallbackPolicy.FailClosed) return fixedResolution;
            }
            if (binding.FallbackPolicy == ProxyFallbackPolicy.FailClosed) return ProxyResolution.Fail("账号绑定的代理节点不存在、已禁用或配置无效。");
        }
        return ResolveGlobal();
    }
    public ProxyResolution ResolveGlobal()
    {
        var settings = _theme.LoadSettings();
        var explicitProxy = Environment.GetEnvironmentVariable("CODEX_PAT_GATEWAY_PROXY");
        var global = !string.IsNullOrWhiteSpace(explicitProxy)
            ? CodexCliService.NormalizeProxyServer(explicitProxy)
            : (!string.IsNullOrWhiteSpace(settings.PatGatewayProxy) ? settings.PatGatewayProxy : CodexCliService.BuildPatGatewayProxyUri(settings));
        if (!Uri.TryCreate(global, UriKind.Absolute, out var globalUri) || globalUri.Scheme is not ("http" or "https")) return ProxyResolution.Fail("未配置可用的全局代理；为防止意外直连，网关已停止请求。");
        return new(true, globalUri, null, "global:" + globalUri.AbsoluteUri, "");
    }
    public bool AllowsRuntimeGlobalFallback(string? accountKey, string? failedNodeId)
    {
        if (string.IsNullOrWhiteSpace(accountKey) || string.IsNullOrWhiteSpace(failedNodeId)) return false;
        var binding = _store.GetBinding(accountKey);
        if (_store.BindingsLoadFailed && File.Exists(_store.BindingsPath)) return false;
        return binding is
        {
            Mode: ProxyBindingMode.FixedNode,
            FallbackPolicy: ProxyFallbackPolicy.InheritGlobal
        } && binding.NodeId?.Equals(failedNodeId, StringComparison.OrdinalIgnoreCase) == true;
    }
    public ProxyNodeRecord? GetNode(string? nodeId) => string.IsNullOrWhiteSpace(nodeId)
        ? null
        : _store.LoadNodes().FirstOrDefault(n => n.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
    public void MarkRuntimeFailure(string? nodeId, string reason)
    {
        if (!string.IsNullOrWhiteSpace(nodeId)) _store.MarkRuntimeFailure(nodeId, reason);
    }
    public ProxyResolution ResolveByNode(ProxyNodeRecord node) => node.Enabled && node.TryValidate(out _)
        ? Build(node)
        : ProxyResolution.Fail("代理节点已禁用或配置无效。");
    /// <summary>Stops interactive native-core probes without touching the gateway's resolver.</summary>
    public void ResetNativeCores() => _core.Dispose();
    public static string AccountKeyFor(AccountRecord account) => QuotaAccountIdentity.CreateKey(account);
    private ProxyResolution Build(ProxyNodeRecord node)
    {
        if (!node.TryGetProtocol(out _)) return ProxyResolution.Fail("代理协议无效。");
        Uri uri;
        if (node.IsNativeProtocol)
        {
            if (!_core.TryEnsureLocalProxy(node, out uri, out var coreError)) return ProxyResolution.Fail(coreError);
        }
        else
        {
            uri = node.BuildUri();
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri + "|" + node.Username + "|" + node.EncryptedPassword)))[..16];
        return new(true, uri, node.NodeId, "node:" + node.NodeId + ":" + fingerprint, "");
    }

    public void Dispose() => _core.Dispose();
}

public static class ProxyHttpClientFactory
{
    public static HttpClient Create(ProxyResolution resolution, ProxyNodeRecord? node = null)
    {
        if (!resolution.Success || resolution.ProxyUri == null) throw new InvalidOperationException(resolution.Error);
        if (resolution.ProxyUri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase))
        {
            var handler = new SocketsHttpHandler { UseProxy = false, ConnectCallback = (context, cancellation) => Socks5ConnectAsync(context.DnsEndPoint, node, cancellation) };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        var web = new WebProxy(resolution.ProxyUri);
        if (node != null && !string.IsNullOrWhiteSpace(node.Username))
        {
            if (!ProxyNodeStore.TryUnprotect(node.EncryptedPassword, out var pass)) throw new InvalidOperationException("代理节点凭据无法解密，网关已安全停止请求。");
            web.Credentials = new NetworkCredential(node.Username, pass);
        }
        return new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = web, UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static async ValueTask<Stream> Socks5ConnectAsync(DnsEndPoint endpoint, ProxyNodeRecord? node, CancellationToken cancellation)
    {
        if (node == null) throw new InvalidOperationException("SOCKS5 节点配置缺失。");
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(node.Address, node.Port, cancellation);
        var stream = new NetworkStream(socket, ownsSocket: true);
        var wantsAuth = !string.IsNullOrWhiteSpace(node.Username);
        await stream.WriteAsync(wantsAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, cancellation);
        var hello = new byte[2]; await ReadExact(stream, hello, cancellation);
        if (hello[1] == 2)
        {
            if (!ProxyNodeStore.TryUnprotect(node.EncryptedPassword, out var password)) throw new IOException("SOCKS5 节点密码无法解密。");
            var user = Encoding.UTF8.GetBytes(node.Username ?? ""); var pass = Encoding.UTF8.GetBytes(password);
            if (user.Length > 255 || pass.Length > 255) throw new IOException("SOCKS5 用户名或密码过长。");
            await stream.WriteAsync(new[] { (byte)1, (byte)user.Length }.Concat(user).Concat(new[] { (byte)pass.Length }).Concat(pass).ToArray(), cancellation);
            var auth = new byte[2]; await ReadExact(stream, auth, cancellation); if (auth[1] != 0) throw new IOException("SOCKS5 用户名密码认证失败。");
        }
        else if (hello[1] != 0) throw new IOException("SOCKS5 节点不接受所需认证方式。");
        var host = Encoding.UTF8.GetBytes(endpoint.Host); var port = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)endpoint.Port));
        var request = new byte[7 + host.Length]; request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = (byte)host.Length; Buffer.BlockCopy(host, 0, request, 5, host.Length); Buffer.BlockCopy(port, 0, request, 5 + host.Length, 2);
        await stream.WriteAsync(request, cancellation); var head = new byte[4]; await ReadExact(stream, head, cancellation); if (head[1] != 0) throw new IOException("SOCKS5 连接被拒绝。");
        var length = head[3] switch { 1 => 4, 3 => (await ReadOne(stream, cancellation)), 4 => 16, _ => 0 }; var tail = new byte[length + 2]; await ReadExact(stream, tail, cancellation); return stream;
    }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token) { var offset = 0; while (offset < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(offset), token); if (n == 0) throw new EndOfStreamException(); offset += n; } }
    private static async Task<int> ReadOne(Stream stream, CancellationToken token) { var b = new byte[1]; await ReadExact(stream, b, token); return b[0]; }
}
