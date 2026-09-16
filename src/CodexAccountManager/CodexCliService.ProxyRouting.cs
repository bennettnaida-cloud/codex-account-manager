namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    // A desktop process inherits one global proxy. Per-account egress is enforced
    // by the gateway, independently of rotation and of the desktop login button.
    internal static bool RequiresDesktopGateway(AccountRecord account, bool requested = false)
    {
        if (requested || account.IsAccessToken) return true;
        var store = new ProxyNodeStore();
        var binding = store.GetBinding(AccountProxyResolver.AccountKeyFor(account));
        if (store.BindingsLoadFailed)
            throw new InvalidDataException("无法读取账号代理绑定；为避免绕过独立节点，已取消启动。请检查代理绑定文件后重试。");
        return binding?.Mode is ProxyBindingMode.FixedNode or ProxyBindingMode.Disabled;
    }

    private static void ValidateBoundDesktopProxyProjection(AccountRecord api, AccountRecord oauth)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "cam-desktop-proxy-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        Directory.CreateDirectory(fixture);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", fixture);
            var store = new ProxyNodeStore(fixture);
            var service = new CodexCliService();
            if (RequiresDesktopGateway(api) || !RequiresDesktopGateway(api, requested: true))
                throw new InvalidOperationException("Unbound API routing no longer respects the caller's rotation request.");

            foreach (var bindingMode in new[] { ProxyBindingMode.FixedNode, ProxyBindingMode.Disabled })
            {
                // Begin with the exact old direct profile. Binding a node must invalidate
                // both strict and relaxed voice/mobile reuse, including harmless text drift.
                store.SaveBindings([]);
                ProjectWindowsClientAccount(api, new LoginStatus(), AccessTokenSharedProfileMode.ChatGptDesktop, oauth);
                File.AppendAllText(Path.Combine(GetDefaultCodexHome(), ConfigFileName), "\n# runtime drift\n");
                store.SetBinding(new AccountProxyBinding
                {
                    AccountKey = AccountProxyResolver.AccountKeyFor(api),
                    Mode = bindingMode, NodeId = "fixture-japan", FallbackPolicy = ProxyFallbackPolicy.FailClosed
                });
                if (!RequiresDesktopGateway(api) || service.IsSharedChatGptFeatureProfileAlreadySelected(api, oauth) ||
                    CanIdentifySharedChatGptFeatureProfileWithoutNetwork(api, oauth))
                    throw new InvalidOperationException("Bound voice/mobile launch reused a direct/global-proxy profile.");

                // Codex and Codex++ share ApiCompatible projection; voice/mobile uses
                // ChatGptDesktop. Call the real launch projection with rotation OFF.
                foreach (var mode in new[] { AccessTokenSharedProfileMode.ApiCompatible, AccessTokenSharedProfileMode.ChatGptDesktop })
                {
                    var feature = mode == AccessTokenSharedProfileMode.ChatGptDesktop ? oauth : null;
                    ProjectWindowsClientAccount(api, new LoginStatus(), mode, feature, routeOfficialOAuthThroughGateway: false);
                    var config = File.ReadAllText(Path.Combine(GetDefaultCodexHome(), ConfigFileName));
                    if (!config.Contains("base_url = " + TomlString(LocalPatGateway.ProviderBaseUrl), StringComparison.Ordinal) ||
                        !CanReuseSharedProfileWithoutNetwork(api, Path.Combine(api.CodexHome, ConfigFileName), mode, feature))
                        throw new InvalidOperationException("Desktop launch projection bypassed the bound proxy gateway.");
                    if (feature != null && (!service.IsSharedChatGptFeatureProfileAlreadySelected(api, oauth) ||
                        !TryReadChatGptAuthAccountId(Path.Combine(GetDefaultCodexHome(), AuthFileName), out var owner) || owner != "account-A"))
                        throw new InvalidOperationException("Gateway routing changed the voice/mobile OAuth identity.");
                }
            }

            store.SaveBindings([]);
            ProjectWindowsClientAccount(api, new LoginStatus(), AccessTokenSharedProfileMode.ChatGptDesktop, oauth, true);
            if (!service.IsSharedChatGptFeatureProfileAlreadySelected(api, oauth, true))
                throw new InvalidOperationException("Voice/mobile launch lost explicitly requested rotation routing.");
            File.WriteAllText(store.BindingsPath, "{ invalid json");
            try { RequiresDesktopGateway(api); throw new InvalidOperationException("Corrupt bindings silently bypassed proxy isolation."); }
            catch (InvalidDataException) { }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Directory.Delete(fixture, recursive: true);
        }
        // Leave the enclosing credential/projection test in its original unbound state.
        ProjectCompatibleApiAccount(api, new LoginStatus(), AccessTokenSharedProfileMode.ChatGptDesktop, oauth);
    }
}
