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
    bool Archived,
    string SectionId = "",
    string SectionName = "");

internal sealed record CodexThreadSection(
    string Id,
    string Name,
    string Appearance)
{
    public bool IsPinned => Id.Equals(
        CodexAppServerClient.PinnedSectionId,
        StringComparison.OrdinalIgnoreCase);
}

internal sealed record CodexAccountIdentity(string Email, string PlanType);

internal sealed class CodexAppServerClient
{
    internal const string PinnedSectionId = "01984de2-8f74-7c91-a3b2-5c5e937cf318";
    private const int PageSize = 200;
    private const int MaxPagesPerArchiveState = 100;
    private const int MaxThreadSectionNameLength = 80;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AccountReadTimeout = TimeSpan.FromSeconds(8);

    public async Task<CodexAccountIdentity?> ReadAccountIdentityAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AccountReadTimeout);
        await using var session = await AppServerSession.StartAsync(codexHome, timeout.Token);
        var result = await session.RequestAsync(
            "account/read",
            new JsonObject { ["refreshToken"] = false },
            timeout.Token);
        return ParseAccountIdentity(result);
    }

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
                    if (!string.IsNullOrWhiteSpace(ReadString(item, "parentThreadId")))
                    {
                        // Sub-agent records are implementation details and do not belong in the
                        // user's manually managed Codex sidebar folders.
                        continue;
                    }

                    var updatedSeconds = ReadInt64(item, "recencyAt") ?? ReadInt64(item, "updatedAt") ?? 0;
                    var section = item["section"] as JsonObject;
                    threads[id] = new CodexThreadSummary(
                        id,
                        ReadString(item, "name"),
                        ReadString(item, "preview"),
                        ReadString(item, "cwd"),
                        ReadString(item, "modelProvider"),
                        FromUnixSeconds(updatedSeconds),
                        archived,
                        section == null ? string.Empty : ReadString(section, "id"),
                        section == null ? string.Empty : ReadString(section, "name"));
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

    public async Task<IReadOnlyList<CodexThreadSection>> ListThreadSectionsAsync(
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartThreadSectionAsync(codexHome, timeout.Token);

        var sections = new Dictionary<string, CodexThreadSection>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxPagesPerArchiveState; page++)
        {
            var parameters = new JsonObject();
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                parameters["cursor"] = cursor;
            }

            var result = await session.RequestAsync("threadSection/list", parameters, timeout.Token);
            if (result["data"] is not JsonArray data)
            {
                throw new InvalidOperationException("Codex 返回的聊天目录格式无效。");
            }

            foreach (var item in data.OfType<JsonObject>())
            {
                var section = ParseThreadSection(item);
                if (section != null)
                {
                    sections[section.Id] = section;
                }
            }

            var nextCursor = ReadString(result, "nextCursor");
            if (string.IsNullOrWhiteSpace(nextCursor))
            {
                break;
            }
            if (!seenCursors.Add(nextCursor))
            {
                throw new InvalidOperationException("Codex 聊天目录返回了重复分页游标。");
            }
            cursor = nextCursor;
        }

        return sections.Values
            .OrderBy(section => section.IsPinned ? 0 : 1)
            .ThenBy(section => section.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<CodexThreadSection> CreateThreadSectionAsync(
        string name,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeThreadSectionName(name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartThreadSectionAsync(codexHome, timeout.Token);
        var result = await session.RequestAsync(
            "threadSection/create",
            new JsonObject
            {
                ["name"] = name,
                ["appearance"] = null
            },
            timeout.Token);
        return ParseThreadSection(result["section"] as JsonObject)
               ?? throw new InvalidOperationException("Codex 创建了目录，但没有返回目录信息。");
    }

    public async Task<CodexThreadSection> RenameThreadSectionAsync(
        string sectionId,
        string name,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateMutableSectionId(sectionId);
        name = NormalizeThreadSectionName(name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartThreadSectionAsync(codexHome, timeout.Token);
        var result = await session.RequestAsync(
            "threadSection/update",
            new JsonObject
            {
                ["sectionId"] = sectionId,
                ["name"] = name
            },
            timeout.Token);
        return ParseThreadSection(result["section"] as JsonObject)
               ?? new CodexThreadSection(sectionId, name, string.Empty);
    }

    public async Task DeleteThreadSectionAsync(
        string sectionId,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateMutableSectionId(sectionId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartThreadSectionAsync(codexHome, timeout.Token);
        await session.RequestAsync(
            "threadSection/delete",
            new JsonObject { ["sectionId"] = sectionId },
            timeout.Token);
    }

    public async Task MoveThreadToSectionAsync(
        string threadId,
        string? sectionId,
        string codexHome,
        CancellationToken cancellationToken = default)
    {
        ValidateThreadId(threadId);
        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            ValidateMutableSectionId(sectionId);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        await using var session = await AppServerSession.StartThreadSectionAsync(codexHome, timeout.Token);
        await session.RequestAsync(
            "thread/section/move",
            new JsonObject
            {
                ["threadId"] = threadId,
                ["sectionId"] = string.IsNullOrWhiteSpace(sectionId) ? null : sectionId,
                ["beforeThreadId"] = null
            },
            timeout.Token);
    }

    internal static void ValidateThreadSectionProtocol()
    {
        var section = ParseThreadSection(JsonNode.Parse(
            """{"id":"01a02005-6112-7c52-a91e-3a8d5e3b8339","name":"示例目录","appearance":null}""")
            ?.AsObject());
        if (section is not { Name: "示例目录", Appearance: "" } || section.IsPinned)
        {
            throw new InvalidOperationException("Codex 聊天目录协议解析自测失败。");
        }

        var normalized = NormalizeThreadSectionName("  示例目录  ");
        if (normalized != "示例目录")
        {
            throw new InvalidOperationException("Codex 聊天目录名称规范化自测失败。");
        }
        var pinnedRejected = false;
        try
        {
            ValidateMutableSectionId(PinnedSectionId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Pinned", StringComparison.Ordinal))
        {
            pinnedRejected = true;
        }
        if (!pinnedRejected)
        {
            throw new InvalidOperationException("内置 Pinned 目录被错误地视为可编辑目录。");
        }

        CodexCliService.ValidateThreadSectionCliResolution();
        ValidateThreadSectionCandidateFallback();
    }

    internal static void ValidateAccountIdentityProtocol()
    {
        var parsed = ParseAccountIdentity(JsonNode.Parse(
            """{"account":{"type":"chatgpt","email":"person@example.com","planType":"team"},"requiresOpenaiAuth":true}""")
            ?.AsObject());
        if (parsed is not { Email: "person@example.com", PlanType: "team" })
        {
            throw new InvalidOperationException("Codex account/read identity parsing self-test failed.");
        }

        foreach (var rejected in new[]
                 {
                     """{"account":{"type":"apiKey","email":"person@example.com","planType":null}}""",
                     """{"account":{"type":"chatgpt","email":"Display <person@example.com>","planType":"team"}}""",
                     """{"account":{"type":"chatgpt","email":"person@example.com\nforged","planType":"team"}}""",
                     """{"account":null,"requiresOpenaiAuth":true}"""
                 })
        {
            if (ParseAccountIdentity(JsonNode.Parse(rejected)?.AsObject()) != null)
            {
                throw new InvalidOperationException(
                    "Codex account/read identity parser accepted an unsafe or non-ChatGPT response.");
            }
        }
    }

    private static async Task<T> SelectThreadSectionCliCandidateAsync<T>(
        IReadOnlyList<string> candidates,
        Func<string, CancellationToken, Task<T>> tryCandidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(tryCandidate);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "找不到可用的 Codex CLI，无法使用聊天目录功能。");
        }

        var failures = new List<string>();
        foreach (var command in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await tryCandidate(command, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // User cancellation and the caller's timeout keep their original semantics.
                throw;
            }
            catch (Exception ex)
            {
                var detail = ex.GetBaseException().Message.Trim();
                if (detail.Length > 400)
                {
                    detail = detail[..400] + "...";
                }
                failures.Add($"{command}: {detail}");
            }
        }

        throw new InvalidOperationException(
            "随包 Codex 运行时和桌面 Codex 候选均无法使用聊天目录协议。" +
            Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    private static void ValidateThreadSectionCandidateFallback()
    {
        string[] candidates = ["packaged", "desktop-new", "desktop-old"];
        var attempts = new List<string>();
        var selected = SelectThreadSectionCliCandidateAsync(
                candidates,
                (candidate, _) =>
                {
                    attempts.Add(candidate);
                    return candidate == "desktop-old"
                        ? Task.FromResult(candidate)
                        : Task.FromException<string>(new MissingMethodException(candidate));
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (selected != "desktop-old" || !attempts.SequenceEqual(candidates))
        {
            throw new InvalidOperationException("聊天目录 CLI 方法探测没有逐项回退。");
        }

        attempts.Clear();
        var expectedCancellation = new OperationCanceledException(
            "thread-section candidate cancellation self-test",
            new CancellationToken(canceled: true));
        OperationCanceledException? observedCancellation = null;
        try
        {
            _ = SelectThreadSectionCliCandidateAsync(
                    candidates,
                    (candidate, _) =>
                    {
                        attempts.Add(candidate);
                        return Task.FromException<string>(expectedCancellation);
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException ex)
        {
            observedCancellation = ex;
        }
        if (!ReferenceEquals(observedCancellation, expectedCancellation) || attempts.Count != 1)
        {
            throw new InvalidOperationException("聊天目录 CLI 回退错误地吞掉了取消异常。");
        }
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

    private static CodexThreadSection? ParseThreadSection(JsonObject? value)
    {
        if (value == null)
        {
            return null;
        }

        var id = ReadString(value, "id");
        var name = ReadString(value, "name").Trim();
        if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        return new CodexThreadSection(id, name, ReadString(value, "appearance"));
    }

    private static CodexAccountIdentity? ParseAccountIdentity(JsonObject? result)
    {
        if (result?["account"] is not JsonObject account ||
            !ReadString(account, "type").Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var email = ReadString(account, "email").Trim();
        if (email.Length is < 3 or > 320 ||
            email.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return null;
        }
        try
        {
            var parsed = new System.Net.Mail.MailAddress(email);
            if (!parsed.Address.Equals(email, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        var planType = ReadString(account, "planType").Trim();
        if (planType.Length > 64 || planType.Any(char.IsControl))
        {
            planType = string.Empty;
        }
        return new CodexAccountIdentity(email, planType);
    }

    private static string NormalizeThreadSectionName(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("聊天目录名称不能为空。", nameof(name));
        }
        if (normalized.Length > MaxThreadSectionNameLength)
        {
            throw new ArgumentException(
                $"聊天目录名称不能超过 {MaxThreadSectionNameLength} 个字符。",
                nameof(name));
        }
        return normalized;
    }

    private static void ValidateMutableSectionId(string sectionId)
    {
        if (!Guid.TryParse(sectionId, out _))
        {
            throw new ArgumentException("Codex 聊天目录 ID 无效。", nameof(sectionId));
        }
        if (sectionId.Equals(PinnedSectionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("内置 Pinned 目录不能作为普通目录修改。");
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

            return await StartCommandAsync(command, codexHome, cancellationToken);
        }

        public static async Task<AppServerSession> StartThreadSectionAsync(
            string codexHome,
            CancellationToken cancellationToken)
        {
            var candidates = CodexCliService.ResolveThreadSectionCodexCliCandidates();
            return await SelectThreadSectionCliCandidateAsync(
                candidates,
                async (command, token) =>
                {
                    var session = await StartCommandAsync(command, codexHome, token);
                    try
                    {
                        // A successful initialize is insufficient: older desktop builds accept
                        // experimentalApi but do not register the thread-section RPCs.
                        var probe = await session.RequestAsync(
                            "threadSection/list",
                            new JsonObject { ["limit"] = 1 },
                            token);
                        if (probe["data"] is not JsonArray)
                        {
                            throw new InvalidOperationException(
                                "Codex threadSection/list 探测返回了无效格式。");
                        }
                        return session;
                    }
                    catch
                    {
                        try
                        {
                            await session.DisposeAsync();
                        }
                        catch
                        {
                            // Keep the original probe/start error for candidate fallback.
                        }
                        throw;
                    }
                },
                cancellationToken);
        }

        private static async Task<AppServerSession> StartCommandAsync(
            string command,
            string codexHome,
            CancellationToken cancellationToken)
        {

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
