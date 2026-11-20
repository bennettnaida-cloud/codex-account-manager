using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal sealed partial class LocalPatGatewayHost
{
    internal static async Task ValidateCpaRoutingAsync(bool roundRobin = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-cpa-test-" + Guid.NewGuid().ToString("N"));
        var oldRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var oldShared = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME");
        using var upstream = Listen();
        using var gateway = Listen();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        LocalPatGatewayHost? host = null;
        Task? pump = null;
        var workers = new ConcurrentBag<Task>();
        var seen = new ConcurrentQueue<string>();
        var failures = new ConcurrentDictionary<string, int>();
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", Path.Combine(root, "shared"));
            var a = MakeAccount("a");
            var b = MakeAccount("b");
            var c = MakeAccount("c");
            var d = MakeAccount("backup");
            var pausedFive = MakeAccount("paused-five");
            var pausedWeek = MakeAccount("paused-week");
            // No usable PAT: admission must check the quota snapshot before even
            // attempting credential resolution or a metadata network request.
            foreach (var paused in new[] { pausedFive, pausedWeek })
            {
                paused.AuthKind = AccountAuthKind.AccessToken;
                File.WriteAllText(Path.Combine(paused.CodexHome, "auth.json"), "{}");
            }
            var accounts = new[] { pausedFive, pausedWeek, a, b, c, d };
            File.WriteAllText(Path.Combine(root, "accounts.json"), JsonSerializer.Serialize(accounts));
            var settings = new AppSettings { AccountRotationEnabled = true, PatGatewayProxyAutoDetect = false,
                AccountRotationStrategy = roundRobin ? "round-robin" : "fill-first" };
            foreach (var account in accounts)
                AccountRotationConfiguration.SetPool(settings, accounts, account,
                    account == d ? AccountRotationPool.Backup : AccountRotationPool.Primary);
            new ThemeService(root).SaveSettings(settings);
            var quotas = new QuotaSnapshotStore(root);
            quotas.Save(pausedFive, new UsageLimitResetInfo(null, [], null,
                new UsageRateLimitWindow(90, 300, DateTimeOffset.UtcNow.AddHours(1)), null, null, null), DateTimeOffset.UtcNow);
            quotas.Save(pausedWeek, new UsageLimitResetInfo(null, [], null,
                new UsageRateLimitWindow(97, 10080, DateTimeOffset.UtcNow.AddDays(1)), null, null, null), DateTimeOffset.UtcNow);
            host = new LocalPatGatewayHost("X-Cpa-Fixture", "1", new Uri(gateway.Prefixes.Single()).Port);

            pump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var ctx = await upstream.GetContextAsync().WaitAsync(stop.Token);
                    workers.Add(Respond(ctx));
                }
            });

            await Send("one"); await Send("two"); await Send("three"); await Send("four");
            Expect(roundRobin ? ["a", "b", "c", "a"] : ["a", "a", "a", "a"]);
            // Ordinary aliases do not override the chosen CPA scheduling strategy.
            await Send("continuation", previous: "resp_cpa_two");
            Expect(roundRobin ? "b" : "a");
            await Send("unknown", previous: "resp_cpa_missing", expectedStatus: 409);
            Expect();

            host._rotationStore.Arm(Key(a), Key(c), DateTimeOffset.UtcNow);
            await Send("pinned-during-arm", previous: "resp_cpa_one");
            Expect("a");
            if (host._rotationStore.Load().Status != PatGatewayRotationStatus.Armed)
                throw new InvalidOperationException("A strict continuation consumed a manual route.");
            await Send("manual"); await Send("after-manual");
            Expect("c", roundRobin ? "a" : "c");
            host._rotationStore.Clear();

            // Reserve the next cursor at admission, not stream completion. A held
            // request must neither stall new requests nor rewind their chosen cursor.
            var held = Send("held");
            await heldStarted.Task.WaitAsync(stop.Token);
            await Send("while-held");
            Expect(roundRobin ? ["b", "c"] : ["a", "a"]);
            heldRelease.SetResult();
            await held;
            await Send("after-held");
            Expect("a");
            await Task.WhenAll(Enumerable.Range(0, 9).Select(i => Send("parallel-" + i)));
            var counts = new List<string>();
            while (seen.TryDequeue(out var item)) counts.Add(item);
            if (counts.Count != 9 || (roundRobin
                ? new[] { "a", "b", "c" }.Any(n => counts.Count(x => x == n) != 3)
                : counts.Any(n => n != "a")))
                throw new InvalidOperationException("Concurrent admissions violated the selected CPA strategy.");

            host._cpaPrimaryCursor = Key(c);
            failures["a"] = 503; failures["b"] = 502;
            await Send("transient");
            Expect("a", "b", "c");
            await Send("cooled-continuation", previous: "resp_cpa_two", expectedStatus: 503);
            Expect();
            await Send("single-primary");
            Expect("c");
            failures["c"] = 429;
            await Send("backup-takeover");
            Expect("c", "backup");
            await Send("backup-again");
            Expect("backup");
            if (host._quotaSignalStore.ReadLatestPerAccount(DateTimeOffset.UtcNow).Count != 0)
                throw new InvalidOperationException("Transient failures fabricated quota exhaustion.");
            failures["backup"] = 503;
            await Send("all-failed", expectedStatus: 503);
            Expect("backup");
            await Send("all-cooling", expectedStatus: 503);
            Expect();
            failures.Clear();
            foreach (var key in host._cpaCooldownUntil.Keys)
                host._cpaCooldownUntil[key] = DateTimeOffset.UtcNow.AddSeconds(-1);
            await Send("recovered");
            Expect("a");

            failures["a"] = 401; failures["b"] = 403; failures["c"] = 408;
            host._cpaPrimaryCursor = Key(c);
            await Send("credential-failover");
            Expect("a", "b", "c", "backup");
            failures.Clear();
            host._cpaCooldownUntil.Clear();
            host._cpaPrimaryCursor = Key(c);
            failures["a"] = 402;
            await Send("real-quota-exhaustion");
            Expect("a", "b");
            if (host._quotaSignalStore.ReadLatestForAccount(Key(a), DateTimeOffset.UtcNow) == null)
                throw new InvalidOperationException("CPA failed to retain confirmed quota exhaustion.");
            await Send("skip-exhausted");
            Expect(roundRobin ? "c" : "b");
            failures.Clear();

            // Quota probes and disabled rotation always stay on the supplied account.
            await Send("probe", probe: true);
            Expect("a");
            settings.AccountRotationEnabled = false;
            settings.PatAutoRotationEnabled = false;
            new ThemeService(root).SaveSettings(settings);
            await Send("disabled");
            Expect("a");
            Console.WriteLine($"CPA {settings.AccountRotationStrategy}: ordering, concurrency, manual route, strict continuation, reserves, cooldown, backup, recovery and probes passed.");

            AccountRecord MakeAccount(string name)
            {
                var record = new AccountRecord { Name = name, CodexHome = Path.Combine(root, name),
                    RegistrationId = Guid.NewGuid().ToString("N"), RegisteredAtUtc = DateTimeOffset.UtcNow,
                    AuthKind = AccountAuthKind.CompatibleApi, ApiBaseUrl = upstream.Prefixes.Single().TrimEnd('/'), ApiModel = "gpt-6-astra" };
                Directory.CreateDirectory(record.CodexHome);
                File.WriteAllText(Path.Combine(record.CodexHome, "auth.json"),
                    JsonSerializer.Serialize(new { OPENAI_API_KEY = "sk-cpa-" + name + "-fixture" }));
                return record;
            }

            async Task Respond(HttpListenerContext ctx)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync(stop.Token));
                var input = body.RootElement.GetProperty("input").GetString()!;
                if (body.RootElement.GetProperty("model").GetString() != "gpt-6-astra")
                    throw new InvalidOperationException("CPA changed the requested model.");
                var account = new[] { a, b, c, d }.Single(x =>
                    ctx.Request.Headers["Authorization"] == "Bearer sk-cpa-" + x.Name + "-fixture");
                seen.Enqueue(account.Name);
                if (input == "held") { heldStarted.SetResult(); await heldRelease.Task.WaitAsync(stop.Token); }
                var status = failures.GetValueOrDefault(account.Name, 200);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                var payload = status == 200
                    ? JsonSerializer.Serialize(new { id = "resp_cpa_" + input, @object = "response", status = "completed", model = "gpt-6-astra", output = Array.Empty<object>() })
                    : status == 402 ? "{\"error\":{\"code\":\"insufficient_balance\"}}"
                    : "{\"error\":{\"code\":\"temporary_failure\"}}";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(payload), stop.Token);
                ctx.Response.Close();
            }

            async Task Send(string input, string? previous = null, int expectedStatus = 200, bool probe = false)
            {
                var handler = Task.Run(async () => await host.HandleAsync(
                    await gateway.GetContextAsync().WaitAsync(stop.Token), gateway, stop));
                using var request = new HttpRequestMessage(HttpMethod.Post, gateway.Prefixes.Single() + "backend-api/codex/responses");
                request.Headers.Authorization = new("Bearer", "sk-cpa-a-fixture");
                request.Headers.Add("session_id", "same-session-for-all-requests");
                if (probe) request.Headers.Add(LocalPatGateway.RequestPurposeHeader, LocalPatGateway.QuotaTestRequestPurpose);
                var data = new Dictionary<string, object> { ["model"] = "gpt-6-astra", ["input"] = input,
                    ["stream"] = false, ["prompt_cache_key"] = "same-cache-key" };
                if (previous != null) data["previous_response_id"] = previous;
                request.Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, stop.Token);
                var payload = await response.Content.ReadAsStringAsync(stop.Token);
                await handler.WaitAsync(stop.Token);
                if ((int)response.StatusCode != expectedStatus)
                    throw new InvalidOperationException($"CPA {input}: HTTP {(int)response.StatusCode}, expected {expectedStatus}; {payload}");
            }

            void Expect(params string[] expected)
            {
                var actual = new List<string>();
                while (seen.TryDequeue(out var name)) actual.Add(name);
                if (!actual.SequenceEqual(expected))
                    throw new InvalidOperationException($"CPA attempts: {string.Join(',', actual)}, expected {string.Join(',', expected)}");
            }
        }
        finally
        {
            heldRelease.TrySetResult();
            stop.Cancel(); upstream.Stop(); gateway.Stop();
            if (pump != null) { try { await pump; } catch (OperationCanceledException) { } catch (HttpListenerException) { } }
            try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
            host?.DisposeUpstreamClients();
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", oldRoot);
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_SHARED_CODEX_HOME", oldShared);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        static string Key(AccountRecord account) => QuotaAccountIdentity.CreateKey(account);
        static HttpListener Listen()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); return listener;
        }
    }
}
