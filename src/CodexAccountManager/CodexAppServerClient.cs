using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal sealed record CodexThreadSummary(
    string Id,
    string Name,
    string Preview,
    string WorkingDirectory,
    string ModelProvider,
    DateTimeOffset UpdatedAt,
    bool Archived);

internal sealed class CodexAppServerClient
{
    private const int PageSize = 200;
    private const int MaxPagesPerArchiveState = 100;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartAsync(codexHome, timeout.Token);

        var threads = new Dictionary<string, CodexThreadSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var archived in new[] { false, true })
        {
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < MaxPagesPerArchiveState; page++)
            {
                var parameters = new JsonObject
                {
                    ["archived"] = archived,
                    ["limit"] = PageSize,
                    ["sortKey"] = "recency_at",
                    ["sortDirection"] = "desc",
                    // The desktop UI uses the state database for its fast sidebar path. Avoid
                    // rescanning multi-gigabyte rollout directories on every manager refresh.
                    ["useStateDbOnly"] = true
                };
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    parameters["cursor"] = cursor;
                }

                var result = await session.RequestAsync("thread/list", parameters, timeout.Token);
                if (result["data"] is not JsonArray data)
                {
                    throw new InvalidOperationException("Codex 返回的聊天列表格式无效。");
                }

                foreach (var item in data.OfType<JsonObject>())
                {
                    var id = ReadString(item, "id");
                    if (!Guid.TryParse(id, out _))
                    {
                        continue;
                    }

                    var updatedSeconds = ReadInt64(item, "recencyAt") ?? ReadInt64(item, "updatedAt") ?? 0;
                    threads[id] = new CodexThreadSummary(
                        id,
                        ReadString(item, "name"),
                        ReadString(item, "preview"),
                        ReadString(item, "cwd"),
                        ReadString(item, "modelProvider"),
                        FromUnixSeconds(updatedSeconds),
                        archived);
                }

                var nextCursor = ReadString(result, "nextCursor");
                if (string.IsNullOrWhiteSpace(nextCursor))
                {
                    break;
                }
                if (!seenCursors.Add(nextCursor))
                {
                    throw new InvalidOperationException("Codex 聊天列表返回了重复分页游标。");
                }
                cursor = nextCursor;
            }
        }

        return threads.Values
            .OrderByDescending(thread => thread.UpdatedAt)
            .ThenByDescending(thread => thread.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task DeleteThreadAsync(
        string threadId,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateThreadId(threadId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartAsync(codexHome, timeout.Token);
        await session.RequestAsync(
            "thread/delete",
            new JsonObject { ["threadId"] = threadId },
            timeout.Token);
    }

    public async Task SetThreadArchivedAsync(
        string threadId,
        bool archived,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateThreadId(threadId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartAsync(codexHome, timeout.Token);
        await session.RequestAsync(
            archived ? "thread/archive" : "thread/unarchive",
            new JsonObject { ["threadId"] = threadId },
            timeout.Token);
    }

    private static string ReadString(JsonObject value, string propertyName)
    {
        try
        {
            return value[propertyName]?.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static long? ReadInt64(JsonObject value, string propertyName)
    {
        try
        {
            return value[propertyName]?.GetValue<long>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset FromUnixSeconds(long value)
    {
        if (value <= 0)
        {
            return DateTimeOffset.MinValue;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static void ValidateThreadId(string threadId)
    {
        if (!Guid.TryParse(threadId, out _))
        {
            throw new ArgumentException("Codex 任务 ID 无效。", nameof(threadId));
        }
    }

    private sealed class AppServerSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderrTask;
        private int _nextRequestId = 1;

        private AppServerSession(Process process)
        {
            _process = process;
            _stderrTask = process.StandardError.ReadToEndAsync();
        }

        public static async Task<AppServerSession> StartAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            var command = CodexCliService.ResolveCodexCliCommand();
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException("找不到可用的 Codex CLI，无法同步聊天目录。");
            }

            var startInfo = new ProcessStartInfo(command)
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new System.Text.UTF8Encoding(false),
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            startInfo.ArgumentList.Add("--disable");
            startInfo.ArgumentList.Add("plugins");
            CodexCliService.ConfigureChildCodexProcessEnvironment(startInfo, codexHome);

            var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }

            var session = new AppServerSession(process);
            try
            {
                var initializeResult = await session.RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "codex-account-manager",
                            ["title"] = "Codex Account Manager",
                            ["version"] = Application.ProductVersion
                        },
                        ["capabilities"] = new JsonObject
                        {
                            ["experimentalApi"] = true
                        }
                    },
                    cancellationToken);
                if (initializeResult is null)
                {
                    throw new InvalidOperationException("Codex app-server 初始化失败。");
                }

                await session.WriteMessageAsync(
                    new JsonObject { ["method"] = "initialized" },
                    cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonObject> RequestAsync(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            var requestId = _nextRequestId++;
            await WriteMessageAsync(
                new JsonObject
                {
                    ["id"] = requestId,
                    ["method"] = method,
                    ["params"] = parameters
                },
                cancellationToken);

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    var stderr = await ReadStderrAsync();
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(stderr)
                            ? $"Codex app-server 在响应 {method} 前退出。"
                            : $"Codex app-server 在响应 {method} 前退出：{TrimDetail(stderr)}");
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message == null || ReadInt64(message, "id") != requestId)
                {
                    continue;
                }
                if (message["error"] is JsonObject error)
                {
                    var detail = ReadString(error, "message");
                    if (string.IsNullOrWhiteSpace(detail))
                    {
                        detail = error.ToJsonString();
                    }
                    throw new InvalidOperationException(
                        $"Codex {method} 失败：{TrimDetail(detail)}");
                }

                return message["result"] as JsonObject ?? new JsonObject();
            }
        }

        private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
        {
            var line = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        private async Task<string> ReadStderrAsync()
        {
            if (!_process.HasExited)
            {
                return string.Empty;
            }
            return await _stderrTask;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch
            {
                // The child may already have closed stdin while shutting down.
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may exit between the timeout and Kill().
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static string TrimDetail(string value)
        {
            var normalized = value.Trim();
            return normalized.Length <= 1200 ? normalized : normalized[..1200] + "...";
        }
    }
}
