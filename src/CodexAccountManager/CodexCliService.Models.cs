namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    private static string AccessTokenModel => ModelCatalogService.DefaultModel;
    private static string AccessTokenReasoningEffort => ModelCatalogService.DefaultReasoningEffort;
    private const string CompatibleApiDefaultModel = "gpt-5.5";
    private const string CompatibleApiReasoningEffort = "xhigh";
    private const string DesktopServiceTier = "default";
    private const int DesktopAutoCompactTokenLimit = 1_000_000_000;
}
