namespace CodexAccountManager;

/// <summary>
/// Version-scoped runtime values that must move together for a release. Keeping the
/// listener, URLs, mutex and persisted state suffix behind one configuration prevents
/// a newer package from accidentally controlling an older gateway.
/// </summary>
internal static class ReleaseConfiguration
{
    internal const string Version = "2.3.37";
    // Startup/catalog changes do not change the wire protocol; keep the compatible 2.3.24 listener
    // and persisted routing state so upgrading the UI does not interrupt requests.
    internal const int GatewayPort = 8341;
    internal const string GatewayPortText = "8341";
    internal const string GatewayListenerPrefix = "http://127.0.0.1:8341/";
    internal const string GatewayProviderBaseUrl =
        "http://127.0.0.1:8341/backend-api/codex";
    internal const string GatewayChatGptBaseUrl =
        "http://127.0.0.1:8341/backend-api";
}
