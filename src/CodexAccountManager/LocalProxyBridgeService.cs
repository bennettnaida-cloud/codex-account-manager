using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

// Explicit local opt-in only. Subscription imports cannot install startup commands.
internal static class LocalProxyBridgeService
{
    internal sealed record Registration(string NodeId, string Address, int Port, string Script);

    internal static bool EnsureReady(string root, ProxyNodeRecord node, out string error)
        => EnsureReady(root, node, IsListening, StartHidden, TimeSpan.FromSeconds(15), out error);

    internal static bool EnsureReady(string root, ProxyNodeRecord node,
        Func<string, int, bool> probe, Action<string> start, TimeSpan timeout, out string error)
    {
        error = "";
        var bridgeRoot = Path.Combine(Path.GetFullPath(root), "proxy-bridges");
        var manifest = Path.Combine(bridgeRoot, "autostart.json");
        if (!File.Exists(manifest)) return true;
        try
        {
            var entries = JsonSerializer.Deserialize<Registration[]>(File.ReadAllText(manifest),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (entries.Any(e => e is null)) throw new InvalidDataException();
            var matches = entries.Where(e => e.NodeId == node.NodeId).ToArray();
            if (matches.Length == 0) return true;
            if (matches.Length != 1) throw new InvalidDataException();
            var entry = matches[0];
            if (!IPAddress.TryParse(node.Address, out var address) || !IPAddress.IsLoopback(address) ||
                entry.Address != node.Address || entry.Port != node.Port ||
                string.IsNullOrWhiteSpace(entry.Script) || Path.IsPathRooted(entry.Script) ||
                entry.Script.Contains(':')) throw new InvalidDataException();
            var script = Path.GetFullPath(Path.Combine(bridgeRoot, entry.Script));
            if (!script.StartsWith(bridgeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
                throw new InvalidDataException();
            // Do not allow junctions/symlinks to escape the explicitly registered local directory.
            for (var current = script; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                if (current.Equals(bridgeRoot, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (probe(node.Address, node.Port)) return true;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.ToUpperInvariant())))[..24];
            using var mutex = new Mutex(false, "Local\\CAM.ProxyBridge." + key);
            var locked = false;
            try
            {
                try { locked = mutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { locked = true; }
                if (locked && !probe(node.Address, node.Port)) start(script);
                var watch = Stopwatch.StartNew();
                do
                {
                    if (probe(node.Address, node.Port)) return true;
                    if (!locked || watch.Elapsed >= timeout) break;
                    Thread.Sleep(150);
                } while (true);
                error = $"独立代理 {node.Address}:{node.Port} 自动启动后仍未就绪。请检查该节点的核心和配置；未改走全局代理。";
                return false;
            }
            finally { if (locked) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = "独立代理自动启动配置无效或启动失败；未改走全局代理。";
            return false;
        }
    }

    private static bool IsListening(string address, int port)
    {
        using var client = new TcpClient();
        try { return client.ConnectAsync(address, port).Wait(250) && client.Connected; }
        catch { return false; }
    }

    private static void StartHidden(string script)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)! };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
    }

    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "cam-bridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "proxy-bridges");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.ps1"), "# synthetic, never executed");
            var node = new ProxyNodeRecord { NodeId = "test", Address = "127.0.0.1", Port = 12345 };
            var starts = 0;
            bool Run(Func<string, int, bool> probe) => EnsureReady(root, node, probe, _ => starts++, TimeSpan.Zero, out _);
            void Save(Registration entry) => File.WriteAllText(Path.Combine(dir, "autostart.json"), JsonSerializer.Serialize(new[] { entry }));
            var valid = new Registration("test", node.Address, node.Port, "test.ps1");
            if (!Run((_, _) => false) || starts != 0) throw new InvalidOperationException("Unregistered node launched a helper.");
            Save(valid);
            if (!Run((_, _) => true) || starts != 0) throw new InvalidOperationException("Ready bridge was relaunched.");
            if (!Run((_, _) => starts > 0) || starts != 1) throw new InvalidOperationException("Bridge was not started before use.");
            if (Run((_, _) => false)) throw new InvalidOperationException("Unready bridge was accepted.");
            foreach (var invalid in new[] { valid with { Port = 99 }, valid with { Script = "../test.ps1" }, valid with { Script = "missing.ps1" } })
            {
                Save(invalid);
                var previous = starts;
                if (Run((_, _) => true) || starts != previous) throw new InvalidOperationException("Invalid bridge registration accepted.");
            }
            Save(valid with { NodeId = "other" });
            if (!Run((_, _) => false)) throw new InvalidOperationException("Unrelated account was blocked.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
