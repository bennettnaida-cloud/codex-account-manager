using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    internal static async Task ValidateCompanionCompatibilityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-companion-http-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        using var upstream = NewListener();
        using var gateway = NewListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        LocalPatGatewayHost? host = null;
        Task? upstreamTask = null;
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
            var account = new AccountRecord
            {
                Name = "unpatched-relay", AuthKind = AccountAuthKind.CompatibleApi,
                CodexHome = Path.Combine(root, "account"),
                ApiBaseUrl = upstream.Prefixes.Single().TrimEnd('/') + "/v1", ApiModel = "gpt-6-astra"
            };
            Directory.CreateDirectory(account.CodexHome);
            File.WriteAllText(Path.Combine(account.CodexHome, "auth.json"), "{\"OPENAI_API_KEY\":\"sk-companion-fixture-only\"}");
            new AccountStore(root).SaveAccounts([account]);
            new ThemeService(root).SaveSettings(new AppSettings { PatGatewayEnabled = false });
            try
            {
                await LocalPatGateway.EnsureReadyForDesktopLaunchAsync(deadline.Token);
                throw new Exception("Disabled compatibility gateway was silently accepted.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("关闭", StringComparison.Ordinal)) { }
            new ThemeService(root).SaveSettings(new AppSettings
            {
                AccountRotationEnabled = false, PatAutoRotationEnabled = false,
                PatGatewayProxyAutoDetect = false, PatGatewayProxy = null, PatGatewayProxyPort = null
            });
            var observedSuccesses = 0;
            upstreamTask = Task.Run(async () =>
            {
                while (!deadline.IsCancellationRequested)
                {
                    var context = await upstream.GetContextAsync().WaitAsync(deadline.Token);
                    using var reader = new StreamReader(context.Request.InputStream);
                    var text = await reader.ReadToEndAsync(deadline.Token);
                    using var document = JsonDocument.Parse(text);
                    var request = document.RootElement;
                    if (context.Request.Headers["Authorization"] != "Bearer sk-companion-fixture-only" ||
                        context.Request.Url?.AbsolutePath != "/v1/responses" ||
                        !string.IsNullOrEmpty(context.Request.Headers["Content-Encoding"]) ||
                        request.GetProperty("model").GetString() != "gpt-6-astra" ||
                        request.GetProperty("metadata").GetProperty("large").GetInt64() != 9007199254740993L)
                        throw new InvalidOperationException("Compatibility forwarding changed endpoint, credentials, model, encoding or metadata.");
                    var input = request.GetProperty("input").EnumerateArray().ToArray();
                    var orphan = input.Any(item => item.TryGetProperty("type", out var type) &&
                        type.GetString() == "function_call_output" && !item.TryGetProperty("call_id", out _));
                    if (orphan)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                            "{\"error\":{\"message\":\"function_call_output requires call_id on HTTP requests\"}}"), deadline.Token);
                    }
                    else
                    {
                        var isFollowup = input.Any(item => item.TryGetProperty("call_id", out var id) && id.GetString() == "call-companion");
                        if (isFollowup && (!input.Any(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call") ||
                            !input.Any(item => item.TryGetProperty("output", out var output) && output.GetString() == "COMPANION_OK")))
                            throw new InvalidOperationException("Tool follow-up lost its matching call or result.");
                        var outputJson = isFollowup
                            ? "[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"COMPANION_OK\"}]}]"
                            : "[{\"type\":\"function_call\",\"call_id\":\"call-companion\",\"name\":\"check\",\"arguments\":\"{}\"}]";
                        var response = "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_companion_fixture\",\"status\":\"completed\",\"model\":\"gpt-6-astra\",\"output\":" + outputJson + "}}";
                        context.Response.ContentType = "text/event-stream";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + response + "\n\n"), deadline.Token);
                        Interlocked.Increment(ref observedSuccesses);
                    }
                    context.Response.Close();
                }
            });
            host = new LocalPatGatewayHost("X-Companion-Fixture", "1", new Uri(gateway.Prefixes.Single()).Port);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var bootstrap = CodexEmptyCompanionSnapshotsTests.Request(
                new JsonObject { ["role"] = "user", ["content"] = "fixture" },
                CodexEmptyCompanionSnapshotsTests.EmptyOpen(), CodexEmptyCompanionSnapshotsTests.EmptySelected());
            // The unchanged request reproduces the original failure on this unpatched relay.
            using (var direct = new HttpRequestMessage(HttpMethod.Post, account.ApiBaseUrl + "/responses"))
            {
                direct.Headers.Authorization = new("Bearer", "sk-companion-fixture-only");
                direct.Content = new ByteArrayContent(bootstrap);
                using var result = await client.SendAsync(direct, deadline.Token);
                if (result.StatusCode != HttpStatusCode.BadRequest) throw new InvalidOperationException("Fixture did not reproduce the original error.");
            }
            foreach (var encoding in new[] { "identity", "gzip", "deflate", "br", "zstd" })
            {
                var first = await Send(bootstrap, encoding, HttpStatusCode.OK);
                if (!first.Contains("call-companion", StringComparison.Ordinal)) throw new InvalidOperationException("Bootstrap lost the tool call.");
                var followup = CodexEmptyCompanionSnapshotsTests.Request(
                    new JsonObject { ["role"] = "user", ["content"] = "continue" },
                    CodexEmptyCompanionSnapshotsTests.EmptyOpen(), CodexEmptyCompanionSnapshotsTests.EmptySelected(),
                    new JsonObject { ["type"] = "function_call", ["call_id"] = "call-companion", ["name"] = "check", ["arguments"] = "{}" },
                    new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call-companion", ["output"] = "COMPANION_OK" });
                var second = await Send(followup, encoding, HttpStatusCode.OK);
                if (!second.Contains("COMPANION_OK", StringComparison.Ordinal) || !second.Contains("response.completed", StringComparison.Ordinal))
                    throw new InvalidOperationException("Tool round trip did not finish.");
            }
            var realPage = CodexEmptyCompanionSnapshotsTests.EmptyOpen();
            realPage["output"] = "{\"page_id\":\"real-page\"}";
            await Send(CodexEmptyCompanionSnapshotsTests.Request(realPage), "identity", HttpStatusCode.BadRequest);
            await Send(bootstrap, "identity", HttpStatusCode.BadRequest, "other-client");
            if (observedSuccesses != 10) throw new InvalidOperationException("Unexpected successful upstream request count.");
            await ValidatePayloadTooLargeCompressionRecoveryAsync(deadline.Token);
            Console.WriteLine("PASS: unpatched HTTP relay rejects direct bootstrap; local gateway completes tool round trips for identity/gzip/deflate/br/zstd; real Page content and unknown clients remain intact; HTTP 413 compression retry and fallback verified");

            async Task<string> Send(byte[] bytes, string encoding, HttpStatusCode expected, string? agent = null)
            {
                var handle = Task.Run(async () => await host.HandleAsync(
                    await gateway.GetContextAsync().WaitAsync(deadline.Token), gateway, deadline));
                using var request = new HttpRequestMessage(HttpMethod.Post, gateway.Prefixes.Single() + "backend-api/codex/responses");
                request.Headers.Authorization = new("Bearer", "sk-companion-fixture-only");
                request.Headers.TryAddWithoutValidation("User-Agent", agent ?? CodexEmptyCompanionSnapshotsTests.DesktopUserAgent);
                request.Content = new ByteArrayContent(Encode(bytes, encoding));
                request.Content.Headers.ContentType = new("application/json");
                if (encoding != "identity") request.Content.Headers.ContentEncoding.Add(encoding);
                using var response = await client.SendAsync(request, deadline.Token);
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                await handle.WaitAsync(deadline.Token);
                if (response.StatusCode != expected) throw new InvalidOperationException($"Compatibility HTTP {encoding}: expected {expected}, got {response.StatusCode}: {body}");
                return body;
            }
        }
        finally
        {
            deadline.Cancel(); upstream.Stop(); gateway.Stop();
            try
            {
                if (upstreamTask != null)
                {
                    try { await upstreamTask; }
                    catch (OperationCanceledException) { }
                    catch (HttpListenerException) { }
                }
            }
            finally
            {
                host?.DisposeUpstreamClients();
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        static byte[] Encode(byte[] bytes, string encoding)
        {
            if (encoding == "identity") return bytes;
            if (encoding == "zstd")
            {
                using var compressor = new ZstdSharp.Compressor();
                return compressor.Wrap(bytes).ToArray();
            }
            using var output = new MemoryStream();
            using (Stream compressor = encoding switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)
            }) compressor.Write(bytes);
            return output.ToArray();
        }

        static HttpListener NewListener()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            return listener;
        }
    }

    private static async Task ValidatePayloadTooLargeCompressionRecoveryAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-413-compress-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        using var upstream = NewLoopbackListener();
        using var gateway = NewLoopbackListener();
        LocalPatGatewayHost? host = null;
        Task? upstreamTask = null;
        var mode = "zstd-ok";
        var observedEncodings = new List<string>();
        const int wireLimitBytes = 4096;
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
            var account = new AccountRecord
            {
                Name = "size-limited-relay",
                AuthKind = AccountAuthKind.CompatibleApi,
                CodexHome = Path.Combine(root, "account"),
                ApiBaseUrl = upstream.Prefixes.Single().TrimEnd('/') + "/v1",
                ApiModel = "gpt-6-astra"
            };
            Directory.CreateDirectory(account.CodexHome);
            File.WriteAllText(
                Path.Combine(account.CodexHome, "auth.json"),
                "{\"OPENAI_API_KEY\":\"sk-413-fixture-only\"}");
            new AccountStore(root).SaveAccounts([account]);
            new ThemeService(root).SaveSettings(new AppSettings
            {
                AccountRotationEnabled = false,
                PatAutoRotationEnabled = false,
                PatGatewayProxyAutoDetect = false,
                PatGatewayProxy = null,
                PatGatewayProxyPort = null
            });

            upstreamTask = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var context = await upstream.GetContextAsync().WaitAsync(cancellationToken);
                    using var rawBuffer = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(rawBuffer, cancellationToken);
                    var rawBytes = rawBuffer.ToArray();
                    var encoding = context.Request.Headers["Content-Encoding"]?.Trim() ?? "identity";
                    observedEncodings.Add(encoding);

                    if (rawBytes.Length > wireLimitBytes)
                    {
                        context.Response.StatusCode = 413;
                        context.Response.Headers["cf-ray"] = "fixture413-LAX";
                        await context.Response.OutputStream.WriteAsync(
                            Encoding.UTF8.GetBytes("<html>413 Request Entity Too Large</html>"),
                            cancellationToken);
                        context.Response.Close();
                        continue;
                    }

                    var rejectEncoding = mode switch
                    {
                        "zstd-ok" => false,
                        "gzip-only" => encoding.Equals("zstd", StringComparison.OrdinalIgnoreCase),
                        "none" => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase),
                        _ => false
                    };
                    if (rejectEncoding)
                    {
                        context.Response.StatusCode = mode == "gzip-only" ? 415 : 400;
                        await context.Response.OutputStream.WriteAsync(
                            Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"unsupported encoding\"}}"),
                            cancellationToken);
                        context.Response.Close();
                        continue;
                    }

                    var decoded = encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
                        ? rawBytes
                        : DecodeRequestBody(rawBytes, encoding);
                    using var document = JsonDocument.Parse(decoded);
                    if (document.RootElement.GetProperty("model").GetString() != "gpt-6-astra" ||
                        !document.RootElement.GetProperty("input").GetString()!.StartsWith("large-context-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("413 compression retry corrupted the forwarded JSON payload.");
                    }

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(
                        Encoding.UTF8.GetBytes(
                            "{\"id\":\"resp_413_recovered\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-6-astra\",\"output\":[]}"),
                        cancellationToken);
                    context.Response.Close();
                }
            }, cancellationToken);

            host = new LocalPatGatewayHost("X-413-Fixture", "1", new Uri(gateway.Prefixes.Single()).Port);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            var largePlain = Encoding.UTF8.GetBytes(
                "{\"model\":\"gpt-6-astra\",\"stream\":false,\"input\":\"large-context-" +
                new string('Q', 16_384) + "\"}");
            var largeZstd = EncodeRequestBody(largePlain, "zstd");

            // Case 1: relay returns 413 on identity, accepts zstd retry -> 200 OK.
            mode = "zstd-ok";
            observedEncodings.Clear();
            var (status1, body1, _) = await SendLargeAsync(largeZstd, "zstd");
            if (status1 != HttpStatusCode.OK || !body1.Contains("resp_413_recovered", StringComparison.Ordinal) ||
                !observedEncodings.SequenceEqual(["identity", "zstd"]))
            {
                throw new InvalidOperationException(
                    $"413->zstd recovery failed: status={status1}, attempts={string.Join(',', observedEncodings)}");
            }

            // Case 2: relay returns 413 on identity, 415 on zstd, accepts gzip fallback -> 200 OK.
            mode = "gzip-only";
            observedEncodings.Clear();
            var (status2, body2, _) = await SendLargeAsync(largeZstd, "zstd");
            if (status2 != HttpStatusCode.OK || !body2.Contains("resp_413_recovered", StringComparison.Ordinal) ||
                !observedEncodings.SequenceEqual(["identity", "zstd", "gzip"]))
            {
                throw new InvalidOperationException(
                    $"413->zstd->gzip fallback failed: status={status2}, attempts={string.Join(',', observedEncodings)}");
            }

            // Case 3: relay rejects all compression probes -> preserves original 413 + cf-ray (never 400/415).
            mode = "none";
            observedEncodings.Clear();
            var (status3, _, cfRay3) = await SendLargeAsync(largeZstd, "zstd");
            if (status3 != HttpStatusCode.RequestEntityTooLarge ||
                cfRay3 != "fixture413-LAX" ||
                !observedEncodings.SequenceEqual(["identity", "zstd", "gzip"]))
            {
                throw new InvalidOperationException(
                    $"413 preservation failed: status={status3}, cf-ray={cfRay3}, attempts={string.Join(',', observedEncodings)}");
            }

            async Task<(HttpStatusCode Status, string Body, string? CfRay)> SendLargeAsync(byte[] wireBytes, string encoding)
            {
                using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var handle = Task.Run(async () => await host.HandleAsync(
                    await gateway.GetContextAsync().WaitAsync(cancellationToken),
                    gateway,
                    shutdown));
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    gateway.Prefixes.Single() + "backend-api/codex/responses");
                request.Headers.Authorization = new("Bearer", "sk-413-fixture-only");
                request.Content = new ByteArrayContent(wireBytes);
                request.Content.Headers.ContentType = new("application/json");
                if (encoding != "identity")
                {
                    request.Content.Headers.ContentEncoding.Add(encoding);
                }
                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var cfRay = response.Headers.TryGetValues("cf-ray", out var values) ? values.FirstOrDefault() : null;
                await handle.WaitAsync(cancellationToken);
                return (response.StatusCode, body, cfRay);
            }
        }
        finally
        {
            upstream.Stop();
            gateway.Stop();
            if (upstreamTask != null)
            {
                try { await upstreamTask; }
                catch (OperationCanceledException) { }
                catch (HttpListenerException) { }
            }
            host?.DisposeUpstreamClients();
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static HttpListener NewLoopbackListener()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return listener;
        }
    }
}
