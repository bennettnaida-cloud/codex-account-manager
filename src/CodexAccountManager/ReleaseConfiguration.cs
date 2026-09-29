namespace CodexAccountManager;

/// <summary>
/// Version-scoped runtime values that must move together for a release. Keeping the
/// listener, URLs, mutex and persisted state suffix behind one configuration prevents
/// a newer package from accidentally controlling an older gateway.
/// </summary>
internal static class ReleaseConfiguration
{
    internal const string Version = "2.3.23";
    internal const int GatewayPort = 8340;
    internal const string GatewayPortText = "8340";
    internal const string GatewayListenerPrefix = "http://127.0.0.1:8340/";
    internal const string GatewayProviderBaseUrl =
        "http://127.0.0.1:8340/backend-api/codex";
    internal const string GatewayChatGptBaseUrl =
        "http://127.0.0.1:8340/backend-api";
}
