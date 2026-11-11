using System.Text.Json.Nodes;

namespace CodexAccountManager;

// One-shot local repair, requested explicitly by the user. Other installations
// and intentionally configured WSL/SSH connections are unaffected by default.
internal static class WindowsLocalPreferenceRepair
{
    internal static string RequestPath(string managerRoot) =>
        Path.Combine(managerRoot, ".cache", "windows-local-preference-repair.pending");

    internal static bool IsPending(string managerRoot) => File.Exists(RequestPath(managerRoot));

    internal static void ApplyAfterClientExit(string managerRoot, string codexHome)
    {
        var request = RequestPath(managerRoot);
        if (!File.Exists(request)) return;
        var path = Path.Combine(codexHome, ".codex-global-state.json");
        if (File.Exists(path))
        {
            var original = File.ReadAllText(path);
            var state = JsonNode.Parse(original) as JsonObject
                ?? throw new InvalidDataException("Codex 设置格式无效，已保留原文件与本机启动修复请求。");
            var keys = state.Where(pair =>
                pair.Key.StartsWith("electron-account-remote-preference:selected-remote-host-id:", StringComparison.Ordinal) &&
                pair.Value is JsonValue value && value.TryGetValue<string>(out var host) &&
                host.StartsWith("remote-wsl:", StringComparison.Ordinal)).Select(pair => pair.Key).ToArray();
            if (keys.Length > 0)
            {
                var backup = Path.Combine(managerRoot, "backups", "windows-local-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(backup, ".codex-global-state.json"), original);
                foreach (var key in keys) state.Remove(key);
                if (File.ReadAllText(path) != original)
                    throw new IOException("Codex 设置已被其它进程更改，请关闭 Codex 后重试本机启动。");
                AtomicFilePersistence.WriteAllText(path, state.ToJsonString());
                ManagerLifecycleDiagnostics.Write("windows-local-preference-repaired", $"wsl_defaults_removed={keys.Length}");
            }
        }
        File.Delete(request);
    }

    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-local-preference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ".codex-global-state.json");
            const string original = """{"electron-account-remote-preference:selected-remote-host-id:a":"remote-wsl:Ubuntu","electron-account-remote-preference:selected-remote-host-id:b":"remote-ssh:server","threads":["keep"],"future":{"key":123}}""";
            File.WriteAllText(path, original);
            ApplyAfterClientExit(root, root);
            if (File.ReadAllText(path) != original) throw new InvalidOperationException("WSL repair requires explicit opt-in.");
            Directory.CreateDirectory(Path.GetDirectoryName(RequestPath(root))!);
            File.WriteAllText(RequestPath(root), "requested");
            ApplyAfterClientExit(root, root);
            var actual = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var expected = JsonNode.Parse(original)!.AsObject();
            expected.Remove("electron-account-remote-preference:selected-remote-host-id:a");
            if (IsPending(root) || !JsonNode.DeepEquals(actual, expected))
                throw new InvalidOperationException("WSL repair must preserve other connections and all unrelated state.");
            var repaired = File.ReadAllText(path);
            ApplyAfterClientExit(root, root);
            if (File.ReadAllText(path) != repaired) throw new InvalidOperationException("WSL repair must be idempotent.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
