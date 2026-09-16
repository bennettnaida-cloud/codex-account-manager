using System.Text.Json;

namespace CodexAccountManager;

// A browser session owns this helper's stdin. EOF releases only its own native
// cores; no gateway, other browser, or system proxy configuration is changed.
internal static class ProxyStdioHost
{
    internal static int Run(bool import)
    {
        try
        {
            var line = Console.ReadLine();
            if (line == null || line.Length > 32768) throw new InvalidDataException("节点请求无效。");
            using var request = JsonDocument.Parse(line);
            var store = new ProxyNodeStore();
            if (import)
            {
                var text = request.RootElement.GetProperty("uri").GetString() ?? "";
                var preview = store.PreviewImport(text);
                if (preview.InvalidEntries.Count != 0 || preview.Nodes.Count != 1)
                    throw new InvalidDataException("请输入一个有效的节点分享链接。");
                var node = preview.Nodes[0];
                store.SetNode(node);
                Reply(new { ok = true, nodeId = node.NodeId, name = node.Name });
                return 0;
            }

            var nodeId = request.RootElement.GetProperty("nodeId").GetString();
            var selected = store.LoadNodes().SingleOrDefault(n => n.NodeId == nodeId);
            if (selected == null || !selected.Enabled) throw new InvalidDataException("绑定节点不存在或已禁用。");
            // Browser proxy authentication needs a dedicated local adapter, not a
            // credential-bearing command-line URL. Native protocols supply that adapter.
            if (!selected.IsNativeProtocol && !string.IsNullOrWhiteSpace(selected.Username))
                throw new InvalidDataException("浏览器暂不支持带用户名的 HTTP/SOCKS 节点，请使用原生节点链接。");
            using var resolver = new AccountProxyResolver(store.RootPath);
            var resolution = resolver.ResolveByNode(selected);
            if (!resolution.Success) throw new InvalidDataException(resolution.Error);
            Reply(new { ok = true, proxyUri = resolution.ProxyUri!.AbsoluteUri, nodeId });
            while (Console.ReadLine() != null) { }
            return 0;
        }
        catch
        {
            // Parser/native-core errors may embed the private URI; never echo them.
            Reply(new { ok = false, error = "独立节点启动失败，请检查节点配置和 sing-box/Xray 核心。未回退全局代理。" });
            return 1;
        }
    }

    private static void Reply(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }
}
