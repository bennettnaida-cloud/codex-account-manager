using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private sealed class AccessTokenCatalogFixtureHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond(request, cancellationToken);
    }

    internal static async Task ValidateAccessTokenRemoteDiscoveryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cam-at-discovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var account = new AccountRecord { AuthKind = AccountAuthKind.AccessToken,
                CodexHome = directory, AccessTokenModel = "gpt-6-astra" };
            var path = ManagedAccessTokenModelCatalogPath(account);
            var old = new JsonArray(new JsonObject { ["slug"] = "gpt-6-astra",
                ["supported_reasoning_levels"] = new JsonArray(new JsonObject { ["effort"] = AccessTokenReasoningEffort }) });
            WriteAccessTokenModelCatalog(path, old);
            var config = "model_catalog_json = " + TomlString(path) + ManagedModelCatalogMarker + "\nmodel = \"gpt-6-astra\"\n";
            File.WriteAllText(Path.Combine(directory, ConfigFileName), config);
            File.WriteAllText(Path.Combine(directory, "models_cache.json"), File.ReadAllText(path));
            var remote = old.DeepClone().AsArray();
            remote.Add(new JsonObject { ["slug"] = "gpt-99.1-sol", ["visibility"] = "list",
                ["future_metadata"] = new JsonObject { ["value"] = 42 } });
            var remoteJson = new JsonObject { ["models"] = remote }.ToJsonString();
            var requests = 0;
            var current = true;
            var mode = "success";
            var handler = new AccessTokenCatalogFixtureHandler();
            handler.Respond = async (request, ct) =>
            {
                requests++;
                Check(request.Headers.Authorization?.ToString() == "Bearer at-fixture-not-a-real-token", "credential isolation");
                Check(request.Headers.GetValues("version").Single() == "0.154.0", "actual CLI version");
                if (request.RequestUri!.Host == "auth.openai.com")
                {
                    Check(!request.Headers.Contains("chatgpt-account-id"), "no borrowed shared identity");
                    return Response(HttpStatusCode.OK, "{\"chatgpt_account_id\":\"fixture-workspace\",\"chatgpt_account_is_fedramp\":true}");
                }
                Check(request.RequestUri.AbsoluteUri == "https://chatgpt.com/backend-api/codex/models?client_version=0.154.0", "remote models endpoint");
                Check(request.Headers.GetValues("chatgpt-account-id").Single() == "fixture-workspace" &&
                    request.Headers.GetValues("x-openai-fedramp").Single() == "true", "PAT workspace binding");
                if (mode == "cancel") { await Task.Delay(Timeout.Infinite, ct); }
                if (mode == "401") return Response(HttpStatusCode.Unauthorized, "secret body must never be logged");
                if (mode == "503") return Response(HttpStatusCode.ServiceUnavailable, "secret body must never be logged");
                if (mode == "malformed") return Response(HttpStatusCode.OK, "not json");
                if (mode == "empty") return Response(HttpStatusCode.OK, "{\"models\":[]}");
                if (mode == "oversized")
                {
                    var response = Response(HttpStatusCode.OK, remoteJson);
                    response.Content.Headers.ContentLength = ManagedAccessTokenModelCatalogMaxBytes + 1;
                    return response;
                }
                if (mode == "identity-changed") current = false;
                return Response(HttpStatusCode.OK, remoteJson);
            };
            using var client = new HttpClient(handler);
            Task<bool> Refresh(bool force = true, bool require = false, CancellationToken ct = default) =>
                RefreshAccessTokenCatalogCoreAsync(account, "fixture-identity", require, force, () => current,
                    async token => (await FetchAccessTokenRemoteModelsAsync(client, "at-fixture-not-a-real-token", "0.154.0", token), "0.154.0"), ct);

            Check(await Refresh(), "old static catalog must be replaced by remote discovery");
            Check(requests == 2, "static catalog/cache must not suppress actual online requests");
            Check(File.ReadAllText(path).Contains("gpt-99.1-sol") && File.ReadAllText(path).Contains("future_metadata"), "new model and full metadata");
            Check(File.ReadAllText(Path.Combine(directory, ConfigFileName)) == config, "default/config preservation");
            Check(ReadVerifiedAccessTokenCatalog(path, "other-account", out _) == null, "credential receipt isolation");
            var good = File.ReadAllText(path);
            var receipt = File.ReadAllText(path + ".refresh.json");
            Check(!await Refresh(false, true) && requests == 2, "fresh verified cache reuse");
            Check(!await Refresh() && requests == 4, "forced refresh must query even an unchanged catalog");
            var beforeUpgrade = requests;
            Check(!await RefreshAccessTokenCatalogCoreAsync(account, "fixture-identity", true, false, () => current,
                async token => (await FetchAccessTokenRemoteModelsAsync(client, "at-fixture-not-a-real-token", "0.154.0", token), "0.163.0"),
                CancellationToken.None, "0.163.0") && requests == beforeUpgrade + 2,
                "client upgrade invalidates a fresh receipt instead of reusing an old server-filtered catalog");
            receipt = File.ReadAllText(path + ".refresh.json");
            foreach (var failure in new[] { "401", "503", "malformed", "empty", "oversized", "identity-changed" })
            {
                mode = failure;
                await ExpectFailure(Refresh());
                Check(File.ReadAllText(path) == good && File.ReadAllText(path + ".refresh.json") == receipt, "failure must preserve catalog and receipt: " + failure);
                current = true;
            }
            mode = "cancel";
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
                await ExpectFailure(Refresh(ct: cancellation.Token));
            Check(File.ReadAllText(path) == good, "cancellation preserves catalog");

            var stale = JsonSerializer.Deserialize<AccessTokenCatalogReceipt>(receipt)! with { FetchedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) };
            File.WriteAllText(path + ".refresh.json", JsonSerializer.Serialize(stale));
            mode = "503";
            Check(!await Refresh(false, true), "offline startup may reuse the same credential's verified last-good catalog");
            mode = "401";
            await ExpectFailure(Refresh(false, true));
            mode = "success";
            account.AccessTokenModel = "gpt-not-offered";
            await ExpectFailure(Refresh(false, true));
            Check(ReadVerifiedAccessTokenCatalog(path, "fixture-identity", out var updated) != null &&
                updated > stale.FetchedAtUtc, "unsupported default must not prevent discovery of other newly authorized models");
            using (var trace = BeginLaunchDiagnostics("model-default-fixture"))
            {
                Check(!await Refresh(false, false) && !string.IsNullOrWhiteSpace(trace.ModelCatalogWarning),
                    "desktop discovery warns about an unsupported default but still lets the user pick another authorized model");
            }
            File.AppendAllText(path, " ");
            Check(ReadVerifiedAccessTokenCatalog(path, "fixture-identity", out _) == null, "receipt detects external catalog edits");
            Check(HasTopLevelModelCatalog("model_catalog_json = \"custom.json\"\n[profiles.old]\n") &&
                !HasTopLevelModelCatalog("[profiles.old] # unused\nmodel_catalog_json = \"old.json\"\n"),
                "an inactive profile catalog must not suppress the active account's root catalog");
            Check(ModelCatalogUpdatedSince(path, DateTime.UtcNow.AddMinutes(-1).Ticks) &&
                !ModelCatalogUpdatedSince(path, DateTime.UtcNow.AddMinutes(1).Ticks) &&
                !ModelCatalogUpdatedSince(path, null) &&
                !ModelCatalogUpdatedSince(Path.Combine(directory, "missing.json"), DateTime.UtcNow.AddMinutes(-1).Ticks),
                "only a newer catalog and a known exact process identity justify reload on an explicit launch");
            Console.WriteLine("PASS: AT remote discovery, static-cache bypass, future metadata, workspace isolation, cancellation, last-good retention, stale/default handling");
        }
        finally { Directory.Delete(directory, recursive: true); }

        static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("AT discovery regression: " + message); }
        static HttpResponseMessage Response(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body) };
        static async Task ExpectFailure(Task<bool> action)
        {
            try { await action; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException or OperationCanceledException) { return; }
            throw new Exception("AT discovery regression: failure was incorrectly reported as success.");
        }
    }
}
