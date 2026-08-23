using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

public enum UnifiedThreadMessageRole
{
    User,
    Assistant
}

public sealed record UnifiedThreadMessage(
    UnifiedThreadMessageRole Role,
    string Text,
    DateTimeOffset? Timestamp);

public enum UnifiedThreadTranscriptStatus
{
    Available,
    Empty,
    SourceMissing,
    Unavailable
}

public sealed record UnifiedThreadTranscript(
    UnifiedThreadTranscriptStatus Status,
    IReadOnlyList<UnifiedThreadMessage> Messages,
    bool IsTruncated,
    int IgnoredMalformedLines,
    int IgnoredOversizedLines,
    string Notice);

/// <summary>
/// Reads a small, read-only transcript from a Codex rollout JSONL file.
/// It never starts a Codex client and never changes the SQLite database or JSONL file.
/// </summary>
public sealed class SharedThreadTranscriptService
{
    private const int DefaultMaxMessages = 80;
    private const int DefaultMaxMessageCharacters = 4000;
    private const long DefaultMaxSourceBytes = 32L * 1024 * 1024;
    private const int DefaultMaxJsonLineCharacters = 512 * 1024;
    private const int CompleteMaxProjectedJsonCharacters = 16 * 1024 * 1024;

    public UnifiedThreadTranscript Load(
        string codexHome,
        UnifiedThreadRecord thread,
        int maxMessages = DefaultMaxMessages,
        int maxMessageCharacters = DefaultMaxMessageCharacters)
    {
        ArgumentNullException.ThrowIfNull(thread);
        maxMessages = Math.Clamp(maxMessages, 1, 200);
        maxMessageCharacters = Math.Clamp(maxMessageCharacters, 80, 12_000);
        return LoadCore(
            codexHome,
            thread,
            maxMessages,
            maxMessageCharacters,
            DefaultMaxSourceBytes,
            DefaultMaxJsonLineCharacters);
    }

    /// <summary>
    /// Reads the complete user/assistant conversation for the interactive preview.
    /// The lightweight Load overload remains bounded for background full-text indexing.
    /// A bounded JSON projection omits embedded images and unrelated tool strings before
    /// parsing, so source records may be large while normal chat text is not clipped.
    /// </summary>
    public UnifiedThreadTranscript LoadComplete(
        string codexHome,
        UnifiedThreadRecord thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return LoadCore(
            codexHome,
            thread,
            int.MaxValue,
            int.MaxValue,
            long.MaxValue,
            CompleteMaxProjectedJsonCharacters,
            projectNonTranscriptStrings: true);
    }

    internal static void ValidateReader()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-thread-transcript-reader-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions", "2026", "07", "12");
        Directory.CreateDirectory(sessions);

        try
        {
            var threadId = "019f5c10-7f43-7a84-89c6-b94ba0c82451";
            var missingThreadId = "019f5c10-7f43-7a84-89c6-b94ba0c82452";
            var rolloutPath = Path.Combine(sessions, $"rollout-fixture-{threadId}.jsonl");
            var missingPath = Path.Combine(sessions, $"rollout-fixture-{missingThreadId}.jsonl");
            var startedAt = DateTimeOffset.Parse("2026-07-12T12:00:00Z");

            var fixtureLines = new[]
            {
                MakeResponseMessageFixture(startedAt.AddSeconds(20), "assistant", "later assistant"),
                "{malformed-json",
                MakeResponseMessageFixture(startedAt.AddSeconds(10), "user", "earlier user"),
                MakeEventMessageFixture(startedAt.AddSeconds(10.2), "user_message", "earlier user"),
                MakeEventMessageFixture(startedAt.AddSeconds(30), "agent_message", new string('x', 180)),
                MakeResponseMessageFixture(startedAt.AddSeconds(40), "developer", "must stay hidden"),
                JsonSerializer.Serialize(new { padding = new string('z', 800) })
            };
            File.WriteAllLines(rolloutPath, fixtureLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            CodexCliService.EnsureSqliteProvider();
            using (var connection = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = Path.Combine(root, "state_5.sqlite"),
                           Pooling = false
                       }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT);
                    INSERT INTO threads (id, rollout_path) VALUES ($id, $path);
                    INSERT INTO threads (id, rollout_path) VALUES ($missingId, $missingPath);
                    """;
                command.Parameters.AddWithValue("$id", threadId);
                command.Parameters.AddWithValue("$path", @"\\?\" + Path.GetFullPath(rolloutPath));
                command.Parameters.AddWithValue("$missingId", missingThreadId);
                command.Parameters.AddWithValue("$missingPath", missingPath);
                command.ExecuteNonQuery();
            }

            var thread = MakeFixtureThread(threadId);
            var transcript = new SharedThreadTranscriptService().LoadCore(
                root,
                thread,
                maxMessages: 10,
                maxMessageCharacters: 80,
                maxSourceBytes: 1024 * 1024,
                maxJsonLineCharacters: 512);
            if (transcript.Status != UnifiedThreadTranscriptStatus.Available ||
                transcript.Messages.Count != 3 ||
                transcript.Messages[0] is not { Role: UnifiedThreadMessageRole.User, Text: "earlier user" } ||
                transcript.Messages[1] is not { Role: UnifiedThreadMessageRole.Assistant, Text: "later assistant" } ||
                transcript.Messages[2].Text.Length > 80 ||
                !transcript.Messages[2].Text.EndsWith('…') ||
                transcript.IgnoredMalformedLines != 1 ||
                transcript.IgnoredOversizedLines != 1 ||
                !transcript.IsTruncated)
            {
                throw new InvalidOperationException(
                    "Read-only thread transcript parsing, ordering, deduplication or bounds validation failed.");
            }

            var limited = new SharedThreadTranscriptService().LoadCore(
                root,
                thread,
                maxMessages: 2,
                maxMessageCharacters: 80,
                maxSourceBytes: 1024 * 1024,
                maxJsonLineCharacters: 512);
            if (limited.Messages.Count != 2 ||
                limited.Messages[0].Text != "later assistant" ||
                !limited.IsTruncated)
            {
                throw new InvalidOperationException("Thread transcript message-count limit validation failed.");
            }

            var missing = new SharedThreadTranscriptService().Load(
                root,
                MakeFixtureThread(missingThreadId));
            if (missing.Status != UnifiedThreadTranscriptStatus.SourceMissing ||
                missing.Messages.Count != 0)
            {
                throw new InvalidOperationException("Missing thread transcript validation failed.");
            }

            var tailThreadId = "019f5c10-7f43-7a84-89c6-b94ba0c82453";
            var tailPath = Path.Combine(sessions, $"rollout-fixture-{tailThreadId}.jsonl");
            File.WriteAllLines(
                tailPath,
                [
                    MakeEventMessageFixture(startedAt, "user_message", new string('q', 500)),
                    MakeEventMessageFixture(startedAt.AddSeconds(1), "agent_message", "latest reply")
                ],
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AddFixtureThread(root, tailThreadId, tailPath);
            var tail = new SharedThreadTranscriptService().LoadCore(
                root,
                MakeFixtureThread(tailThreadId),
                maxMessages: 10,
                maxMessageCharacters: 80,
                maxSourceBytes: 220,
                maxJsonLineCharacters: 256);
            if (tail.Status != UnifiedThreadTranscriptStatus.Available ||
                tail.Messages.Count != 1 ||
                tail.Messages[0].Text != "latest reply" ||
                !tail.IsTruncated)
            {
                throw new InvalidOperationException("Oversized transcript tail-reading validation failed.");
            }

            var completeTail = new SharedThreadTranscriptService().LoadComplete(
                root,
                MakeFixtureThread(tailThreadId));
            if (completeTail.Status != UnifiedThreadTranscriptStatus.Available ||
                completeTail.Messages.Count != 2 ||
                completeTail.Messages[0].Text.Length != 500 ||
                completeTail.Messages[1].Text != "latest reply" ||
                completeTail.IsTruncated)
            {
                throw new InvalidOperationException(
                    "Complete transcript reading retained the background tail limit.");
            }

            var completeThreadId = "019f5c10-7f43-7a84-89c6-b94ba0c82454";
            var completePath = Path.Combine(
                sessions,
                $"rollout-fixture-{completeThreadId}.jsonl");
            WriteCompleteTranscriptFixture(
                completePath,
                startedAt.AddSeconds(100),
                firstMessageText: new string('完', 13_000),
                imageCharacters: checked((int)DefaultMaxSourceBytes + 1024 * 1024),
                messageCount: 205);
            AddFixtureThread(root, completeThreadId, completePath);
            var complete = new SharedThreadTranscriptService().LoadComplete(
                root,
                MakeFixtureThread(completeThreadId));
            var boundedComplete = new SharedThreadTranscriptService().Load(
                root,
                MakeFixtureThread(completeThreadId),
                maxMessages: 160,
                maxMessageCharacters: 6000);
            if (new FileInfo(completePath).Length <= DefaultMaxSourceBytes ||
                complete.Status != UnifiedThreadTranscriptStatus.Available ||
                complete.Messages.Count != 205 ||
                complete.Messages[0].Text.Length != 13_000 ||
                complete.Messages[^1].Text != "complete message 204" ||
                complete.IsTruncated ||
                !complete.Notice.Contains("完整读取", StringComparison.Ordinal) ||
                boundedComplete.Messages.Count != 160 ||
                boundedComplete.Messages[0].Text != "complete message 45" ||
                !boundedComplete.IsTruncated)
            {
                throw new InvalidOperationException(
                    "Complete transcript reading clipped a large-image message, count, text, or history order; " +
                    "or it removed the background reader bounds.");
            }

            var partialThreadId = "019f5c10-7f43-7a84-89c6-b94ba0c82456";
            var partialPath = Path.Combine(sessions, $"rollout-fixture-{partialThreadId}.jsonl");
            File.WriteAllText(
                partialPath,
                MakeResponseMessageFixture(startedAt, "user", "complete before partial") +
                Environment.NewLine +
                "{\"timestamp\":\"2026-07-12T12:00:01Z\",\"type\":\"response_item\",\"payload\":",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AddFixtureThread(root, partialThreadId, partialPath);
            var partial = new SharedThreadTranscriptService().LoadComplete(
                root,
                MakeFixtureThread(partialThreadId));
            if (partial.Messages is not [{ Text: "complete before partial" }] ||
                partial.IgnoredMalformedLines != 1 ||
                !partial.IsTruncated ||
                partial.Notice.Contains("已完整读取", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A partially written active rollout was incorrectly reported as complete.");
            }

            var repeatedRealMessages = Deduplicate(
            [
                new MessageCandidate(
                    new UnifiedThreadMessage(
                        UnifiedThreadMessageRole.User,
                        "repeat exactly",
                        startedAt),
                    1,
                    2),
                new MessageCandidate(
                    new UnifiedThreadMessage(
                        UnifiedThreadMessageRole.User,
                        "repeat exactly",
                        startedAt.AddSeconds(1)),
                    2,
                    2)
            ]);
            var duplicateRepresentations = Deduplicate(
            [
                new MessageCandidate(
                    new UnifiedThreadMessage(
                        UnifiedThreadMessageRole.User,
                        "one logical message",
                        startedAt),
                    1,
                    1),
                new MessageCandidate(
                    new UnifiedThreadMessage(
                        UnifiedThreadMessageRole.User,
                        "one logical message",
                        startedAt.AddMilliseconds(200)),
                    2,
                    2)
            ]);
            if (repeatedRealMessages.Count != 2 ||
                duplicateRepresentations is not [{ Priority: 2 }])
            {
                throw new InvalidOperationException(
                    "Transcript deduplication removed a genuine repeated user message.");
            }

            ValidateConversationFiltering();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // A temporary fixture still held by an antivirus must not hide the validation result.
            }
        }
    }

    private UnifiedThreadTranscript LoadCore(
        string codexHome,
        UnifiedThreadRecord thread,
        int maxMessages,
        int maxMessageCharacters,
        long maxSourceBytes,
        int maxJsonLineCharacters,
        bool projectNonTranscriptStrings = false)
    {
        string home;
        try
        {
            home = Path.GetFullPath(codexHome);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unavailable("聊天目录无效，无法读取聊天正文。");
        }

        if (!Guid.TryParse(thread.Id, out _))
        {
            return Unavailable("聊天记录 ID 无效，无法读取聊天正文。");
        }

        string? rolloutPath;
        try
        {
            rolloutPath = ResolveRolloutPath(home, thread.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return Unavailable("暂时无法读取这条聊天的本地索引。");
        }

        if (string.IsNullOrWhiteSpace(rolloutPath))
        {
            return Missing();
        }

        string fullRolloutPath;
        try
        {
            fullRolloutPath = Path.IsPathFullyQualified(rolloutPath)
                ? Path.GetFullPath(rolloutPath)
                : Path.GetFullPath(Path.Combine(home, rolloutPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Missing();
        }

        if (!IsInsideDirectory(fullRolloutPath, home) ||
            !Path.GetExtension(fullRolloutPath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullRolloutPath))
        {
            return Missing();
        }

        try
        {
            return ReadRollout(
                fullRolloutPath,
                maxMessages,
                maxMessageCharacters,
                Math.Max(1, maxSourceBytes),
                Math.Max(128, maxJsonLineCharacters),
                projectNonTranscriptStrings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Unavailable("暂时无法读取这条聊天的本地会话文件。");
        }
    }

    private static UnifiedThreadTranscript ReadRollout(
        string path,
        int maxMessages,
        int maxMessageCharacters,
        long maxSourceBytes,
        int maxJsonLineCharacters,
        bool projectNonTranscriptStrings)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var startsAt = maxSourceBytes >= stream.Length
            ? 0
            : stream.Length - maxSourceBytes;
        var sourceWasTailTruncated = startsAt > 0;
        var discardPartialFirstLine = false;
        if (startsAt > 0)
        {
            stream.Position = startsAt - 1;
            discardPartialFirstLine = stream.ReadByte() != (byte)'\n';
        }
        stream.Position = startsAt;

        var malformedLines = 0;
        var oversizedLines = 0;
        var textWasTruncated = false;
        var sequence = 0L;
        var candidates = new List<MessageCandidate>();

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);
        var isFirstLine = true;
        var lines = projectNonTranscriptStrings
            ? ReadProjectedJsonLines(reader, maxJsonLineCharacters)
            : ReadBoundedLines(reader, maxJsonLineCharacters);
        foreach (var line in lines)
        {
            if (isFirstLine && discardPartialFirstLine)
            {
                isFirstLine = false;
                continue;
            }
            isFirstLine = false;

            if (line.Oversized)
            {
                oversizedLines++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            sequence++;
            try
            {
                if (TryParseMessage(
                        line.Text,
                        sequence,
                        maxMessageCharacters,
                        out var candidate,
                        out var candidateWasTruncated))
                {
                    candidates.Add(candidate);
                    textWasTruncated |= candidateWasTruncated;
                }
            }
            catch (JsonException)
            {
                malformedLines++;
            }
        }

        var ordered = candidates
            .OrderBy(candidate => candidate.Message.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Sequence)
            .ToList();
        var deduplicated = Deduplicate(ordered);
        var messageLimitReached = deduplicated.Count > maxMessages;
        if (messageLimitReached)
        {
            deduplicated = deduplicated.TakeLast(maxMessages).ToList();
        }

        var messages = deduplicated.Select(candidate => candidate.Message).ToList();
        var isTruncated = sourceWasTailTruncated ||
                          messageLimitReached ||
                          textWasTruncated ||
                          oversizedLines > 0 ||
                          malformedLines > 0;
        if (messages.Count == 0)
        {
            return new UnifiedThreadTranscript(
                UnifiedThreadTranscriptStatus.Empty,
                [],
                isTruncated,
                malformedLines,
                oversizedLines,
                "会话文件存在，但没有可显示的用户或助手正文。");
        }

        return new UnifiedThreadTranscript(
            UnifiedThreadTranscriptStatus.Available,
            messages,
            isTruncated,
            malformedLines,
            oversizedLines,
            isTruncated
                ? $"已读取 {messages.Count} 条消息；部分内容仍在写入、格式异常或受安全限制，未能完整显示。"
                : $"已完整读取 {messages.Count} 条消息。");
    }

    private static string? ResolveRolloutPath(string home, string threadId)
    {
        var databasePath = Path.Combine(home, "state_5.sqlite");
        if (File.Exists(databasePath))
        {
            CodexCliService.EnsureSqliteProvider();
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString());
            connection.Open();
            if (TableHasColumn(connection, "threads", "rollout_path"))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT rollout_path FROM threads WHERE id = $id LIMIT 1;";
                command.Parameters.AddWithValue("$id", threadId);
                var value = Convert.ToString(command.ExecuteScalar());
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(ToCandidatePath(home, value)))
                {
                    return value;
                }
            }
        }

        return FindNewestRolloutByThreadId(home, threadId);
    }

    private static string? FindNewestRolloutByThreadId(string home, string threadId)
    {
        var candidates = new List<string>();
        foreach (var directoryName in new[] { "sessions", "archived_sessions", "account-switcher-conflicts" })
        {
            var root = Path.Combine(home, directoryName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                candidates.AddRange(Directory
                    .EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileNameWithoutExtension(path)
                        .Contains(threadId, StringComparison.OrdinalIgnoreCase))
                    .Take(16));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A partially inaccessible archive should not prevent another root from being checked.
            }
        }

        return candidates
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
    }

    private static string ToCandidatePath(string home, string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(home, path));
        }
        catch
        {
            return "";
        }
    }

    private static bool TableHasColumn(SqliteConnection connection, string table, string column)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        tableCommand.Parameters.AddWithValue("$name", table);
        if (tableCommand.ExecuteScalar() == null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseMessage(
        string line,
        long sequence,
        int maxMessageCharacters,
        out MessageCandidate candidate,
        out bool textWasTruncated)
    {
        candidate = default;
        textWasTruncated = false;
        using var document = JsonDocument.Parse(
            line,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 48
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var rootType = ReadString(root, "type");
        var payload = TryGetObject(root, "payload");
        var timestamp = ReadTimestamp(root, payload);
        UnifiedThreadMessageRole role;
        string text;
        var priority = 0;

        if (rootType.Equals("response_item", StringComparison.OrdinalIgnoreCase) &&
            payload.HasValue &&
            ReadString(payload.Value, "type").Equals("message", StringComparison.OrdinalIgnoreCase) &&
            TryReadRole(payload.Value, out role))
        {
            text = ReadMessageText(payload.Value);
            priority = 2;
        }
        else if (rootType.Equals("event_msg", StringComparison.OrdinalIgnoreCase) && payload.HasValue)
        {
            var eventType = ReadString(payload.Value, "type");
            if (eventType.Equals("user_message", StringComparison.OrdinalIgnoreCase))
            {
                role = UnifiedThreadMessageRole.User;
            }
            else if (eventType.Equals("agent_message", StringComparison.OrdinalIgnoreCase) ||
                     eventType.Equals("assistant_message", StringComparison.OrdinalIgnoreCase))
            {
                role = UnifiedThreadMessageRole.Assistant;
            }
            else
            {
                return false;
            }
            text = ReadMessageText(payload.Value);
            priority = 1;
        }
        else if (rootType.Equals("message", StringComparison.OrdinalIgnoreCase) &&
                 TryReadRole(root, out role))
        {
            text = ReadMessageText(root);
        }
        else
        {
            return false;
        }

        text = NormalizeMessageText(text);
        text = FilterConversationText(role, text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = LimitText(text, maxMessageCharacters, out textWasTruncated);
        candidate = new MessageCandidate(
            new UnifiedThreadMessage(role, text, timestamp),
            sequence,
            priority);
        return true;
    }

    private static bool TryReadRole(JsonElement element, out UnifiedThreadMessageRole role)
    {
        var value = ReadString(element, "role");
        if (value.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            role = UnifiedThreadMessageRole.User;
            return true;
        }
        if (value.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            role = UnifiedThreadMessageRole.Assistant;
            return true;
        }

        role = default;
        return false;
    }

    private static string ReadMessageText(JsonElement element)
    {
        foreach (var propertyName in new[] { "message", "text", "content" })
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            var text = ReadTextValue(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return "";
    }

    private static string ReadTextValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : "";
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var direct = item.GetString();
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    parts.Add(direct);
                }
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var contentType = ReadString(item, "type");
            if (string.IsNullOrWhiteSpace(contentType) ||
                contentType.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("input_text", StringComparison.OrdinalIgnoreCase) ||
                contentType.Equals("output_text", StringComparison.OrdinalIgnoreCase))
            {
                var part = text.GetString();
                if (!string.IsNullOrWhiteSpace(part))
                {
                    parts.Add(part);
                }
            }
        }
        return string.Join(Environment.NewLine, parts);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, JsonElement? payload)
    {
        var value = ReadString(root, "timestamp");
        if (string.IsNullOrWhiteSpace(value) && payload.HasValue)
        {
            value = ReadString(payload.Value, "timestamp");
        }
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string NormalizeMessageText(string value)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        while (value.Contains("\n\n\n", StringComparison.Ordinal))
        {
            value = value.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }
        return value;
    }

    /// <summary>
    /// Codex stores some orchestrator context as role=user messages so that the model can
    /// reconstruct a turn. Those entries are useful to the runtime but are not part of the
    /// human conversation. Keep the original rollout untouched and remove only well-known,
    /// leading machine-generated envelopes from this read-only view.
    /// </summary>
    private static string FilterConversationText(UnifiedThreadMessageRole role, string value)
    {
        if (role != UnifiedThreadMessageRole.User || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var text = value.Trim();
        if (text.StartsWith(
                "Another language model started to solve this problem and produced a summary of its thinking process.",
                StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var changed = true;
        while (changed && !string.IsNullOrWhiteSpace(text))
        {
            changed = false;
            var trimmed = text.TrimStart();

            if (trimmed.StartsWith("# Files mentioned by the user:", StringComparison.OrdinalIgnoreCase))
            {
                const string requestMarker = "## My request for Codex:";
                var requestIndex = trimmed.IndexOf(requestMarker, StringComparison.OrdinalIgnoreCase);
                if (requestIndex >= 0)
                {
                    text = trimmed[(requestIndex + requestMarker.Length)..].TrimStart();
                    changed = true;
                    continue;
                }
            }

            if (trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase))
            {
                text = RemoveLeadingBlock(trimmed, "</INSTRUCTIONS>", removeAllWhenUnclosed: true);
                changed = true;
                continue;
            }

            foreach (var tag in new[]
                     {
                         "INSTRUCTIONS",
                         "environment_context",
                         "permissions instructions",
                         "app-context",
                         "collaboration_mode",
                         "skills_instructions",
                         "apps_instructions",
                         "plugins_instructions",
                         "multi_agent_mode",
                         "turn_aborted"
                     })
            {
                var openingTag = "<" + tag;
                if (!trimmed.StartsWith(openingTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                text = RemoveLeadingBlock(trimmed, "</" + tag + ">", removeAllWhenUnclosed: true);
                changed = true;
                break;
            }
        }

        return NormalizeMessageText(text);
    }

    private static string RemoveLeadingBlock(
        string value,
        string closingMarker,
        bool removeAllWhenUnclosed)
    {
        var end = value.IndexOf(closingMarker, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return removeAllWhenUnclosed ? "" : value;
        }

        return value[(end + closingMarker.Length)..].TrimStart();
    }

    private static void ValidateConversationFiltering()
    {
        const string internalContext = """
            # AGENTS.md instructions

            <INSTRUCTIONS>
            hidden language policy
            </INSTRUCTIONS>
            <environment_context>
              <cwd>C:\fixture</cwd>
            </environment_context>
            """;
        const string attachmentWrappedRequest = """
            # Files mentioned by the user:

            ## screenshot.png: C:\Temp\screenshot.png

            ## My request for Codex:
            只显示真正的对话
            """;

        if (!string.IsNullOrEmpty(FilterConversationText(
                UnifiedThreadMessageRole.User,
                internalContext)) ||
            FilterConversationText(
                UnifiedThreadMessageRole.User,
                attachmentWrappedRequest) != "只显示真正的对话" ||
            FilterConversationText(
                UnifiedThreadMessageRole.Assistant,
                "assistant may explain <environment_context> literally") !=
            "assistant may explain <environment_context> literally")
        {
            throw new InvalidOperationException(
                "Thread transcript machine-context filtering validation failed.");
        }
    }

    private static string LimitText(string value, int maxCharacters, out bool truncated)
    {
        truncated = value.Length > maxCharacters;
        if (!truncated)
        {
            return value;
        }

        var end = Math.Max(1, maxCharacters - 1);
        if (end < value.Length && end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }
        return value[..Math.Max(1, end)].TrimEnd() + "…";
    }

    private static List<MessageCandidate> Deduplicate(IReadOnlyList<MessageCandidate> ordered)
    {
        var result = new List<MessageCandidate>(ordered.Count);
        foreach (var candidate in ordered)
        {
            if (result.Count > 0 && AreDuplicate(result[^1], candidate))
            {
                if (candidate.Priority > result[^1].Priority)
                {
                    result[^1] = candidate;
                }
                continue;
            }
            result.Add(candidate);
        }
        return result;
    }

    private static bool AreDuplicate(MessageCandidate left, MessageCandidate right)
    {
        if (left.Priority == right.Priority ||
            left.Message.Role != right.Message.Role ||
            !left.Message.Text.Equals(right.Message.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.Message.Timestamp.HasValue && right.Message.Timestamp.HasValue)
        {
            return (right.Message.Timestamp.Value - left.Message.Timestamp.Value).Duration() <= TimeSpan.FromSeconds(3);
        }
        return Math.Abs(right.Sequence - left.Sequence) <= 3;
    }

    private static IEnumerable<BoundedLine> ReadBoundedLines(TextReader reader, int maxCharacters)
    {
        var buffer = new char[8192];
        var line = new StringBuilder(Math.Min(maxCharacters, 4096));
        var oversized = false;
        while (true)
        {
            var count = reader.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }

            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }
                    yield return new BoundedLine(oversized ? null : line.ToString(), oversized);
                    line.Clear();
                    oversized = false;
                    continue;
                }

                if (!oversized && line.Length < maxCharacters)
                {
                    line.Append(character);
                }
                else
                {
                    oversized = true;
                }
            }
        }

        if (line.Length > 0 || oversized)
        {
            if (line.Length > 0 && line[^1] == '\r')
            {
                line.Length--;
            }
            yield return new BoundedLine(oversized ? null : line.ToString(), oversized);
        }
    }

    /// <summary>
    /// Builds a bounded JSON projection instead of bounding the source record itself.
    /// Codex user messages may embed a multi-megabyte data URL beside a very small text
    /// request. Keeping only transcript-relevant string values lets the complete preview
    /// recover that text without retaining the image or an arbitrarily large tool payload.
    /// </summary>
    private static IEnumerable<BoundedLine> ReadProjectedJsonLines(
        TextReader reader,
        int maxProjectedCharacters)
    {
        var buffer = new char[8192];
        var projection = new ProjectedJsonLineBuilder(maxProjectedCharacters);
        while (true)
        {
            var count = reader.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }

            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\n')
                {
                    yield return projection.Build();
                    projection = new ProjectedJsonLineBuilder(maxProjectedCharacters);
                    continue;
                }

                projection.Append(character);
            }
        }

        if (projection.HasInput)
        {
            yield return projection.Build();
        }
    }

    private sealed class ProjectedJsonLineBuilder
    {
        private const int MaxRememberedPropertyNameCharacters = 256;
        private static readonly HashSet<string> PreservedStringProperties = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "timestamp",
            "type",
            "role",
            "message",
            "text",
            "content"
        };

        private readonly int _maxProjectedCharacters;
        private readonly StringBuilder _projected;
        private readonly Stack<JsonProjectionFrame> _frames = new();
        private StringBuilder? _propertyNameRaw;
        private bool _propertyNameTooLong;
        private bool _inString;
        private bool _escaped;
        private bool _currentStringIsProperty;
        private bool _preserveCurrentString;
        private bool _projectionExceeded;

        internal ProjectedJsonLineBuilder(int maxProjectedCharacters)
        {
            _maxProjectedCharacters = Math.Max(128, maxProjectedCharacters);
            _projected = new StringBuilder(Math.Min(_maxProjectedCharacters, 4096));
        }

        internal bool HasInput { get; private set; }

        internal void Append(char character)
        {
            HasInput = true;
            if (_inString)
            {
                AppendStringCharacter(character);
                return;
            }

            switch (character)
            {
                case '"':
                    BeginString();
                    return;
                case '{':
                    AppendProjected(character);
                    _frames.Push(new JsonProjectionFrame(
                        isObject: true,
                        ownerPropertyName: CurrentValuePropertyName()));
                    return;
                case '[':
                    AppendProjected(character);
                    _frames.Push(new JsonProjectionFrame(
                        isObject: false,
                        ownerPropertyName: CurrentValuePropertyName()));
                    return;
                case '}':
                case ']':
                    AppendProjected(character);
                    if (_frames.Count > 0)
                    {
                        _frames.Pop();
                    }
                    return;
                case ':':
                    AppendProjected(character);
                    if (_frames.TryPeek(out var propertyFrame) && propertyFrame.IsObject)
                    {
                        propertyFrame.ExpectPropertyName = false;
                    }
                    return;
                case ',':
                    AppendProjected(character);
                    if (_frames.TryPeek(out var nextFrame) && nextFrame.IsObject)
                    {
                        nextFrame.ExpectPropertyName = true;
                        nextFrame.CurrentPropertyName = null;
                    }
                    return;
                default:
                    AppendProjected(character);
                    return;
            }
        }

        internal BoundedLine Build()
        {
            if (!_inString && _projected.Length > 0 && _projected[^1] == '\r')
            {
                _projected.Length--;
            }

            return new BoundedLine(
                _projectionExceeded ? null : _projected.ToString(),
                _projectionExceeded);
        }

        private void BeginString()
        {
            _inString = true;
            _escaped = false;
            var frame = _frames.TryPeek(out var current) ? current : null;
            _currentStringIsProperty = frame is { IsObject: true, ExpectPropertyName: true };
            _preserveCurrentString = _currentStringIsProperty || ShouldPreserveStringValue(frame);
            _propertyNameTooLong = false;
            _propertyNameRaw = _currentStringIsProperty ? new StringBuilder(32) : null;
            AppendProjected('"');
        }

        private void AppendStringCharacter(char character)
        {
            if (_escaped)
            {
                RememberPropertyNameCharacter(character);
                if (_preserveCurrentString)
                {
                    AppendProjected(character);
                }
                _escaped = false;
                return;
            }

            if (character == '\\')
            {
                RememberPropertyNameCharacter(character);
                if (_preserveCurrentString)
                {
                    AppendProjected(character);
                }
                _escaped = true;
                return;
            }

            if (character == '"')
            {
                AppendProjected(character);
                _inString = false;
                if (_currentStringIsProperty &&
                    _frames.TryPeek(out var frame) &&
                    frame.IsObject)
                {
                    frame.CurrentPropertyName = DecodePropertyName();
                }
                _propertyNameRaw = null;
                return;
            }

            RememberPropertyNameCharacter(character);
            if (_preserveCurrentString)
            {
                AppendProjected(character);
            }
        }

        private string? CurrentValuePropertyName()
        {
            if (!_frames.TryPeek(out var frame))
            {
                return null;
            }

            return frame.IsObject ? frame.CurrentPropertyName : frame.OwnerPropertyName;
        }

        private static bool ShouldPreserveStringValue(JsonProjectionFrame? frame)
        {
            if (frame == null)
            {
                return false;
            }

            var propertyName = frame.IsObject
                ? frame.CurrentPropertyName
                : frame.OwnerPropertyName;
            return !string.IsNullOrWhiteSpace(propertyName) &&
                   PreservedStringProperties.Contains(propertyName);
        }

        private void RememberPropertyNameCharacter(char character)
        {
            if (!_currentStringIsProperty || _propertyNameRaw == null || _propertyNameTooLong)
            {
                return;
            }

            if (_propertyNameRaw.Length >= MaxRememberedPropertyNameCharacters)
            {
                _propertyNameTooLong = true;
                _propertyNameRaw.Clear();
                return;
            }

            _propertyNameRaw.Append(character);
        }

        private string? DecodePropertyName()
        {
            if (_propertyNameTooLong || _propertyNameRaw == null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<string>("\"" + _propertyNameRaw + "\"");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void AppendProjected(char character)
        {
            if (_projectionExceeded)
            {
                return;
            }

            if (_projected.Length >= _maxProjectedCharacters)
            {
                _projectionExceeded = true;
                return;
            }

            _projected.Append(character);
        }
    }

    private sealed class JsonProjectionFrame(bool isObject, string? ownerPropertyName)
    {
        internal bool IsObject { get; } = isObject;

        internal string? OwnerPropertyName { get; } = ownerPropertyName;

        internal bool ExpectPropertyName { get; set; } = isObject;

        internal string? CurrentPropertyName { get; set; }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var root = NormalizeComparablePath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = NormalizeComparablePath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPathPrefix = @"\\?\";
        if (fullPath.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + fullPath[extendedUncPrefix.Length..];
        }
        return fullPath.StartsWith(extendedPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[extendedPathPrefix.Length..]
            : fullPath;
    }

    private static UnifiedThreadTranscript Missing()
    {
        return new UnifiedThreadTranscript(
            UnifiedThreadTranscriptStatus.SourceMissing,
            [],
            false,
            0,
            0,
            "找不到这条聊天的本地会话文件。");
    }

    private static UnifiedThreadTranscript Unavailable(string notice)
    {
        return new UnifiedThreadTranscript(
            UnifiedThreadTranscriptStatus.Unavailable,
            [],
            false,
            0,
            0,
            notice);
    }

    private static string MakeResponseMessageFixture(
        DateTimeOffset timestamp,
        string role,
        string text)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "response_item",
            payload = new
            {
                type = "message",
                role,
                content = new[] { new { type = role == "assistant" ? "output_text" : "input_text", text } }
            }
        });
    }

    private static string MakeEventMessageFixture(
        DateTimeOffset timestamp,
        string eventType,
        string message)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            type = "event_msg",
            payload = new { type = eventType, message }
        });
    }

    private static void WriteCompleteTranscriptFixture(
        string path,
        DateTimeOffset startedAt,
        string firstMessageText,
        int imageCharacters,
        int messageCount)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 64 * 1024,
            leaveOpen: false)
        {
            NewLine = "\n"
        };

        writer.Write("{\"timestamp\":");
        writer.Write(JsonSerializer.Serialize(startedAt.ToString("O")));
        writer.Write(",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":");
        writer.Write(JsonSerializer.Serialize(firstMessageText));
        writer.Write("},{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,");
        var imageChunk = new string('A', 64 * 1024);
        var remaining = Math.Max(0, imageCharacters);
        while (remaining > 0)
        {
            var count = Math.Min(remaining, imageChunk.Length);
            writer.Write(imageChunk.AsSpan(0, count));
            remaining -= count;
        }
        writer.Write("\",\"detail\":\"auto\"}]}}");
        writer.WriteLine();

        for (var index = 1; index < messageCount; index++)
        {
            writer.WriteLine(MakeResponseMessageFixture(
                startedAt.AddSeconds(index),
                index % 2 == 0 ? "user" : "assistant",
                $"complete message {index}"));
        }
    }

    private static UnifiedThreadRecord MakeFixtureThread(string id)
    {
        return new UnifiedThreadRecord(
            id,
            "Synthetic thread",
            "Synthetic preview",
            @"C:\synthetic",
            "synthetic-model",
            "synthetic-provider",
            DateTimeOffset.Parse("2026-07-12T12:00:00Z"),
            Archived: false,
            HasUserEvent: true);
    }

    private static void AddFixtureThread(string root, string id, string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "state_5.sqlite"),
                Pooling = false
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO threads (id, rollout_path) VALUES ($id, $path);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private readonly record struct MessageCandidate(
        UnifiedThreadMessage Message,
        long Sequence,
        int Priority);

    private readonly record struct BoundedLine(string? Text, bool Oversized);
}
