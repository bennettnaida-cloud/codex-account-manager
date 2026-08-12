namespace CodexAccountManager;

public sealed partial class CodexCliService
{
    // The public short alias is useful for pricing, but the manager's custom ChatGPT/PAT
    // provider must send an explicit model slug. Some workspaces reject the gpt-5.6 alias
    // even though they offer gpt-5.6-sol.
    private static string AccessTokenModel => ModelCatalogService.CanonicalDefaultModel;
    private static string AccessTokenReasoningEffort => ModelCatalogService.DefaultReasoningEffort;
    private const string CompatibleApiDefaultModel = "gpt-5.5";
    private const string CompatibleApiReasoningEffort = "xhigh";
    private const string DesktopServiceTier = "default";
    private const int DesktopAutoCompactTokenLimit = 1_000_000_000;
}
