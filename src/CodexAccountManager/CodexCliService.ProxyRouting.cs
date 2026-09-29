namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private AccountProxyResolver? _officialDesktopProxyResolver;

    private Uri? ResolveOfficialDesktopProxy(AccountRecord account)
    {
        var store = new ProxyNodeStore();
        var binding = store.GetBinding(AccountProxyResolver.AccountKeyFor(account));
        if (store.BindingsLoadFailed)
            throw new InvalidDataException("账号代理绑定无法读取；没有关闭当前 Codex。");
        // Unbound installations may intentionally use the OS proxy or a direct connection.
        if (binding?.Mode is null or ProxyBindingMode.InheritGlobal)
        {
            var global = GetConfiguredProxyUri();
            return string.IsNullOrWhiteSpace(global) ? null : ValidateDesktopProxyUri(new Uri(global));
        }
        if (_officialDesktopProxyResolver == null)
        {
            var resolver = new AccountProxyResolver(new AccountStore().RootPath);
            _officialDesktopProxyResolver = resolver;
            // Keep managed native cores alive for the desktop's lifetime, not just preflight.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => resolver.Dispose();
        }
        var resolution = _officialDesktopProxyResolver.Resolve(AccountProxyResolver.AccountKeyFor(account));
        if (!resolution.Success || resolution.ProxyUri == null)
            throw new InvalidOperationException(resolution.Error);
        var node = _officialDesktopProxyResolver.GetNode(resolution.NodeId);
        if (!string.IsNullOrWhiteSpace(node?.Username))
            throw new InvalidOperationException("官方桌面端不能通过启动参数安全传递代理密码，请为此账号绑定本地代理入口或原生节点。当前 Codex 未关闭。");
        return ValidateDesktopProxyUri(resolution.ProxyUri);
    }

    private static Uri ValidateDesktopProxyUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "socks5") ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("桌面代理必须为不含凭据的 HTTP/HTTPS/SOCKS5 地址。");
        return uri;
    }

    // Model traffic remains request-boundary routed by the gateway. The desktop's
    // Chromium login/feature requests also need an explicit proxy on cold activation.
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
