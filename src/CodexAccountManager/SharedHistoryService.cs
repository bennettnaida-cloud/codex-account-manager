using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodexAccountManager;

public sealed record UnifiedThreadRecord(
    string Id,
    string Title,
    string Preview,
    string WorkingDirectory,
    string Model,
    string Provider,
    DateTimeOffset UpdatedAt,
    bool Archived,
    bool HasUserEvent);

public sealed class SharedHistoryService
{
    private const string DeletedThreadsFileName = "account-manager-deleted-threads.json";
    private static readonly object DeletedThreadsLock = new();

    public IReadOnlyList<UnifiedThreadRecord> Load(string codexHome, int limit = 5000)
    {
        var databasePath = Path.Combine(Path.GetFullPath(codexHome), "state_5.sqlite");
        if (!File.Exists(databasePath))
        {
            return [];
        }

        var deletedThreadIds = LoadDeletedThreadIds(codexHome);

        CodexCliService.EnsureSqliteProvider();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        if (!TableExists(connection, "threads"))
        {
            return [];
        }

        var columns = ReadColumns(connection, "threads");
        if (!columns.Contains("id"))
        {
            return [];
        }

        var updatedMilliseconds = BuildUpdatedMillisecondsExpression(columns);
        var visibleThreadFilter = BuildVisibleThreadFilter(columns);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {TextColumn(columns, "id")} AS id,
                {TextColumn(columns, "title")} AS title,
                {TextColumn(columns, "preview")} AS preview,
                {TextColumn(columns, "first_user_message")} AS first_user_message,
                {TextColumn(columns, "cwd")} AS cwd,
                {TextColumn(columns, "model")} AS model,
                {TextColumn(columns, "model_provider")} AS model_provider,
                {TextColumn(columns, "source")} AS source,
                {TextColumn(columns, "agent_path")} AS agent_path,
                {IntegerColumn(columns, "archived")} AS archived,
                {IntegerColumn(columns, "has_user_event")} AS has_user_event,
                {updatedMilliseconds} AS updated_ms
            FROM threads
            WHERE {visibleThreadFilter}
            ORDER BY updated_ms DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));

        var result = new List<UnifiedThreadRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, 0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            if (deletedThreadIds.Contains(id))
            {
                continue;
            }

            var source = ReadText(reader, 7);
            var agentPath = ReadText(reader, 8);
            if (!string.IsNullOrWhiteSpace(agentPath) ||
                source.Contains("\"subagent\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = NormalizeLine(ReadText(reader, 1));
            var preview = NormalizeLine(ReadText(reader, 2));
            var firstUserMessage = NormalizeLine(ReadText(reader, 3));
            var hasUserEvent = !reader.IsDBNull(10) && reader.GetInt64(10) != 0;
            if (!hasUserEvent &&
                string.IsNullOrWhiteSpace(title) &&
                string.IsNullOrWhiteSpace(preview) &&
                string.IsNullOrWhiteSpace(firstUserMessage))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = FirstNonEmpty(firstUserMessage, preview, "未命名任务");
            }
            if (string.IsNullOrWhiteSpace(preview))
            {
                preview = firstUserMessage;
            }

            var updatedMillisecondsValue = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
            result.Add(new UnifiedThreadRecord(
                id,
                title,
                preview,
                NormalizePathForDisplay(ReadText(reader, 4)),
                ReadText(reader, 5),
                ReadText(reader, 6),
                FromUnixMilliseconds(updatedMillisecondsValue),
                !reader.IsDBNull(9) && reader.GetInt64(9) != 0,
                hasUserEvent));
        }

        return result;
    }

    internal IReadOnlyList<UnifiedThreadRecord> ReconcileWithCodex(
        string codexHome,
        IReadOnlyList<UnifiedThreadRecord> indexedThreads,
        IReadOnlyList<CodexThreadSummary> codexThreads)
    {
        var deletedThreadIds = LoadDeletedThreadIds(codexHome);
        var indexedById = indexedThreads.ToDictionary(
            thread => thread.Id,
            StringComparer.OrdinalIgnoreCase);
        var result = new List<UnifiedThreadRecord>(codexThreads.Count);
        foreach (var codexThread in codexThreads)
        {
            if (deletedThreadIds.Contains(codexThread.Id))
            {
                continue;
            }

            indexedById.TryGetValue(codexThread.Id, out var indexed);
            result.Add(new UnifiedThreadRecord(
                codexThread.Id,
                FirstNonEmpty(
                    codexThread.Name,
                    indexed?.Title ?? string.Empty,
                    codexThread.Preview,
                    "未命名任务"),
                FirstNonEmptyOrEmpty(codexThread.Preview, indexed?.Preview ?? string.Empty),
                FirstNonEmptyOrEmpty(
                    codexThread.WorkingDirectory,
                    indexed?.WorkingDirectory ?? string.Empty),
                indexed?.Model ?? string.Empty,
                FirstNonEmptyOrEmpty(
                    codexThread.ModelProvider,
                    indexed?.Provider ?? string.Empty),
                codexThread.UpdatedAt != DateTimeOffset.MinValue
                    ? codexThread.UpdatedAt
                    : indexed?.UpdatedAt ?? DateTimeOffset.MinValue,
                codexThread.Archived,
                indexed?.HasUserEvent ?? true));
        }

        return result
            .OrderByDescending(thread => thread.UpdatedAt)
            .ThenByDescending(thread => thread.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool ContainsThread(string codexHome, string threadId)
    {
        if (!Guid.TryParse(threadId, out _))
        {
            return false;
        }

        var databasePath = Path.Combine(Path.GetFullPath(codexHome), "state_5.sqlite");
        if (!File.Exists(databasePath))
        {
            return false;
        }

        CodexCliService.EnsureSqliteProvider();
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 10
            }.ToString());
        connection.Open();
        if (!TableExists(connection, "threads"))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM threads WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", threadId);
        return command.ExecuteScalar() != null;
    }

    public void RecordDeletedThread(string codexHome, string threadId)
    {
        if (!Guid.TryParse(threadId, out _))
        {
            throw new ArgumentException("Codex 任务 ID 无效。", nameof(threadId));
        }

        lock (DeletedThreadsLock)
        {
            var deleted = LoadDeletedThreadIds(codexHome);
            if (!deleted.Add(threadId))
            {
                return;
            }

            var path = Path.Combine(Path.GetFullPath(codexHome), DeletedThreadsFileName);
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    tempPath,
                    JsonSerializer.Serialize(deleted.OrderBy(id => id), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }

    public void RemoveDeletedThreadRecord(string codexHome, string threadId)
    {
        if (!Guid.TryParse(threadId, out _))
        {
            throw new ArgumentException("Codex 任务 ID 无效。", nameof(threadId));
        }

        lock (DeletedThreadsLock)
        {
            var deleted = LoadDeletedThreadIds(codexHome);
            if (!deleted.Remove(threadId))
            {
                return;
            }

            var path = Path.Combine(Path.GetFullPath(codexHome), DeletedThreadsFileName);
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    tempPath,
                    JsonSerializer.Serialize(deleted.OrderBy(id => id), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }

    internal static HashSet<string> LoadDeletedThreadIds(string codexHome)
    {
        var path = Path.Combine(Path.GetFullPath(codexHome), DeletedThreadsFileName);
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
            return ids
                .Where(id => Guid.TryParse(id, out _))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static void ValidateReader()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-shared-history-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CodexCliService.EnsureSqliteProvider();
            var databasePath = Path.Combine(root, "state_5.sqlite");
            using (var connection = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = databasePath,
                           Pooling = false
                       }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE threads (
                        id TEXT PRIMARY KEY,
                        title TEXT,
                        preview TEXT,
                        first_user_message TEXT,
                        cwd TEXT,
                        model TEXT,
                        model_provider TEXT,
                        source TEXT,
                        agent_path TEXT,
                        archived INTEGER,
                        has_user_event INTEGER,
                        updated_at_ms INTEGER
                    );
                    INSERT INTO threads VALUES (
                        '019f4be7-aa6e-72b2-84bf-4e35b9c5f25f',
                        'Visible task',
                        'Preview',
                        'Prompt',
                        'C:\work',
                        'model',
                        'provider',
                        'vscode',
                        NULL,
                        0,
                        1,
                        1783684901000
                    );
                    INSERT INTO threads VALUES (
                        '019f4be7-aa6e-72b2-84bf-4e35b9c5f260',
                        'Subagent task',
                        '',
                        '',
                        'C:\work',
                        'model',
                        'provider',
                        '{"subagent":{}}',
                        '/root/worker',
                        0,
                        0,
                        1783684902000
                    );
                    """;
                command.ExecuteNonQuery();
            }

            var records = new SharedHistoryService().Load(root);
            if (records.Count != 1 ||
                records[0].Title != "Visible task" ||
                records[0].WorkingDirectory != @"C:\work")
            {
                throw new InvalidOperationException("Unified shared history reader validation failed.");
            }

            var reconciled = new SharedHistoryService().ReconcileWithCodex(
                root,
                records,
                [
                    new CodexThreadSummary(
                        records[0].Id,
                        "Codex display name",
                        "Current preview",
                        @"C:\current",
                        "current-provider",
                        DateTimeOffset.FromUnixTimeSeconds(1783685901),
                        false)
                ]);
            if (reconciled.Count != 1 ||
                reconciled[0].Title != "Codex display name" ||
                reconciled[0].Preview != "Current preview" ||
                reconciled[0].WorkingDirectory != @"C:\current" ||
                reconciled[0].Model != "model" ||
                reconciled[0].Provider != "current-provider")
            {
                throw new InvalidOperationException("Codex authoritative history reconciliation failed.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string BuildUpdatedMillisecondsExpression(HashSet<string> columns)
    {
        var candidates = new List<string>();
        if (columns.Contains("recency_at_ms"))
        {
            candidates.Add("NULLIF(\"recency_at_ms\", 0)");
        }
        if (columns.Contains("updated_at_ms"))
        {
            candidates.Add("NULLIF(\"updated_at_ms\", 0)");
        }
        if (columns.Contains("recency_at"))
        {
            candidates.Add("NULLIF(\"recency_at\", 0) * 1000");
        }
        if (columns.Contains("updated_at"))
        {
            candidates.Add("NULLIF(\"updated_at\", 0) * 1000");
        }
        if (columns.Contains("created_at_ms"))
        {
            candidates.Add("NULLIF(\"created_at_ms\", 0)");
        }
        if (columns.Contains("created_at"))
        {
            candidates.Add("NULLIF(\"created_at\", 0) * 1000");
        }

        if (candidates.Count == 0)
        {
            return "0";
        }

        var normalizedCandidates = candidates.Select(candidate => $"COALESCE({candidate}, 0)");
        if (candidates.Count == 1)
        {
            return normalizedCandidates.Single();
        }

        return $"MAX({string.Join(", ", normalizedCandidates)})";
    }

    private static string BuildVisibleThreadFilter(HashSet<string> columns)
    {
        var filters = new List<string>();
        if (columns.Contains("agent_path"))
        {
            filters.Add("COALESCE(\"agent_path\", '') = ''");
        }
        if (columns.Contains("source"))
        {
            filters.Add("COALESCE(\"source\", '') NOT LIKE '%subagent%'");
        }

        return filters.Count == 0 ? "1 = 1" : string.Join(" AND ", filters);
    }

    private static string TextColumn(HashSet<string> columns, string name)
    {
        return columns.Contains(name) ? $"COALESCE(\"{name}\", '')" : "''";
    }

    private static string IntegerColumn(HashSet<string> columns, string name)
    {
        return columns.Contains(name) ? $"COALESCE(\"{name}\", 0)" : "0";
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() != null;
    }

    private static string ReadText(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
    }

    private static string NormalizeLine(string value)
    {
        return string.Join(" ", value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim();
    }

    private static string NormalizePathForDisplay(string path)
    {
        const string extendedPrefix = @"\\?\";
        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static DateTimeOffset FromUnixMilliseconds(long value)
    {
        if (value <= 0)
        {
            return DateTimeOffset.MinValue;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.First(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string FirstNonEmptyOrEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

}
