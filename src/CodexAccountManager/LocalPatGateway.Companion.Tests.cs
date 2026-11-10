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
            Console.WriteLine("PASS: unpatched HTTP relay rejects direct bootstrap; local gateway completes tool round trips for identity/gzip/deflate/br/zstd; real Page content and unknown clients remain intact");

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
                if (host != null) foreach (var client in host._clients.Values) client.Dispose();
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
}
