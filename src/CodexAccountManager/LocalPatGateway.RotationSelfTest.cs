using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    // Real HTTP request/retry path, with synthetic credentials and loopback upstreams.
    // The production listener, account store and processes are never used.
    internal static async Task ValidateCompatibleQuotaFailoverAsync()
    {
        foreach (var status in new[] { 402, 403, 429 })
        {
            await ValidateCaseAsync(status, AccountRotationPool.Primary, true);
            await ValidateCaseAsync(status, AccountRotationPool.Backup, true);
        }
        await ValidateCaseAsync(403, AccountRotationPool.None, true);
        await ValidateCaseAsync(403, AccountRotationPool.Primary, false);
        await ValidateCaseAsync(403, AccountRotationPool.Primary, true, authorizationError: true);
        await ValidateCaseAsync(402, AccountRotationPool.Primary, true, manual: true);
        await ValidateCaseAsync(402, AccountRotationPool.Primary, true, manual: true, oauthSource: true);

        static async Task ValidateCaseAsync(
            int status, AccountRotationPool targetPool, bool enabled, bool authorizationError = false, bool manual = false,
            bool oauthSource = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "cam-rotation-test-" + Guid.NewGuid().ToString("N"));
            var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
            var previousShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
            LocalPatGatewayHost? host = null;
            using var upstream = CreateListener();
            using var gateway = CreateListener();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task? upstreamTask = null;
            try
            {
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
                Directory.CreateDirectory(root);
                var source = MakeAccount("source");
                var target = MakeAccount("target");
                var sourceToken = "sk-source-fixture-only";
                if (oauthSource)
                {
                    source.AuthKind = AccountAuthKind.OfficialOAuth;
                    sourceToken = BuildTestJwt(DateTimeOffset.UtcNow.AddHours(1));
                    File.WriteAllText(Path.Combine(source.CodexHome, "auth.json"), JsonSerializer.Serialize(new
                    {
                        tokens = new { access_token = sourceToken, id_token = sourceToken,
                            refresh_token = "fixture-only", account_id = "fixture-account" }
                    }));
                }
                var accounts = new[] { source, target };
                File.WriteAllText(Path.Combine(root, "accounts.json"), JsonSerializer.Serialize(accounts));
                var settings = new AppSettings { AccountRotationEnabled = true, PatGatewayProxyAutoDetect = false };
                AccountRotationConfiguration.SetPool(settings, accounts, source, AccountRotationPool.Primary);
                AccountRotationConfiguration.SetPool(settings, accounts, target, targetPool);
                settings.AccountRotationEnabled = enabled;
                settings.PatAutoRotationEnabled = enabled;
                new ThemeService(root).SaveSettings(settings);

                var seen = new List<string>();
                upstreamTask = Task.Run(async () =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        var context = await upstream.GetContextAsync().WaitAsync(cancellation.Token);
                        using var reader = new StreamReader(context.Request.InputStream);
                        using var body = JsonDocument.Parse(await reader.ReadToEndAsync(cancellation.Token));
                        if (body.RootElement.GetProperty("model").GetString() != "gpt-6-astra" ||
                            body.RootElement.GetProperty("input").GetString() != "rotation fixture")
                            throw new InvalidOperationException("Failover changed the model or request input.");
                        var isSource = context.Request.Headers["Authorization"] == "Bearer sk-source-fixture-only";
                        seen.Add(isSource ? "source" : "target");
                        context.Response.StatusCode = isSource ? status : 200;
                        context.Response.ContentType = "application/json";
                        var errorCode = authorizationError ? "permission_denied" : "insufficient_balance";
                        var payload = isSource
                            ? "{\"error\":{\"code\":\"" + errorCode + "\"}}"
                            : "{\"id\":\"resp_rotation_fixture\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-6-astra\",\"output\":[]}";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellation.Token);
                        context.Response.Close();
                    }
                });
                host = new LocalPatGatewayHost("X-Rotation-Fixture", "1", new Uri(gateway.Prefixes.Single()).Port);
                if (manual)
                    host._rotationStore.Arm(QuotaAccountIdentity.CreateKey(source),
                        QuotaAccountIdentity.CreateKey(target), DateTimeOffset.UtcNow);
                var handle = Task.Run(async () =>
                    await host.HandleAsync(await gateway.GetContextAsync().WaitAsync(cancellation.Token), gateway, cancellation));
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    gateway.Prefixes.Single() + "backend-api/codex/responses");
                request.Headers.Authorization = new("Bearer", sourceToken);
                request.Content = new StringContent(
                    "{\"model\":\"gpt-6-astra\",\"input\":\"rotation fixture\",\"stream\":false}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, cancellation.Token);
                var responseText = await response.Content.ReadAsStringAsync(cancellation.Token);
                await handle.WaitAsync(cancellation.Token);
                var shouldRotate = enabled && targetPool != AccountRotationPool.None && !authorizationError;
                var expected = manual ? new[] { "target" } : shouldRotate ? new[] { "source", "target" } : new[] { "source" };
                if (!seen.SequenceEqual(expected) || (int)response.StatusCode != (shouldRotate ? 200 : status))
                    throw new InvalidOperationException($"Quota failover HTTP {status}, pool={targetPool}, enabled={enabled}: " +
                        $"received {(int)response.StatusCode}, attempts={string.Join(',', seen)}, body={responseText}");
                if (shouldRotate && host._successfulActivityStore.ReadLatest()?.AccountKey != QuotaAccountIdentity.CreateKey(target))
                    throw new InvalidOperationException("The completed fallback account was not recorded.");
                var signal = host._quotaSignalStore.ReadLatestForAccount(QuotaAccountIdentity.CreateKey(source), DateTimeOffset.UtcNow);
                if (authorizationError || manual ? signal != null : signal == null)
                    throw new InvalidOperationException("Quota persistence confused billing and authorization errors.");
                if (manual && host._rotationStore.Load().Status != PatGatewayRotationStatus.Active)
                    throw new InvalidOperationException("Manual route did not activate on the model request.");
                Console.WriteLine($"HTTP {status}, target={targetPool}, enabled={enabled}, auth_error={authorizationError}, manual={manual}, oauth={oauthSource}: passed");

                AccountRecord MakeAccount(string name)
                {
                    var account = new AccountRecord
                    {
                        Name = name, CodexHome = Path.Combine(root, name), AuthKind = AccountAuthKind.CompatibleApi,
                        ApiBaseUrl = upstream.Prefixes.Single().TrimEnd('/'), ApiModel = "gpt-6-astra"
                    };
                    Directory.CreateDirectory(account.CodexHome);
                    File.WriteAllText(Path.Combine(account.CodexHome, "auth.json"),
                        JsonSerializer.Serialize(new { OPENAI_API_KEY = "sk-" + name + "-fixture-only" }));
                    return account;
                }
            }
            finally
            {
                cancellation.Cancel();
                upstream.Stop();
                gateway.Stop();
                if (upstreamTask != null)
                {
                    try { await upstreamTask; }
                    catch (OperationCanceledException) { }
                    catch (HttpListenerException) { }
                }
                if (host != null)
                    foreach (var client in host._clients.Values) client.Dispose();
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
                Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", previousShared);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        static HttpListener CreateListener()
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
