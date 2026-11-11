namespace CodexAccountManager;

public partial class Form1
{
    internal static void ValidateAccountRotationRowLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-rotation-layout-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", root);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, ".codex"));
            ValidateAccountRotationRowLayoutCore();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_ACCOUNT_MANAGER_HOME", previousRoot);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateAccountRotationRowLayoutCore()
    {
        // Run with an isolated manager root; never show the form or launch clients.
        using var form = new Form1(preserveExistingPatGatewayOnStartup: true);
        using var enabledAction = new Button { Enabled = true };
        using var disabledAction = new Button { Enabled = false };
        form.Controls.AddRange([enabledAction, disabledAction]);
        form.SetButtonsEnabled(false);
        form.SetButtonsEnabled(false);
        form.SetButtonsEnabled(true);
        if (enabledAction.Enabled) throw new InvalidOperationException("Nested launch enabled actions too early.");
        form.SetButtonsEnabled(true);
        if (!enabledAction.Enabled || disabledAction.Enabled)
            throw new InvalidOperationException("Launch changed the original action availability.");
        var account = new AccountRecord { Name = "rotation-layout@example.invalid", AuthKind = AccountAuthKind.OfficialOAuth, CodexHome = Path.Combine(form._store.RootPath, "layout-fixture") };
        var usage = new AccountUsageSummary
        {
            AccountName = account.Name,
            RateLimitUsedPercent = 90,
            RateLimitWindowMinutes = 300,
            RateLimitResetAtUtc = DateTimeOffset.Now.AddHours(4),
            SecondaryRateLimitUsedPercent = 52,
            SecondaryRateLimitWindowMinutes = 10080,
            SecondaryRateLimitResetAtUtc = DateTimeOffset.Now.AddDays(5)
        };
        foreach (var width in new[] { 640, 970, 1450 })
        foreach (var scale in new[] { 1F, 1.5F, 2F })
        {
            using var row = form.CreateAccountRotationRow(account, AccountRotationPool.Primary, 0, 2, width, usage);
            var detail = row.Controls.OfType<Label>().Single(label => label.Text.Contains("周重置", StringComparison.Ordinal));
            using var font = new Font(detail.Font.FontFamily, detail.Font.Size * scale);
            detail.Font = font;
            Check();
            // In-place refresh must resize too, without rebuilding the list.
            detail.Text += "  ·  下次周额度重置时间待服务端更新";
            Check();
            using var bitmap = new Bitmap(row.Width, row.Height);
            row.DrawToBitmap(bitmap, row.ClientRectangle);
            if (Environment.GetEnvironmentVariable("CAM_ROTATION_LAYOUT_PREVIEW") is { Length: > 0 } path && width == 1450 && scale == 2F)
                bitmap.Save(path);
            void Check()
            {
                if (detail.Height < detail.GetPreferredSize(new Size(detail.Width, 0)).Height || detail.Bottom + 8 > row.Height ||
                    row.Controls.OfType<Button>().Any(button => button.Bounds.IntersectsWith(detail.Bounds) || button.Bottom > row.Height))
                    throw new InvalidOperationException($"Rotation quota/reset text clipped or overlapped at width={width}, scale={scale}.");
            }
        }
    }
}
