using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexAccountManager;

internal readonly record struct CodexRolloutHistoryRepairResult(
    int CandidateFiles,
    int ScannedFiles,
    int RepairedFiles,
    int RepairedRecords,
    long ScannedBytes);

/// <summary>
/// Repairs a narrowly identified Codex desktop rollout serialization defect that leaves
/// otherwise intact conversations invisible after the paginated history projector reaches
/// the malformed record.  The replacement has exactly the same byte length, so existing
/// projection offsets and all following rollout records remain stable.
/// </summary>
internal static class CodexRolloutHistoryRepairService
{
    private const int CandidateLimit = 128;
    private const int BufferSize = 1024 * 1024;
    private const long MaximumBytesPerPass = 512L * 1024L * 1024L;
    private static readonly byte[] InvalidPermissionProfile =
        Encoding.UTF8.GetBytes("\"id\":\":danger-full-access\"");
    private static readonly byte[] ValidPermissionProfile =
        Encoding.UTF8.GetBytes("\"id\":\"danger-full-access\" ");
    private static readonly ConcurrentDictionary<string, long> LastScannedLengths =
        new(StringComparer.OrdinalIgnoreCase);

    internal static CodexRolloutHistoryRepairResult TryRepairLaggingPaginatedRollouts(
        string codexHome,
        bool allowOrdinalRewrite = false)
    {
        if (InvalidPermissionProfile.Length != ValidPermissionProfile.Length)
        {
            throw new InvalidOperationException(
                "Codex rollout repair replacement must preserve the original byte length.");
        }

        var root = Path.GetFullPath(codexHome);
        var statePath = Path.Combine(root, "state_5.sqlite");
        var historyPath = FindLatestHistoryDatabase(root);
        if (!File.Exists(statePath) || string.IsNullOrWhiteSpace(historyPath))
        {
            return default;
        }

        try
        {
            CodexCliService.EnsureSqliteProvider();
            var projectionCursors = LoadProjectionCursors(historyPath);
            if (projectionCursors.Count == 0)
            {
                return default;
            }

            var candidates = LoadRecentPaginatedRollouts(root, statePath, projectionCursors);
            var scannedFiles = 0;
            var repairedFiles = 0;
            var repairedRecords = 0;
            long scannedBytes = 0;

            foreach (var candidate in candidates)
            {
                if (scannedBytes >= MaximumBytesPerPass)
                {
                    break;
                }

                try
                {
                    var info = new FileInfo(candidate.Path);
                    if (!info.Exists || info.Length <= candidate.ProjectionOffset)
                    {
                        continue;
                    }

                    var scanStart = Math.Max(0, candidate.ProjectionOffset - InvalidPermissionProfile.Length);
                    if (LastScannedLengths.TryGetValue(candidate.Path, out var lastScannedLength) &&
                        lastScannedLength >= candidate.ProjectionOffset &&
                        lastScannedLength <= info.Length)
                    {
                        scanStart = Math.Max(
                            scanStart,
                            Math.Max(0, lastScannedLength - InvalidPermissionProfile.Length));
                    }

                    var allowedEnd = Math.Min(
                        info.Length,
                        scanStart + Math.Max(0, MaximumBytesPerPass - scannedBytes));
                    if (allowedEnd <= scanStart)
                    {
                        continue;
                    }

                    var repaired = RepairFileRegionInPlace(
                        candidate.Path,
                        scanStart,
                        allowedEnd,
                        out var bytesRead);
                    scannedFiles++;
                    scannedBytes += bytesRead;
                    LastScannedLengths[candidate.Path] = allowedEnd;
                    if (allowOrdinalRewrite)
                    {
                        repaired += RepairNonIncreasingOrdinalTail(
                            root,
                            candidate.Path,
                            candidate.ProjectionOffset,
                            candidate.NextOrdinal);
                    }
                    if (repaired > 0)
                    {
                        repairedFiles++;
                        repairedRecords += repaired;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An active writer may briefly reopen a rollout without write sharing.
                    // The background pass will retry after the next debounce interval.
                }
            }

            return new CodexRolloutHistoryRepairResult(
                candidates.Count,
                scannedFiles,
                repairedFiles,
                repairedRecords,
                scannedBytes);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            // History repair is best-effort and must never block the Manager or gateway.
            return default;
        }
    }

    private static string? FindLatestHistoryDatabase(string root)
    {
        try
        {
            return Directory
                .EnumerateFiles(root, "thread_history_*.sqlite", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static Dictionary<string, ProjectionCursor> LoadProjectionCursors(string historyPath)
    {
        var result = new Dictionary<string, ProjectionCursor>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenReadOnlyDatabase(historyPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT thread_id, next_rollout_byte_offset, next_rollout_ordinal " +
            "FROM thread_history_projection_state;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var threadId = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                result[threadId] = new ProjectionCursor(
                    Math.Max(0, reader.GetInt64(1)),
                    Math.Max(0, reader.GetInt64(2)));
            }
        }

        return result;
    }

    private static List<RolloutCandidate> LoadRecentPaginatedRollouts(
        string root,
        string statePath,
        IReadOnlyDictionary<string, ProjectionCursor> projectionCursors)
    {
        var candidates = new List<RolloutCandidate>();
        using var connection = OpenReadOnlyDatabase(statePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, rollout_path FROM threads " +
            "WHERE history_mode = 'paginated' AND rollout_path IS NOT NULL " +
            "ORDER BY updated_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", CandidateLimit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var threadId = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var storedPath = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(threadId) ||
                string.IsNullOrWhiteSpace(storedPath) ||
                !projectionCursors.TryGetValue(threadId, out var projectionCursor))
            {
                continue;
            }

            var path = ResolveSafeRolloutPath(root, storedPath);
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(new RolloutCandidate(
                    path,
                    projectionCursor.ByteOffset,
                    projectionCursor.NextOrdinal));
            }
        }

        return candidates;
    }

    private static SqliteConnection OpenReadOnlyDatabase(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 3
            }.ToString());
        connection.Open();
        return connection;
    }

    private static string? ResolveSafeRolloutPath(string root, string storedPath)
    {
        try
        {
            var candidate = storedPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? storedPath[4..]
                : storedPath;
            var fullPath = Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(root, candidate));
            if (!Path.GetExtension(fullPath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, fullPath);
            if (relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relative))
            {
                return null;
            }

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static int RepairFileRegionInPlace(
        string path,
        long startOffset,
        long requestedEndOffset,
        out long bytesRead)
    {
        bytesRead = 0;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.RandomAccess);
        var stableEnd = Math.Min(stream.Length, requestedEndOffset);
        var position = Math.Clamp(startOffset, 0, stableEnd);
        var buffer = new byte[BufferSize];
        var repaired = 0;
        long lastRepairedOffset = -1;

        while (position < stableEnd)
        {
            stream.Position = position;
            var wanted = checked((int)Math.Min(buffer.Length, stableEnd - position));
            var read = stream.Read(buffer, 0, wanted);
            if (read <= 0)
            {
                break;
            }

            bytesRead += read;
            for (var i = 0; i <= read - InvalidPermissionProfile.Length; i++)
            {
                if (buffer[i] != InvalidPermissionProfile[0] ||
                    !buffer.AsSpan(i, InvalidPermissionProfile.Length)
                        .SequenceEqual(InvalidPermissionProfile))
                {
                    continue;
                }

                var absoluteOffset = position + i;
                if (absoluteOffset <= lastRepairedOffset)
                {
                    continue;
                }

                stream.Position = absoluteOffset;
                stream.Write(ValidPermissionProfile);
                repaired++;
                lastRepairedOffset = absoluteOffset;
                i += InvalidPermissionProfile.Length - 1;
            }

            if (read < InvalidPermissionProfile.Length)
            {
                break;
            }

            position += Math.Max(1, read - InvalidPermissionProfile.Length + 1);
        }

        if (repaired > 0)
        {
            stream.Flush(flushToDisk: true);
        }

        return repaired;
    }

    private static int RepairNonIncreasingOrdinalTail(
        string codexHome,
        string path,
        long projectionOffset,
        long nextOrdinal)
    {
        if (nextOrdinal < 0)
        {
            return 0;
        }

        var requiresRewrite = false;
        var adjustedOrdinals = 0;
        long sourceLength;
        using (var source = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   BufferSize,
                   FileOptions.SequentialScan))
        {
            sourceLength = source.Length;
            if (projectionOffset < 0 || projectionOffset >= source.Length ||
                !IsLineBoundary(source, projectionOffset))
            {
                return 0;
            }

            source.Position = projectionOffset;
            using var reader = new StreamReader(
                source,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                BufferSize,
                leaveOpen: true);
            var expected = nextOrdinal;
            while (reader.ReadLine() is { } line)
            {
                if (!TryReadOrdinal(line, out var ordinal, out _, out _))
                {
                    continue;
                }

                if (ordinal < expected)
                {
                    requiresRewrite = true;
                    adjustedOrdinals++;
                    ordinal = expected;
                }
                expected = ordinal == long.MaxValue ? long.MaxValue : ordinal + 1;
            }
        }

        if (!requiresRewrite)
        {
            return 0;
        }

        var backupDirectory = Path.Combine(
            codexHome,
            "recovery-backups",
            "rollout-ordinal-repair-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(path));
        var temporaryPath = path + ".account-manager-ordinal-repair-" +
                            Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(path, backupPath, overwrite: false);
            RewriteOrdinalTail(path, temporaryPath, projectionOffset, nextOrdinal);
            var currentInfo = new FileInfo(path);
            if (!currentInfo.Exists || currentInfo.Length != sourceLength)
            {
                throw new IOException(
                    "Codex rollout changed while the ordinal repair was being staged.");
            }
            File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return adjustedOrdinals;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static void RewriteOrdinalTail(
        string sourcePath,
        string targetPath,
        long projectionOffset,
        long nextOrdinal)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        var endsWithNewline = source.Length > 0 && ReadLastByte(source) == (byte)'\n';
        source.Position = 0;
        using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        CopyExactly(source, target, projectionOffset);

        using var reader = new StreamReader(
            source,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            BufferSize,
            leaveOpen: true);
        using var writer = new StreamWriter(
            target,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            BufferSize,
            leaveOpen: true)
        {
            NewLine = "\n"
        };
        var expected = nextOrdinal;
        var firstLine = true;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!firstLine)
            {
                writer.Write('\n');
            }
            firstLine = false;

            if (TryReadOrdinal(line, out var ordinal, out var digitStart, out var digitLength))
            {
                if (ordinal < expected)
                {
                    line = line[..digitStart] +
                           expected.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           line[(digitStart + digitLength)..];
                    ordinal = expected;
                }
                expected = ordinal == long.MaxValue ? long.MaxValue : ordinal + 1;
            }
            writer.Write(line);
        }
        if (endsWithNewline && !firstLine)
        {
            writer.Write('\n');
        }
        writer.Flush();
        target.Flush(flushToDisk: true);
    }

    private static bool IsLineBoundary(FileStream stream, long offset)
    {
        if (offset == 0)
        {
            return true;
        }
        stream.Position = offset - 1;
        return stream.ReadByte() == (byte)'\n';
    }

    private static int ReadLastByte(FileStream stream)
    {
        stream.Position = stream.Length - 1;
        return stream.ReadByte();
    }

    private static void CopyExactly(Stream source, Stream target, long count)
    {
        var buffer = new byte[BufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, checked((int)Math.Min(buffer.Length, remaining)));
            if (read <= 0)
            {
                throw new EndOfStreamException("Codex rollout ended before its projection boundary.");
            }
            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool TryReadOrdinal(
        string line,
        out long ordinal,
        out int digitStart,
        out int digitLength)
    {
        ordinal = 0;
        digitStart = 0;
        digitLength = 0;
        const string marker = "\"ordinal\":";
        var searchLength = Math.Min(line.Length, 512);
        var markerIndex = line.IndexOf(marker, 0, searchLength, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        digitStart = markerIndex + marker.Length;
        while (digitStart < line.Length && char.IsWhiteSpace(line[digitStart]))
        {
            digitStart++;
        }
        var end = digitStart;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
        {
            end++;
        }
        digitLength = end - digitStart;
        return digitLength > 0 &&
               long.TryParse(
                   line.AsSpan(digitStart, digitLength),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out ordinal);
    }

    internal static void Validate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "codex-rollout-history-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "09", "03"));
        try
        {
            CodexCliService.EnsureSqliteProvider();
            var threadId = Guid.NewGuid().ToString();
            var rolloutPath = Path.Combine(
                root,
                "sessions",
                "2026",
                "09",
                "03",
                "rollout-" + threadId + ".jsonl");
            var validBefore = "{\"ordinal\":1,\"type\":\"event_msg\"}";
            var invalid =
                "{\"ordinal\":1,\"type\":\"turn_context\",\"payload\":{" +
                "\"active_permission_profile\":{\"id\":\":danger-full-access\"}}}";
            var validAfter = "{\"ordinal\":2,\"type\":\"event_msg\"}";
            File.WriteAllText(
                rolloutPath,
                string.Join('\n', validBefore, invalid, validAfter) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var originalLength = new FileInfo(rolloutPath).Length;
            var projectionOffset = Encoding.UTF8.GetByteCount(validBefore + "\n");

            using (var state = new SqliteConnection("Data Source=" + Path.Combine(root, "state_5.sqlite")))
            {
                state.Open();
                using var command = state.CreateCommand();
                command.CommandText =
                    "CREATE TABLE threads (id TEXT, rollout_path TEXT, history_mode TEXT, updated_at INTEGER);" +
                    "INSERT INTO threads VALUES ($id, $path, 'paginated', 1);";
                command.Parameters.AddWithValue("$id", threadId);
                command.Parameters.AddWithValue("$path", @"\\?\" + rolloutPath);
                command.ExecuteNonQuery();
            }

            using (var history = new SqliteConnection(
                       "Data Source=" + Path.Combine(root, "thread_history_1.sqlite")))
            {
                history.Open();
                using var command = history.CreateCommand();
                command.CommandText =
                    "CREATE TABLE thread_history_projection_state " +
                    "(thread_id TEXT, next_rollout_byte_offset INTEGER, next_rollout_ordinal INTEGER);" +
                    "INSERT INTO thread_history_projection_state VALUES ($id, $offset, 2);";
                command.Parameters.AddWithValue("$id", threadId);
                command.Parameters.AddWithValue("$offset", projectionOffset);
                command.ExecuteNonQuery();
            }

            var result = TryRepairLaggingPaginatedRollouts(root, allowOrdinalRewrite: true);
            var repairedText = File.ReadAllText(rolloutPath);
            if (result.RepairedRecords != 3 ||
                new FileInfo(rolloutPath).Length != originalLength ||
                repairedText.Contains("\"id\":\":danger-full-access\"", StringComparison.Ordinal) ||
                !repairedText.Contains("\"id\":\"danger-full-access\" ", StringComparison.Ordinal) ||
                !repairedText.Contains("{\"ordinal\":2,\"type\":\"turn_context\"", StringComparison.Ordinal) ||
                !repairedText.Contains("{\"ordinal\":3,\"type\":\"event_msg\"}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex rollout history repair validation failed.");
            }

            foreach (var line in File.ReadLines(rolloutPath))
            {
                using var document = JsonDocument.Parse(line);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private readonly record struct ProjectionCursor(long ByteOffset, long NextOrdinal);

    private readonly record struct RolloutCandidate(
        string Path,
        long ProjectionOffset,
        long NextOrdinal);
}
