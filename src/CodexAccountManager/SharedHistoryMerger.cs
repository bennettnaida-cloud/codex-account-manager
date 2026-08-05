using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountManager;

public sealed class SharedHistoryMergeResult
{
    public string SharedHome { get; init; } = "";
    public string? BackupDirectory { get; init; }
    public int CopiedSessionFiles { get; set; }
    public int ImportedThreads { get; set; }
    public int ImportedDynamicTools { get; set; }
    public int ImportedSpawnEdges { get; set; }
    public int TotalThreads { get; set; }
    public bool Changed => CopiedSessionFiles > 0 || ImportedThreads > 0 ||
                           ImportedDynamicTools > 0 || ImportedSpawnEdges > 0;
}

public static class SharedHistoryMerger
{
    private const string ConflictDirectoryName = "account-switcher-conflicts";
    private static readonly string[] HistoryDirectories = ["sessions", "archived_sessions"];

    private enum FileContentRelationship
    {
        Equal,
        SourceIsPrefix,
        TargetIsPrefix,
        Diverged
    }

    private enum HistoryFileMergeAction
    {
        None,
        ReplaceCanonical,
        SaveConflict
    }

    private readonly record struct HistoryFileResolution(
        string CanonicalPath,
        string ActualPath,
        HistoryFileMergeAction Action);

    public static SharedHistoryMergeResult Merge(
        IEnumerable<string> sourceHomes,
        string sharedHome)
    {
        CodexCliService.EnsureSqliteProvider();

        sharedHome = NormalizePath(sharedHome);
        Directory.CreateDirectory(sharedHome);

        var sources = sourceHomes
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(Directory.Exists)
            .Where(path => !PathsEqual(path, sharedHome))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new SharedHistoryMergeResult
        {
            SharedHome = sharedHome,
            TotalThreads = CountThreads(sharedHome)
        };
        var deletedThreadIds = SharedHistoryService.LoadDeletedThreadIds(sharedHome);

        if (sources.Count == 0 || !HasPendingHistory(sources, sharedHome, deletedThreadIds))
        {
            return result;
        }

        var backupDirectory = CreateHistoryBackup(sources.Prepend(sharedHome));
        result = new SharedHistoryMergeResult
        {
            SharedHome = sharedHome,
            BackupDirectory = backupDirectory,
            TotalThreads = result.TotalThreads
        };

        foreach (var source in sources)
        {
            result.CopiedSessionFiles += CopyHistoryFiles(source, sharedHome, deletedThreadIds);
        }

        if (EnsureTargetDatabase(sharedHome, sources))
        {
            MergeDatabases(sources, sharedHome, result, deletedThreadIds);
        }
        result.TotalThreads = CountThreads(sharedHome);
        WriteMergeManifest(result, sources);
        return result;
    }

    private static bool HasPendingHistory(
        IReadOnlyList<string> sources,
        string sharedHome,
        IReadOnlySet<string> deletedThreadIds)
    {
        foreach (var source in sources)
        {
            if (HasPendingThreadChanges(source, sharedHome, deletedThreadIds))
            {
                return true;
            }

            foreach (var directoryName in HistoryDirectories)
            {
                var sourceRoot = Path.Combine(source, directoryName);
                if (!Directory.Exists(sourceRoot))
                {
                    continue;
                }

                foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (IsDeletedThreadFile(sourceFile, deletedThreadIds))
                    {
                        continue;
                    }

                    var resolution = ResolveHistoryFile(source, sourceFile, sharedHome);
                    if (resolution.Action != HistoryFileMergeAction.None)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasPendingThreadChanges(
        string sourceHome,
        string sharedHome,
        IReadOnlySet<string> deletedThreadIds)
    {
        var sourcePath = Path.Combine(sourceHome, "state_5.sqlite");
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        using var source = OpenDatabase(sourcePath, SqliteOpenMode.ReadOnly);
        if (!TableExists(source, "threads"))
        {
            return false;
        }

        var targetPath = Path.Combine(sharedHome, "state_5.sqlite");
        if (!File.Exists(targetPath))
        {
            return TableHasRows(source, "threads");
        }

        using var target = OpenDatabase(targetPath, SqliteOpenMode.ReadOnly);
        if (!TableExists(target, "threads"))
        {
            return TableHasRows(source, "threads");
        }

        var columns = CommonColumns(source, target, "threads");
        var idOrdinal = columns.FindIndex(column => column.Equals("id", StringComparison.OrdinalIgnoreCase));
        var rolloutOrdinal = columns.FindIndex(column => column.Equals("rollout_path", StringComparison.OrdinalIgnoreCase));
        if (idOrdinal < 0 || rolloutOrdinal < 0)
        {
            return TableHasRows(source, "threads");
        }

        var quotedColumns = string.Join(", ", columns.Select(QuoteIdentifier));
        using var readSource = source.CreateCommand();
        readSource.CommandText = $"SELECT {quotedColumns} FROM threads;";
        using var sourceReader = readSource.ExecuteReader();

        using var readTarget = target.CreateCommand();
        readTarget.CommandText = $"SELECT {quotedColumns} FROM threads WHERE id = $id LIMIT 1;";
        var targetId = readTarget.Parameters.Add("$id", SqliteType.Text);

        while (sourceReader.Read())
        {
            var sourceValues = ReadValues(sourceReader, columns.Count);
            var id = Convert.ToString(sourceValues[idOrdinal]) ?? "";
            if (deletedThreadIds.Contains(id))
            {
                continue;
            }

            var targetValues = ReadThreadValues(readTarget, targetId, id, columns.Count);
            if (targetValues == null)
            {
                return true;
            }

            var mergedValues = MergeThreadValues(
                columns,
                sourceValues,
                targetValues,
                rolloutOrdinal,
                sourceHome,
                sharedHome,
                out _);
            if (!ThreadValuesEqual(targetValues, mergedValues, rolloutOrdinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateHistoryBackup(IEnumerable<string> homes)
    {
        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "CodexHistoryBackups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(backupDirectory);

        var manifestHomes = new List<object>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(home))
            {
                continue;
            }

            var labelBase = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .TrimStart('.');
            if (string.IsNullOrWhiteSpace(labelBase))
            {
                labelBase = "codex-home";
            }

            var label = labelBase;
            for (var suffix = 2; !usedLabels.Add(label); suffix++)
            {
                label = labelBase + "-" + suffix;
            }

            var homeBackup = Path.Combine(backupDirectory, label);
            Directory.CreateDirectory(homeBackup);
            var fileCount = 0;
            foreach (var directoryName in HistoryDirectories)
            {
                fileCount += CopyDirectoryIfPresent(
                    Path.Combine(home, directoryName),
                    Path.Combine(homeBackup, directoryName));
            }

            var statePath = Path.Combine(home, "state_5.sqlite");
            if (File.Exists(statePath))
            {
                BackupDatabase(statePath, Path.Combine(homeBackup, "state_5.sqlite"));
            }

            var cliHistoryPath = Path.Combine(home, "history.jsonl");
            if (File.Exists(cliHistoryPath))
            {
                CopyFilePreservingTime(cliHistoryPath, Path.Combine(homeBackup, "history.jsonl"));
                fileCount++;
            }

            manifestHomes.Add(new { home, backup = homeBackup, historyFiles = fileCount });
        }

        File.WriteAllText(
            Path.Combine(backupDirectory, "backup-manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    createdAtUtc = DateTimeOffset.UtcNow,
                    purpose = "Codex shared chat history migration",
                    excludesCredentials = true,
                    homes = manifestHomes
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return backupDirectory;
    }

    private static int CopyDirectoryIfPresent(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return 0;
        }

        var copied = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, sourceFile));
            CopyFilePreservingTime(sourceFile, targetFile);
            copied++;
        }

        return copied;
    }

    private static int CopyHistoryFiles(
        string sourceHome,
        string sharedHome,
        IReadOnlySet<string> deletedThreadIds)
    {
        var copied = 0;
        foreach (var directoryName in HistoryDirectories)
        {
            var sourceRoot = Path.Combine(sourceHome, directoryName);
            if (!Directory.Exists(sourceRoot))
            {
                continue;
            }

            foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                if (IsDeletedThreadFile(sourceFile, deletedThreadIds))
                {
                    continue;
                }

                var resolution = ResolveHistoryFile(sourceHome, sourceFile, sharedHome);
                if (resolution.Action == HistoryFileMergeAction.None)
                {
                    continue;
                }

                if (MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome))
                {
                    copied++;
                }
            }
        }

        return copied;
    }

    private static bool IsDeletedThreadFile(string path, IReadOnlySet<string> deletedThreadIds)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Length < 36)
        {
            return false;
        }

        var possibleId = fileName[^36..];
        return Guid.TryParse(possibleId, out _) && deletedThreadIds.Contains(possibleId);
    }

    internal static void ValidateHistoryFileMerge()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-history-file-merge-" + Guid.NewGuid().ToString("N"));
        var sourceHome = Path.Combine(root, "source");
        var sharedHome = Path.Combine(root, "shared");
        var relative = Path.Combine("sessions", "2026", "07", "10", "thread.jsonl");
        var sourceFile = Path.Combine(sourceHome, relative);
        var canonicalFile = Path.Combine(sharedHome, relative);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalFile)!);

            File.WriteAllText(sourceFile, "first\n");
            if (!MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome) ||
                File.ReadAllText(canonicalFile) != "first\n")
            {
                throw new InvalidOperationException("Missing history file merge validation failed.");
            }

            File.WriteAllText(sourceFile, "first\n");
            File.WriteAllText(canonicalFile, "first\nsecond\n");
            if (MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome) ||
                File.ReadAllText(canonicalFile) != "first\nsecond\n")
            {
                throw new InvalidOperationException("Longer canonical history preservation validation failed.");
            }

            File.WriteAllText(sourceFile, "first\nsecond\nthird\n");
            File.WriteAllText(canonicalFile, "first\nsecond\n");
            if (!MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome) ||
                File.ReadAllText(canonicalFile) != "first\nsecond\nthird\n")
            {
                throw new InvalidOperationException("Append-only history merge validation failed.");
            }

            File.WriteAllText(sourceFile, "source-branch\n");
            File.WriteAllText(canonicalFile, "target-branch\n");
            if (!MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome) ||
                File.ReadAllText(canonicalFile) != "target-branch\n")
            {
                throw new InvalidOperationException("Diverged history preservation validation failed.");
            }

            var resolution = ResolveHistoryFile(sourceHome, sourceFile, sharedHome);
            if (resolution.Action != HistoryFileMergeAction.None ||
                PathsEqual(resolution.ActualPath, canonicalFile) ||
                !IsPathInsideHome(resolution.ActualPath, Path.Combine(sharedHome, ConflictDirectoryName)) ||
                File.ReadAllText(resolution.ActualPath) != "source-branch\n" ||
                MergeHistoryFileAtomically(sourceHome, sourceFile, sharedHome) ||
                !PathsEqual(MapRolloutPath(sourceFile, sourceHome, sharedHome), resolution.ActualPath))
            {
                throw new InvalidOperationException("Hash-addressed history conflict validation failed.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static void ValidateDeletedThreadTombstones()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-history-delete-tombstone-" + Guid.NewGuid().ToString("N"));
        var sourceHome = Path.Combine(root, "source");
        var sharedHome = Path.Combine(root, "shared");
        const string deletedId = "019f4be7-aa6e-72b2-84bf-4e35b9c5f25f";
        var sourceFile = Path.Combine(
            sourceHome,
            "sessions",
            "2026",
            "07",
            "10",
            "rollout-2026-07-10T20-01-41-" + deletedId + ".jsonl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            Directory.CreateDirectory(sharedHome);
            File.WriteAllText(sourceFile, "deleted thread must not return\n");
            new SharedHistoryService().RecordDeletedThread(sharedHome, deletedId);
            var deleted = SharedHistoryService.LoadDeletedThreadIds(sharedHome);
            if (!deleted.Contains(deletedId) ||
                !IsDeletedThreadFile(sourceFile, deleted) ||
                CopyHistoryFiles(sourceHome, sharedHome, deleted) != 0)
            {
                throw new InvalidOperationException("Deleted thread tombstone validation failed.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static HistoryFileResolution ResolveHistoryFile(
        string sourceHome,
        string sourceFile,
        string sharedHome,
        string? contentPath = null)
    {
        sourceHome = NormalizePath(sourceHome);
        sharedHome = NormalizePath(sharedHome);
        sourceFile = NormalizePath(sourceFile);
        contentPath = NormalizePath(contentPath ?? sourceFile);

        var relative = GetRelativePathInsideHome(sourceHome, sourceFile);
        var canonicalPath = Path.GetFullPath(Path.Combine(sharedHome, relative));
        if (!File.Exists(canonicalPath))
        {
            return new HistoryFileResolution(
                canonicalPath,
                canonicalPath,
                HistoryFileMergeAction.ReplaceCanonical);
        }

        var relationship = CompareFileContents(contentPath, canonicalPath);
        if (relationship is FileContentRelationship.Equal or FileContentRelationship.SourceIsPrefix)
        {
            return new HistoryFileResolution(
                canonicalPath,
                canonicalPath,
                HistoryFileMergeAction.None);
        }

        if (relationship == FileContentRelationship.TargetIsPrefix)
        {
            return new HistoryFileResolution(
                canonicalPath,
                canonicalPath,
                HistoryFileMergeAction.ReplaceCanonical);
        }

        var sourceHash = ComputeFileSha256(contentPath);
        var conflictPath = CreateConflictCopyPath(sharedHome, relative, sourceHash);
        if (!File.Exists(conflictPath))
        {
            return new HistoryFileResolution(
                canonicalPath,
                conflictPath,
                HistoryFileMergeAction.SaveConflict);
        }

        if (CompareFileContents(contentPath, conflictPath) == FileContentRelationship.Equal)
        {
            return new HistoryFileResolution(
                canonicalPath,
                conflictPath,
                HistoryFileMergeAction.None);
        }

        throw new InvalidDataException(
            $"冲突副本路径的内容与其 SHA-256 不匹配，已停止合并以避免覆盖：{conflictPath}");
    }

    private static bool MergeHistoryFileAtomically(
        string sourceHome,
        string sourceFile,
        string sharedHome)
    {
        var stagingRoot = Path.Combine(sharedHome, ConflictDirectoryName);
        Directory.CreateDirectory(stagingRoot);
        var stagingPath = Path.Combine(stagingRoot, $".staging-{Guid.NewGuid():N}.tmp");

        try
        {
            CopyFilePreservingTime(sourceFile, stagingPath);
            var resolution = ResolveHistoryFile(sourceHome, sourceFile, sharedHome, stagingPath);
            if (resolution.Action == HistoryFileMergeAction.None)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resolution.ActualPath)!);
            if (resolution.Action == HistoryFileMergeAction.SaveConflict &&
                File.Exists(resolution.ActualPath))
            {
                if (FilesEqual(stagingPath, resolution.ActualPath))
                {
                    return false;
                }

                throw new InvalidDataException(
                    $"冲突副本路径已被不同内容占用，已停止合并：{resolution.ActualPath}");
            }

            try
            {
                File.Move(
                    stagingPath,
                    resolution.ActualPath,
                    resolution.Action == HistoryFileMergeAction.ReplaceCanonical);
                return true;
            }
            catch (IOException) when (
                resolution.Action == HistoryFileMergeAction.SaveConflict &&
                File.Exists(resolution.ActualPath) &&
                FilesEqual(stagingPath, resolution.ActualPath))
            {
                // Another merge saved the identical hash-addressed conflict first.
                return false;
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static FileContentRelationship CompareFileContents(string sourcePath, string targetPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var sourceLength = source.Length;
        var targetLength = target.Length;
        var bytesToCompare = Math.Min(sourceLength, targetLength);
        var sourceBuffer = new byte[64 * 1024];
        var targetBuffer = new byte[sourceBuffer.Length];

        while (bytesToCompare > 0)
        {
            var requested = (int)Math.Min(sourceBuffer.Length, bytesToCompare);
            var sourceRead = ReadFully(source, sourceBuffer, requested);
            var targetRead = ReadFully(target, targetBuffer, requested);
            if (sourceRead != requested || targetRead != requested ||
                !sourceBuffer.AsSpan(0, requested).SequenceEqual(targetBuffer.AsSpan(0, requested)))
            {
                return FileContentRelationship.Diverged;
            }

            bytesToCompare -= requested;
        }

        if (sourceLength == targetLength)
        {
            return FileContentRelationship.Equal;
        }

        return sourceLength < targetLength
            ? FileContentRelationship.SourceIsPrefix
            : FileContentRelationship.TargetIsPrefix;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetRelativePathInsideHome(string home, string path)
    {
        var normalizedHome = NormalizePath(home);
        var normalizedPath = NormalizePath(path);
        var relative = Path.GetRelativePath(normalizedHome, normalizedPath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"rollout 文件不在来源 CODEX_HOME 内：{path}");
        }

        return relative;
    }

    private static bool EnsureTargetDatabase(string sharedHome, IReadOnlyList<string> sources)
    {
        var targetPath = Path.Combine(sharedHome, "state_5.sqlite");
        if (File.Exists(targetPath))
        {
            return true;
        }

        var seedHome = sources.FirstOrDefault(source => File.Exists(Path.Combine(source, "state_5.sqlite")));
        if (seedHome == null)
        {
            return false;
        }

        BackupDatabase(Path.Combine(seedHome, "state_5.sqlite"), targetPath);
        RewriteAllRolloutPaths(targetPath, seedHome, sharedHome);
        return true;
    }

    private static void MergeDatabases(
        IReadOnlyList<string> sources,
        string sharedHome,
        SharedHistoryMergeResult result,
        IReadOnlySet<string> deletedThreadIds)
    {
        var targetPath = Path.Combine(sharedHome, "state_5.sqlite");
        using var target = OpenDatabase(targetPath, SqliteOpenMode.ReadWrite);
        using var transaction = target.BeginTransaction();

        foreach (var sourceHome in sources)
        {
            var sourcePath = Path.Combine(sourceHome, "state_5.sqlite");
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            using var source = OpenDatabase(sourcePath, SqliteOpenMode.ReadOnly);
            result.ImportedThreads += CopyThreads(source, target, transaction, sourceHome, sharedHome, deletedThreadIds);
            result.ImportedDynamicTools += CopyTable(source, target, transaction, "thread_dynamic_tools", deletedThreadIds);
            result.ImportedSpawnEdges += CopyTable(source, target, transaction, "thread_spawn_edges", deletedThreadIds);
        }

        transaction.Commit();
        using var checkpoint = target.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }

    private static int CopyThreads(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        string sourceHome,
        string sharedHome,
        IReadOnlySet<string> deletedThreadIds)
    {
        var columns = CommonColumns(source, target, "threads");
        if (!columns.Contains("id", StringComparer.OrdinalIgnoreCase) ||
            !columns.Contains("rollout_path", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Codex threads 表缺少 id 或 rollout_path 列。");
        }

        var quotedColumns = string.Join(", ", columns.Select(QuoteIdentifier));
        using var read = source.CreateCommand();
        read.CommandText = $"SELECT {quotedColumns} FROM threads;";
        using var reader = read.ExecuteReader();

        using var readTarget = target.CreateCommand();
        readTarget.Transaction = transaction;
        readTarget.CommandText = $"SELECT {quotedColumns} FROM threads WHERE id = $id LIMIT 1;";
        var targetId = readTarget.Parameters.Add("$id", SqliteType.Text);

        using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO threads ({quotedColumns}) VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i))});";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.Add(new SqliteParameter("$p" + i, DBNull.Value));
        }

        var updateOrdinals = Enumerable.Range(0, columns.Count)
            .Where(i => !columns[i].Equals("id", StringComparison.OrdinalIgnoreCase))
            .ToList();
        using var update = target.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE threads SET {string.Join(", ", updateOrdinals.Select((ordinal, i) => $"{QuoteIdentifier(columns[ordinal])} = $u{i}"))} WHERE id = $id;";
        for (var i = 0; i < updateOrdinals.Count; i++)
        {
            update.Parameters.Add(new SqliteParameter("$u" + i, DBNull.Value));
        }
        var updateId = update.Parameters.Add("$id", SqliteType.Text);

        var idOrdinal = columns.FindIndex(column => column.Equals("id", StringComparison.OrdinalIgnoreCase));
        var rolloutOrdinal = columns.FindIndex(column => column.Equals("rollout_path", StringComparison.OrdinalIgnoreCase));
        var imported = 0;
        while (reader.Read())
        {
            var sourceValues = ReadValues(reader, columns.Count);
            var id = Convert.ToString(sourceValues[idOrdinal]) ?? "";
            if (deletedThreadIds.Contains(id))
            {
                continue;
            }

            var targetValues = ReadThreadValues(readTarget, targetId, id, columns.Count);
            var mergedValues = MergeThreadValues(
                columns,
                sourceValues,
                targetValues,
                rolloutOrdinal,
                sourceHome,
                sharedHome,
                out var usesSourceRollout);

            if (targetValues != null && ThreadValuesEqual(targetValues, mergedValues, rolloutOrdinal))
            {
                continue;
            }

            var mergedRollout = Convert.ToString(mergedValues[rolloutOrdinal]) ?? "";
            if (usesSourceRollout && !File.Exists(mergedRollout))
            {
                throw new FileNotFoundException("合并后的 rollout 文件不存在。", mergedRollout);
            }

            if (targetValues == null)
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    insert.Parameters[i].Value = mergedValues[i];
                }

                imported += insert.ExecuteNonQuery();
                continue;
            }

            for (var i = 0; i < updateOrdinals.Count; i++)
            {
                update.Parameters[i].Value = mergedValues[updateOrdinals[i]];
            }
            updateId.Value = id;
            imported += update.ExecuteNonQuery();
        }

        return imported;
    }

    private static object[] ReadValues(SqliteDataReader reader, int count)
    {
        var values = new object[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
        }

        return values;
    }

    private static object[]? ReadThreadValues(
        SqliteCommand command,
        SqliteParameter idParameter,
        string id,
        int columnCount)
    {
        idParameter.Value = id;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadValues(reader, columnCount) : null;
    }

    private static object[] MergeThreadValues(
        IReadOnlyList<string> columns,
        IReadOnlyList<object> sourceValues,
        IReadOnlyList<object>? targetValues,
        int rolloutOrdinal,
        string sourceHome,
        string sharedHome,
        out bool usesSourceRollout)
    {
        if (targetValues == null)
        {
            var insertedValues = sourceValues.ToArray();
            insertedValues[rolloutOrdinal] = MapRolloutPath(
                Convert.ToString(sourceValues[rolloutOrdinal]) ?? "",
                sourceHome,
                sharedHome);
            usesSourceRollout = true;
            return insertedValues;
        }

        var mergedValues = targetValues.ToArray();
        var sourceIsNewer = ReadThreadFreshness(columns, sourceValues) >
                            ReadThreadFreshness(columns, targetValues);

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                column.Equals("rollout_path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsMonotonicThreadColumn(column))
            {
                mergedValues[i] = LargerValue(sourceValues[i], targetValues[i]);
                continue;
            }

            if (!IsEmptyValue(sourceValues[i]) &&
                (sourceIsNewer || IsEmptyValue(targetValues[i])))
            {
                mergedValues[i] = sourceValues[i];
            }
        }

        var targetRollout = Convert.ToString(targetValues[rolloutOrdinal]) ?? "";
        usesSourceRollout = sourceIsNewer ||
                            IsEmptyValue(targetValues[rolloutOrdinal]) ||
                            !IsPathInsideHome(targetRollout, sharedHome) ||
                            !File.Exists(targetRollout);
        if (usesSourceRollout)
        {
            mergedValues[rolloutOrdinal] = MapRolloutPath(
                Convert.ToString(sourceValues[rolloutOrdinal]) ?? "",
                sourceHome,
                sharedHome);
        }

        return mergedValues;
    }

    private static long ReadThreadFreshness(
        IReadOnlyList<string> columns,
        IReadOnlyList<object> values)
    {
        var freshness = 0L;
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "updated_at_ms", true));
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "recency_at_ms", true));
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "archived_at_ms", true));
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "updated_at", false));
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "recency_at", false));
        freshness = Math.Max(freshness, ReadTimestamp(columns, values, "archived_at", false));
        if (freshness == 0)
        {
            freshness = Math.Max(freshness, ReadTimestamp(columns, values, "created_at_ms", true));
            freshness = Math.Max(freshness, ReadTimestamp(columns, values, "created_at", false));
        }

        return freshness;
    }

    private static long ReadTimestamp(
        IReadOnlyList<string> columns,
        IReadOnlyList<object> values,
        string column,
        bool isMilliseconds)
    {
        var ordinal = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                break;
            }
        }

        if (ordinal < 0 || !TryConvertToInt64(values[ordinal], out var timestamp) || timestamp <= 0)
        {
            return 0;
        }

        if (isMilliseconds || timestamp >= 100_000_000_000)
        {
            return timestamp;
        }

        return timestamp > long.MaxValue / 1000 ? long.MaxValue : timestamp * 1000;
    }

    private static bool IsMonotonicThreadColumn(string column)
    {
        return column.Equals("tokens_used", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("has_user_event", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("updated_at", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("updated_at_ms", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("recency_at", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("recency_at_ms", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("archived_at", StringComparison.OrdinalIgnoreCase) ||
               column.Equals("archived_at_ms", StringComparison.OrdinalIgnoreCase);
    }

    private static object LargerValue(object sourceValue, object targetValue)
    {
        if (IsEmptyValue(targetValue))
        {
            return sourceValue;
        }

        if (IsEmptyValue(sourceValue))
        {
            return targetValue;
        }

        return TryConvertToDecimal(sourceValue, out var sourceNumber) &&
               TryConvertToDecimal(targetValue, out var targetNumber) &&
               sourceNumber > targetNumber
            ? sourceValue
            : targetValue;
    }

    private static bool TryConvertToInt64(object value, out long number)
    {
        try
        {
            number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            number = 0;
            return false;
        }
    }

    private static bool TryConvertToDecimal(object value, out decimal number)
    {
        try
        {
            number = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            number = 0;
            return false;
        }
    }

    private static bool IsEmptyValue(object value)
    {
        return value == DBNull.Value ||
               value is null ||
               value is string text && string.IsNullOrWhiteSpace(text) ||
               value is byte[] bytes && bytes.Length == 0;
    }

    private static bool ThreadValuesEqual(
        IReadOnlyList<object> left,
        IReadOnlyList<object> right,
        int rolloutOrdinal)
    {
        for (var i = 0; i < left.Count; i++)
        {
            if (i == rolloutOrdinal)
            {
                if (!PathValuesEqual(left[i], right[i]))
                {
                    return false;
                }

                continue;
            }

            if (!ValuesEqual(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PathValuesEqual(object left, object right)
    {
        var leftPath = Convert.ToString(left) ?? "";
        var rightPath = Convert.ToString(right) ?? "";
        if (leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
        {
            return false;
        }

        try
        {
            return PathsEqual(leftPath, rightPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ValuesEqual(object left, object right)
    {
        if (left == DBNull.Value || left is null)
        {
            return right == DBNull.Value || right is null;
        }

        if (right == DBNull.Value || right is null)
        {
            return false;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        if (IsNumericValue(left) && IsNumericValue(right) &&
            TryConvertToDecimal(left, out var leftNumber) &&
            TryConvertToDecimal(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return Equals(left, right);
    }

    private static bool IsNumericValue(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private static bool IsPathInsideHome(string path, string home)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var relative = Path.GetRelativePath(NormalizePath(home), NormalizePath(path));
            return !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TableHasRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {QuoteIdentifier(table)} LIMIT 1;";
        return command.ExecuteScalar() != null;
    }

    private static int CopyTable(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        string table,
        IReadOnlySet<string> deletedThreadIds)
    {
        if (!TableExists(source, table) || !TableExists(target, table))
        {
            return 0;
        }

        var columns = CommonColumns(source, target, table);
        if (columns.Count == 0)
        {
            return 0;
        }

        var quotedColumns = string.Join(", ", columns.Select(QuoteIdentifier));
        using var read = source.CreateCommand();
        read.CommandText = $"SELECT {quotedColumns} FROM {QuoteIdentifier(table)};";
        using var reader = read.ExecuteReader();

        using var insert = target.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT OR IGNORE INTO {QuoteIdentifier(table)} ({quotedColumns}) VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i))});";
        for (var i = 0; i < columns.Count; i++)
        {
            insert.Parameters.Add(new SqliteParameter("$p" + i, DBNull.Value));
        }

        var imported = 0;
        var threadIdOrdinals = columns
            .Select((column, index) => new { column, index })
            .Where(item => item.column.Contains("thread_id", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        while (reader.Read())
        {
            if (threadIdOrdinals.Any(ordinal =>
                    !reader.IsDBNull(ordinal) &&
                    deletedThreadIds.Contains(Convert.ToString(reader.GetValue(ordinal)) ?? "")))
            {
                continue;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                insert.Parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            }

            imported += insert.ExecuteNonQuery();
        }

        return imported;
    }

    private static List<string> CommonColumns(SqliteConnection source, SqliteConnection target, string table)
    {
        var sourceColumns = ReadColumns(source, table);
        var targetColumns = ReadColumns(target, table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sourceColumns.Where(targetColumns.Contains).ToList();
    }

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
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

    private static void RewriteAllRolloutPaths(string databasePath, string sourceHome, string sharedHome)
    {
        using var connection = OpenDatabase(databasePath, SqliteOpenMode.ReadWrite);
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, rollout_path FROM threads;";
        using var reader = select.ExecuteReader();
        var rows = new List<(string Id, string RolloutPath)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        reader.Close();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE threads SET rollout_path = $path WHERE id = $id;";
        var pathParameter = update.Parameters.Add("$path", SqliteType.Text);
        var idParameter = update.Parameters.Add("$id", SqliteType.Text);
        foreach (var row in rows)
        {
            pathParameter.Value = MapRolloutPath(row.RolloutPath, sourceHome, sharedHome);
            idParameter.Value = row.Id;
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string MapRolloutPath(string rolloutPath, string sourceHome, string sharedHome)
    {
        var fullSourcePath = NormalizePath(rolloutPath);
        var relative = GetRelativePathInsideHome(sourceHome, fullSourcePath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"rollout 文件不在来源 CODEX_HOME 内：{rolloutPath}");
        }

        var canonicalPath = Path.GetFullPath(Path.Combine(sharedHome, relative));
        if (!File.Exists(fullSourcePath))
        {
            return canonicalPath;
        }

        return ResolveHistoryFile(sourceHome, fullSourcePath, sharedHome).ActualPath;
    }

    private static HashSet<string> ReadThreadIds(string home)
    {
        var databasePath = Path.Combine(home, "state_5.sqlite");
        if (!File.Exists(databasePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        using var connection = OpenDatabase(databasePath, SqliteOpenMode.ReadOnly);
        if (!TableExists(connection, "threads"))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM threads;";
        using var reader = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static int CountThreads(string home)
    {
        return ReadThreadIds(home).Count;
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void BackupDatabase(string sourcePath, string backupPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        using var source = OpenDatabase(sourcePath, SqliteOpenMode.ReadOnly);
        using var backup = OpenDatabase(backupPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(backup);
    }

    private static void CopyFilePreservingTime(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using (var source = new FileStream(
                   sourcePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var target = new FileStream(
                   targetPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.Read))
        {
            source.CopyTo(target);
        }

        File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = new FileStream(
            left,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var rightStream = new FileStream(
            right,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
    }

    private static string CreateConflictCopyPath(
        string sharedHome,
        string relativeSourcePath,
        string sourceHash)
    {
        var relativeDirectory = Path.GetDirectoryName(relativeSourcePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativeSourcePath);
        var extension = Path.GetExtension(relativeSourcePath);
        return Path.GetFullPath(Path.Combine(
            sharedHome,
            ConflictDirectoryName,
            relativeDirectory,
            $"{name}.{sourceHash}{extension}"));
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string NormalizePath(string path)
    {
        return StripExtendedPathPrefix(Path.GetFullPath(path))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string StripExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[localPrefix.Length..]
            : path;
    }

    private static bool PathsEqual(string left, string right)
    {
        return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMergeManifest(SharedHistoryMergeResult result, IReadOnlyList<string> sources)
    {
        File.WriteAllText(
            Path.Combine(result.SharedHome, "shared-history-merge.json"),
            JsonSerializer.Serialize(
                new
                {
                    mergedAtUtc = DateTimeOffset.UtcNow,
                    sharedHome = result.SharedHome,
                    sources,
                    result.BackupDirectory,
                    result.CopiedSessionFiles,
                    result.ImportedThreads,
                    result.ImportedDynamicTools,
                    result.ImportedSpawnEdges,
                    result.TotalThreads
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
