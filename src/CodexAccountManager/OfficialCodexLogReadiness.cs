using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexAccountManager;

internal enum OfficialCodexLogReadinessState
{
    Pending,
    Ready,
    PersistedAtomSyncFailed
}

internal enum OfficialCodexLogReadinessStage
{
    InitialLaunch,
    RendererReload
}

internal readonly record struct OfficialCodexLogFileSnapshot(
    long Length,
    long CreationTimeUtcTicks)
{
    internal static OfficialCodexLogFileSnapshot Capture(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException(
                "The official Codex log disappeared while its baseline was captured.",
                fullPath);
        }

        var length = info.Length;
        var creationTimeUtcTicks = info.CreationTimeUtc.Ticks;
        if (length < 0 || creationTimeUtcTicks <= 0)
        {
            throw new IOException(
                "The official Codex log returned invalid baseline metadata.");
        }

        return new OfficialCodexLogFileSnapshot(length, creationTimeUtcTicks);
    }
}

/// <summary>
/// A byte-accurate, fail-closed snapshot of the official Codex t0 logs that existed before a
/// launch or renderer-reload step. Files and bytes already present in this snapshot can never
/// satisfy a later readiness probe.
/// </summary>
internal sealed class OfficialCodexLogBaseline
{
    private readonly IReadOnlyDictionary<string, OfficialCodexLogFileSnapshot> _files;
    private readonly bool _captureComplete;

    private OfficialCodexLogBaseline(
        string root,
        DateTimeOffset capturedAtUtc,
        Dictionary<string, OfficialCodexLogFileSnapshot> files,
        bool captureComplete)
    {
        Root = root;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        _files = files;
        _captureComplete = captureComplete;
    }

    internal string Root { get; }
    internal DateTimeOffset CapturedAtUtc { get; }

    /// <summary>
    /// True only when the AUMID-derived root was resolved and every t0 file present at capture
    /// supplied a length and creation time. A resolved but not-yet-created Logs directory is a
    /// complete empty snapshot; a partial enumeration is never safe as a reload cursor.
    /// </summary>
    internal bool IsUsable => _captureComplete && !string.IsNullOrEmpty(Root);

    internal static OfficialCodexLogBaseline Capture(string? root)
    {
        return CaptureCore(
            ResolveRoot(root),
            OfficialCodexLogProbe.EnumerateT0LogFiles,
            OfficialCodexLogFileSnapshot.Capture,
            static () => DateTimeOffset.UtcNow);
    }

    internal OfficialCodexLogProbe CreateProbe(
        int pid,
        long? activationStartTimeUtcTicks,
        OfficialCodexLogReadinessStage stage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        if (activationStartTimeUtcTicks is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activationStartTimeUtcTicks));
        }
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        return new OfficialCodexLogProbe(
            Root,
            CapturedAtUtc,
            _files,
            _captureComplete,
            pid,
            activationStartTimeUtcTicks,
            stage);
    }

    internal bool TryGetRecordedLength(string path, out long length)
    {
        if (_files.TryGetValue(Path.GetFullPath(path), out var snapshot))
        {
            length = snapshot.Length;
            return true;
        }

        length = 0;
        return false;
    }

    internal static void ValidateFailClosedCapture(string existingRoot)
    {
        if (!Directory.Exists(existingRoot))
        {
            throw new DirectoryNotFoundException(existingRoot);
        }

        var enumerationFailure = CaptureCore(
            existingRoot,
            static _ => throw new IOException("synthetic enumeration failure"),
            OfficialCodexLogFileSnapshot.Capture,
            static () => DateTimeOffset.UtcNow);
        if (enumerationFailure.IsUsable)
        {
            throw new InvalidOperationException(
                "Official Codex log baseline accepted an incomplete directory enumeration.");
        }

        var syntheticPath = Path.Combine(
            existingRoot,
            "codex-desktop-00000000-0000-0000-0000-000000000000-1-t0-i1-000000-0.log");
        var metadataFailure = CaptureCore(
            existingRoot,
            _ => [syntheticPath],
            static _ => throw new IOException("synthetic metadata failure"),
            static () => DateTimeOffset.UtcNow);
        if (metadataFailure.IsUsable ||
            metadataFailure.TryGetRecordedLength(syntheticPath, out _))
        {
            throw new InvalidOperationException(
                "Official Codex log baseline accepted a partial file-metadata snapshot.");
        }
    }

    private static OfficialCodexLogBaseline CaptureCore(
        string resolvedRoot,
        Func<string, IReadOnlyList<string>> enumerate,
        Func<string, OfficialCodexLogFileSnapshot> inspect,
        Func<DateTimeOffset> utcNow)
    {
        var files = new Dictionary<string, OfficialCodexLogFileSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        // A fresh Codex installation creates Logs only after package activation. A trusted,
        // AUMID-derived path that does not exist yet is therefore a complete empty baseline,
        // not a probe failure. An unresolved path remains explicitly unusable.
        var captureComplete = !string.IsNullOrEmpty(resolvedRoot);
        if (captureComplete && Directory.Exists(resolvedRoot))
        {
            try
            {
                foreach (var path in enumerate(resolvedRoot))
                {
                    var fullPath = Path.GetFullPath(path);
                    files[fullPath] = inspect(fullPath);
                }
            }
            catch (Exception ex) when (IsRecoverableCaptureException(ex))
            {
                // A partial cursor is more dangerous than no cursor: an omitted old file would
                // otherwise be treated as newly created and its historical ready lines could
                // satisfy a post-reload probe.
                files.Clear();
                captureComplete = false;
            }
        }

        // Capture completion, rather than capture start, is the temporal boundary. Bytes written
        // after an early per-file length sample but before this instant are rejected by their log
        // timestamp, closing the non-atomic directory-snapshot window.
        var capturedAtUtc = utcNow().ToUniversalTime();
        return new OfficialCodexLogBaseline(
            resolvedRoot,
            capturedAtUtc,
            files,
            captureComplete);
    }

    private static bool IsRecoverableCaptureException(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException;
    }

    private static string ResolveRoot(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(root);
        }

        // Production resolves the package family from the installed AUMID before capture. If
        // that identity cannot be established, keep the baseline explicitly unusable instead of
        // trusting a hard-coded package family that may belong to an older build.
        return string.Empty;
    }
}

/// <summary>
/// Reads only complete lines appended after an <see cref="OfficialCodexLogBaseline"/>, from a
/// filename whose exact PID and t0 index identify the activated process. Initial launch requires
/// app-server-connected, primary-routes-mounted, ready in one file; renderer reload requires a
/// fresh primary-routes-mounted, ready pair in one file.
/// </summary>
internal sealed class OfficialCodexLogProbe
{
    private const int ReadBufferSize = 16 * 1024;
    private const int MaximumLineBytes = 256 * 1024;
    private const int MaximumIncrementBytes = 8 * 1024 * 1024;
    private const string LogTimestampCapture =
        "(?<timestamp>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z)";
    private const string LogTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly TimeSpan MaximumAppServerToRoutesInterval =
        TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumRoutesToReadyInterval =
        TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex T0LogFileNamePattern = CreatePattern(
        "^codex-desktop-(?<session>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})-" +
        "(?<pid>[1-9][0-9]*)-t0-i(?<instance>[1-9][0-9]*)-[0-9]{6}-[0-9]+\\.log$");
    private static readonly Regex AppServerConnectedPattern = CreatePattern(
        "^" + LogTimestampCapture + " info \\[AppServerConnection\\] " +
        "app_server_connection\\.state_changed cause=post_initialize_connection_state " +
        "connectionError=null currentConnectionId=(?<connectionId>[1-9][0-9]*) currentState=connecting " +
        "hasConnection=true hostId=local initialized=true initializeInFlight=false " +
        "next=connected(?: [A-Za-z][A-Za-z0-9]*=[^ \\r\\n]+)* transport=stdio" +
        "(?: [A-Za-z][A-Za-z0-9]*=[^ \\r\\n]+)*$");
    private static readonly Regex RoutesMountedPattern = CreatePattern(
        "^" + LogTimestampCapture + " info " +
        "\\[electron-message-handler\\] \\[startup\\]\\[renderer\\] app routes mounted after [0-9]+ms " +
        "rendererWebContentsId=[1-9][0-9]* " +
        "rendererWindowAppearance=(?<appearance>[A-Za-z][A-Za-z0-9_-]{0,63}) " +
        "rendererWindowFocused=(?:true|false) rendererWindowId=[1-9][0-9]* " +
        "rendererWindowVisible=(?<visible>true|false)$");
    private static readonly Regex ReadyMessagePattern = CreatePattern(
        "^" + LogTimestampCapture + " info " +
        "\\[electron-message-handler\\] Handled 'ready' message, sent ide-context-updated$");
    private static readonly Regex PersistedAtomSyncFailurePattern = CreatePattern(
        "^" + LogTimestampCapture + " error " +
        "\\[electron-message-handler\\] \\[persisted-atom\\] host did not respond to sync request; " +
        "continuing with legacy state only rendererWebContentsId=[1-9][0-9]* " +
        "rendererWindowAppearance=(?<appearance>[A-Za-z][A-Za-z0-9_-]{0,63}) " +
        "rendererWindowFocused=(?:true|false) rendererWindowId=[1-9][0-9]* " +
        "rendererWindowVisible=(?:true|false)$");

    private readonly string _root;
    private readonly IReadOnlyDictionary<string, OfficialCodexLogFileSnapshot> _baselineFiles;
    private readonly bool _baselineCaptureComplete;
    private readonly long _baselineCapturedAtUtcTicks;
    private readonly long _minimumEvidenceUtcTicks;
    private readonly int _pid;
    private readonly OfficialCodexLogReadinessStage _stage;
    private readonly HashSet<string> _invalidatedBaselinePaths = new(
        StringComparer.OrdinalIgnoreCase);

    internal OfficialCodexLogProbe(
        string root,
        DateTimeOffset baselineCapturedAtUtc,
        IReadOnlyDictionary<string, OfficialCodexLogFileSnapshot> baselineFiles,
        bool baselineCaptureComplete,
        int pid,
        long? activationStartTimeUtcTicks,
        OfficialCodexLogReadinessStage stage)
    {
        _root = root;
        _baselineFiles = baselineFiles;
        _baselineCaptureComplete = baselineCaptureComplete;
        _baselineCapturedAtUtcTicks = baselineCapturedAtUtc.UtcDateTime.Ticks;
        _minimumEvidenceUtcTicks = FloorToLogTimestampPrecision(Math.Max(
            _baselineCapturedAtUtcTicks,
            activationStartTimeUtcTicks ?? 0));
        _pid = pid;
        _stage = stage;
    }

    internal bool IsAvailable => _baselineCaptureComplete && !string.IsNullOrEmpty(_root);

    internal OfficialCodexLogReadinessState Poll()
    {
        if (!IsAvailable ||
            !TryEnumerateCurrentIncrementFiles(out var candidates))
        {
            return OfficialCodexLogReadinessState.Pending;
        }

        var aggregate = OfficialCodexLogReadinessState.Pending;
        foreach (var session in candidates
                     .GroupBy(candidate =>
                         new LogSessionKey(candidate.SessionId, candidate.InstanceId)))
        {
            var readiness = new ReadinessAccumulator();
            foreach (var candidate in session
                         .OrderBy(candidate => candidate.CreationTimeUtcTicks)
                         .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
            {
                var state = ScanCompleteIncrementLines(
                    candidate.Path,
                    candidate.Offset,
                    readiness);
                if (state == OfficialCodexLogReadinessState.PersistedAtomSyncFailed)
                {
                    return state;
                }
            }

            if (readiness.ObservedReady)
            {
                aggregate = OfficialCodexLogReadinessState.Ready;
            }
        }

        return aggregate;
    }

    internal static IReadOnlyList<string> EnumerateT0LogFiles(string root)
    {
        var matches = new List<string>();
        if (!Directory.Exists(root))
        {
            return matches;
        }

        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "codex-desktop-*.log",
                     SearchOption.AllDirectories))
        {
            if (T0LogFileNamePattern.IsMatch(Path.GetFileName(path)))
            {
                matches.Add(Path.GetFullPath(path));
            }
        }

        return matches;
    }

    private bool TryEnumerateCurrentIncrementFiles(out List<IncrementFile> candidates)
    {
        candidates = [];
        try
        {
            foreach (var path in EnumerateT0LogFiles(_root))
            {
                var match = T0LogFileNamePattern.Match(Path.GetFileName(path));
                if (!int.TryParse(match.Groups["pid"].Value, out var filePid) ||
                    !int.TryParse(match.Groups["instance"].Value, out var instanceId) ||
                    filePid != _pid)
                {
                    continue;
                }

                var current = OfficialCodexLogFileSnapshot.Capture(path);
                if (_baselineFiles.TryGetValue(path, out var baseline))
                {
                    if (_invalidatedBaselinePaths.Contains(path))
                    {
                        continue;
                    }

                    // A path that was truncated or recreated is not the snapshotted file. Ignore
                    // it permanently for this probe rather than reading replacement bytes.
                    if (current.CreationTimeUtcTicks != baseline.CreationTimeUtcTicks ||
                        current.Length < baseline.Length)
                    {
                        _invalidatedBaselinePaths.Add(path);
                        continue;
                    }
                    if (current.Length == baseline.Length)
                    {
                        continue;
                    }

                    candidates.Add(new IncrementFile(
                        path,
                        baseline.Length,
                        current.CreationTimeUtcTicks,
                        match.Groups["session"].Value,
                        instanceId));
                    continue;
                }

                // A file omitted by a non-atomic enumeration, restored from an old directory, or
                // renamed into place is not fresh merely because its path was absent. Its file
                // creation must be at the baseline boundary. Windows file timestamps can trail
                // DateTime.UtcNow by a sub-millisecond clock-source skew, so tolerate one log
                // timestamp quantum here; strict line timestamps still reject all evidence from
                // the ambiguous boundary millisecond.
                if (current.CreationTimeUtcTicks <
                    _baselineCapturedAtUtcTicks - TimeSpan.TicksPerMillisecond)
                {
                    continue;
                }

                candidates.Add(new IncrementFile(
                    path,
                    0,
                    current.CreationTimeUtcTicks,
                    match.Groups["session"].Value,
                    instanceId));
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            candidates.Clear();
            return false;
        }
    }

    private OfficialCodexLogReadinessState ScanCompleteIncrementLines(
        string path,
        long offset,
        ReadinessAccumulator readiness)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.SequentialScan);
            var snapshotLength = stream.Length;
            if (offset < 0 || offset > snapshotLength)
            {
                readiness.Invalidate();
                return OfficialCodexLogReadinessState.Pending;
            }

            stream.Position = offset;
            var remaining = snapshotLength - offset;
            if (remaining > MaximumIncrementBytes)
            {
                readiness.Invalidate();
                return OfficialCodexLogReadinessState.Pending;
            }

            var buffer = new byte[ReadBufferSize];
            using var lineBytes = new MemoryStream();
            var lineOverflowed = false;

            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    break;
                }

                remaining -= read;
                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    AppendLineSegment(
                        lineBytes,
                        buffer,
                        segmentStart,
                        index - segmentStart,
                        ref lineOverflowed);
                    if (!lineOverflowed && TryDecodeCompleteLine(lineBytes, out var line))
                    {
                        var failure = PersistedAtomSyncFailurePattern.Match(line);
                        if (failure.Success &&
                            IsFreshTimestamp(failure, out _) &&
                            failure.Groups["appearance"].Value.Equals(
                                "primary",
                                StringComparison.Ordinal))
                        {
                            return OfficialCodexLogReadinessState.PersistedAtomSyncFailed;
                        }

                        if (_stage == OfficialCodexLogReadinessStage.InitialLaunch)
                        {
                            var appServerConnected = AppServerConnectedPattern.Match(line);
                            if (appServerConnected.Success)
                            {
                                if (IsFreshTimestamp(appServerConnected, out var connectedAt) &&
                                    int.TryParse(
                                        appServerConnected.Groups["connectionId"].Value,
                                        NumberStyles.None,
                                        CultureInfo.InvariantCulture,
                                        out var connectionId))
                                {
                                    // The desktop logger can emit the same state-change record
                                    // twice. Do not let a duplicate for the same immutable
                                    // connection generation erase routes that mounted between
                                    // the two writes. A genuinely new connection still resets
                                    // the renderer evidence and must complete the full chain.
                                    if (readiness.AppServerConnectionId != connectionId)
                                    {
                                        readiness.ObservedReady = false;
                                        readiness.PrimaryRoutesMountedAt = null;
                                        readiness.AppServerConnectedAt = connectedAt;
                                        readiness.AppServerConnectionId = connectionId;
                                    }
                                }
                                else
                                {
                                    readiness.ObservedReady = false;
                                    readiness.PrimaryRoutesMountedAt = null;
                                    readiness.AppServerConnectedAt = null;
                                    readiness.AppServerConnectionId = null;
                                }
                                ResetLineBuffer(lineBytes, ref lineOverflowed);
                                segmentStart = index + 1;
                                continue;
                            }
                        }

                        var routesMounted = RoutesMountedPattern.Match(line);
                        if (routesMounted.Success)
                        {
                            readiness.PrimaryRoutesMountedAt = null;
                            var isPrimary = routesMounted.Groups["appearance"].Value.Equals(
                                "primary",
                                StringComparison.Ordinal);
                            if (isPrimary)
                            {
                                // A later primary renderer generation supersedes an older ready
                                // handshake. Poll rescans from the immutable baseline, so its
                                // final state must describe the newest relevant generation.
                                readiness.ObservedReady = false;
                            }
                            if (isPrimary &&
                                IsFreshTimestamp(routesMounted, out var routesAt) &&
                                routesMounted.Groups["visible"].Value.Equals(
                                    "true",
                                    StringComparison.Ordinal) &&
                                (_stage == OfficialCodexLogReadinessStage.RendererReload ||
                                 IsOrderedWithin(
                                     readiness.AppServerConnectedAt,
                                     routesAt,
                                     MaximumAppServerToRoutesInterval)))
                            {
                                readiness.PrimaryRoutesMountedAt = routesAt;
                            }
                        }
                        else
                        {
                            var ready = ReadyMessagePattern.Match(line);
                            if (ready.Success)
                            {
                                if (IsFreshTimestamp(ready, out var readyAt) &&
                                    IsOrderedWithin(
                                        readiness.PrimaryRoutesMountedAt,
                                        readyAt,
                                        MaximumRoutesToReadyInterval))
                                {
                                    readiness.ObservedReady = true;
                                }

                                readiness.PrimaryRoutesMountedAt = null;
                            }
                        }
                    }

                    ResetLineBuffer(lineBytes, ref lineOverflowed);
                    segmentStart = index + 1;
                }

                AppendLineSegment(
                    lineBytes,
                    buffer,
                    segmentStart,
                    read - segmentStart,
                    ref lineOverflowed);
            }

            if (remaining != 0)
            {
                readiness.Invalidate();
                return OfficialCodexLogReadinessState.Pending;
            }

            // Without a terminating LF, the writer may still be producing the line. It cannot be
            // readiness or failure evidence until a later poll observes the terminator.
            return readiness.ObservedReady
                ? OfficialCodexLogReadinessState.Ready
                : OfficialCodexLogReadinessState.Pending;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            readiness.Invalidate();
            return OfficialCodexLogReadinessState.Pending;
        }
    }

    private bool IsFreshTimestamp(Match match, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return DateTimeOffset.TryParseExact(
                   match.Groups["timestamp"].Value,
                   LogTimestampFormat,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out timestamp) &&
               // Millisecond log timestamps cannot prove whether an event in the
               // baseline boundary's own millisecond happened before or after the
               // byte cursor was completed. Require the next representable log tick;
               // ordering between later events is independently guaranteed by their
               // complete-line order in this same file.
               timestamp.UtcDateTime.Ticks > _minimumEvidenceUtcTicks;
    }

    private static bool IsOrderedWithin(
        DateTimeOffset? earlier,
        DateTimeOffset later,
        TimeSpan maximumInterval)
    {
        if (!earlier.HasValue)
        {
            return false;
        }

        var interval = later - earlier.Value;
        // Codex writes timestamps at millisecond precision and can log routes + ready
        // in the same millisecond. The scanner's line order proves causality; timestamps
        // only reject clock reversal and implausibly stale evidence.
        return interval >= TimeSpan.Zero && interval <= maximumInterval;
    }

    private static long FloorToLogTimestampPrecision(long ticks)
    {
        return ticks - ticks % TimeSpan.TicksPerMillisecond;
    }

    private static void ResetLineBuffer(
        MemoryStream lineBytes,
        ref bool lineOverflowed)
    {
        lineBytes.SetLength(0);
        lineOverflowed = false;
    }

    private static void AppendLineSegment(
        MemoryStream line,
        byte[] source,
        int offset,
        int count,
        ref bool overflowed)
    {
        if (count <= 0 || overflowed)
        {
            return;
        }

        if (line.Length + count > MaximumLineBytes)
        {
            line.SetLength(0);
            overflowed = true;
            return;
        }

        line.Write(source, offset, count);
    }

    private static bool TryDecodeCompleteLine(MemoryStream bytes, out string line)
    {
        line = string.Empty;
        if (!bytes.TryGetBuffer(out var buffer))
        {
            return false;
        }

        var count = checked((int)bytes.Length);
        if (count > 0 && buffer.Array![buffer.Offset + count - 1] == (byte)'\r')
        {
            count--;
        }

        try
        {
            line = StrictUtf8.GetString(buffer.Array!, buffer.Offset, count);
            return true;
        }
        catch (DecoderFallbackException)
        {
            // A baseline captured during a UTF-8 write can begin in the middle of a code point.
            // Skip that fragment; later complete ASCII log lines remain independently usable.
            return false;
        }
    }

    private static Regex CreatePattern(string pattern)
    {
        return new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));
    }

    private sealed class ReadinessAccumulator
    {
        internal DateTimeOffset? AppServerConnectedAt { get; set; }
        internal int? AppServerConnectionId { get; set; }
        internal DateTimeOffset? PrimaryRoutesMountedAt { get; set; }
        internal bool ObservedReady { get; set; }

        internal void Invalidate()
        {
            AppServerConnectedAt = null;
            AppServerConnectionId = null;
            PrimaryRoutesMountedAt = null;
            ObservedReady = false;
        }
    }

    private sealed record LogSessionKey(string SessionId, int InstanceId);
    private sealed record IncrementFile(
        string Path,
        long Offset,
        long CreationTimeUtcTicks,
        string SessionId,
        int InstanceId);
}

/// <summary>
/// Executable parser, file-identity, temporal-ordering and baseline contract tests.
/// </summary>
internal static class OfficialCodexLogReadiness
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    internal static void Validate()
    {
        Expect(
            !OfficialCodexLogBaseline.Capture(null).IsUsable,
            "a missing AUMID-derived log root did not fail closed");

        var root = Path.Combine(
            Path.GetTempPath(),
            "CodexAccountManager-OfficialCodexLogReadiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            OfficialCodexLogBaseline.ValidateFailClosedCapture(
                CreateTestDirectory(root, "capture-failure"));
            ValidateInitiallyMissingLogDirectory(
                Path.Combine(root, "missing-at-capture"));
            ValidateBaselinePidAvatarChatAndHalfLine(
                CreateTestDirectory(root, "baseline-and-lines"));
            ValidateInitialAndReloadStages(
                CreateTestDirectory(root, "stages"));
            ValidateCrossFileRejection(
                CreateTestDirectory(root, "cross-file"));
            ValidateTemporalOrderingAndBounds(
                CreateTestDirectory(root, "temporal"));
            ValidateStaleNewAndReplacementFiles(
                CreateTestDirectory(root, "file-identity"));
            ValidatePersistedAtomFailure(
                CreateTestDirectory(root, "persisted-atom"));
            ValidateBaselineBoundary(
                CreateTestDirectory(root, "baseline-boundary"));
            ValidateReadBounds(
                CreateTestDirectory(root, "read-bounds"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A scanner or antivirus may briefly retain a handle; cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Do not hide the actual parser assertion behind temporary-file cleanup.
            }
        }
    }

    private static void ValidateInitiallyMissingLogDirectory(string directory)
    {
        Expect(!Directory.Exists(directory), "the missing-log-directory fixture already existed");
        var baseline = OfficialCodexLogBaseline.Capture(directory);
        Expect(
            baseline.IsUsable,
            "a resolved but not-yet-created Logs directory was not a complete empty baseline");

        const int pid = 40501;
        var clock = EvidenceClock.After(baseline);
        var probe = CreateProbe(
            baseline,
            pid,
            clock.Activation,
            OfficialCodexLogReadinessStage.InitialLaunch);
        Expect(probe.IsAvailable, "an empty pre-activation log baseline was unavailable");
        ExpectState(
            probe,
            OfficialCodexLogReadinessState.Pending,
            "a missing Logs directory produced readiness evidence");

        Directory.CreateDirectory(directory);
        var path = NewT0Path(directory, pid);
        File.WriteAllText(path, ValidInitialSequence(clock), Utf8WithoutBom);
        File.SetCreationTimeUtc(
            path,
            baseline.CapturedAtUtc.AddMilliseconds(50).UtcDateTime);
        ExpectState(
            probe,
            OfficialCodexLogReadinessState.Ready,
            "a log directory created by the activated package was never observed");
    }

    private static void ValidateBaselinePidAvatarChatAndHalfLine(string directory)
    {
        const int pid = 41001;
        var path = NewT0Path(directory, pid);
        var oldAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var oldBytes =
            AppServerConnected(oldAt) + "\n" +
            PrimaryRoute(oldAt.AddSeconds(1)) + "\n" +
            Ready(oldAt.AddSeconds(2)) + "\n";
        File.WriteAllText(path, oldBytes, Utf8WithoutBom);

        var baseline = CaptureUsable(directory);
        Expect(
            baseline.TryGetRecordedLength(path, out var recordedLength) &&
            recordedLength == Utf8WithoutBom.GetByteCount(oldBytes),
            "baseline did not record the existing file byte length");
        var clock = EvidenceClock.After(baseline);
        var probe = CreateProbe(
            baseline,
            pid,
            clock.Activation,
            OfficialCodexLogReadinessStage.InitialLaunch);
        Expect(probe.IsAvailable, "a complete official log baseline was unavailable");
        ExpectState(probe, OfficialCodexLogReadinessState.Pending, "old baseline lines were accepted");

        File.WriteAllText(
            NewT0Path(directory, pid + 100),
            ValidInitialSequence(clock),
            Utf8WithoutBom);
        File.WriteAllText(
            NewThreadPath(directory, pid, thread: 1),
            ValidInitialSequence(clock),
            Utf8WithoutBom);
        ExpectState(probe, OfficialCodexLogReadinessState.Pending, "a wrong PID or t1 log was accepted");

        File.AppendAllText(
            path,
            AppServerConnected(clock.AppServer) + "\n" +
            Route(clock.Routes, appearance: "avatarOverlay", visible: false, webContentsId: 2) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(probe, OfficialCodexLogReadinessState.Pending, "avatarOverlay readiness was accepted");

        File.AppendAllText(path, ChatContainingMarkers(clock.Ready.AddSeconds(1)) + "\n", Utf8WithoutBom);
        ExpectState(probe, OfficialCodexLogReadinessState.Pending, "chat text containing markers was accepted");

        var finalClock = clock.Shift(TimeSpan.FromSeconds(3));
        File.AppendAllText(
            path,
            AppServerConnected(finalClock.AppServer) + "\n" +
            PrimaryRoute(finalClock.Routes) + "\n" +
            Ready(finalClock.Ready),
            Utf8WithoutBom);
        ExpectState(probe, OfficialCodexLogReadinessState.Pending, "a non-terminated ready line was accepted");
        File.AppendAllText(path, "\n", Utf8WithoutBom);
        ExpectState(probe, OfficialCodexLogReadinessState.Ready, "a complete strict launch sequence was rejected");
    }

    private static void ValidateInitialAndReloadStages(string directory)
    {
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);

        const int noAppServerPid = 42001;
        File.WriteAllText(
            NewT0Path(directory, noAppServerPid),
            PrimaryRoute(clock.Routes) + "\n" + Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                noAppServerPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "initial launch accepted routes/ready without app-server connected");
        ExpectState(
            CreateProbe(
                baseline,
                noAppServerPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Ready,
            "renderer reload rejected a fresh routes/ready pair");

        const int validInitialPid = 42002;
        File.WriteAllText(
            NewT0Path(directory, validInitialPid),
            ValidInitialSequence(clock),
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                validInitialPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Ready,
            "initial launch rejected app-server/routes/ready");

        const int invisiblePid = 42003;
        File.WriteAllText(
            NewT0Path(directory, invisiblePid),
            AppServerConnected(clock.AppServer) + "\n" +
            Route(clock.Routes, appearance: "primary", visible: false) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                invisiblePid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an invisible primary renderer was accepted");

        const int interveningAvatarPid = 42004;
        File.WriteAllText(
            NewT0Path(directory, interveningAvatarPid),
            AppServerConnected(clock.AppServer) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Route(
                clock.Routes.AddMilliseconds(5),
                appearance: "avatarOverlay",
                visible: false,
                webContentsId: 2) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                interveningAvatarPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an avatarOverlay ready message was associated with an earlier primary route");

        const int nonStdioPid = 42005;
        File.WriteAllText(
            NewT0Path(directory, nonStdioPid),
            AppServerConnected(clock.AppServer, transport: "websocket") + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                nonStdioPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "initial launch accepted a non-stdio app-server connection");

        const int duplicateConnectionPid = 42006;
        File.WriteAllText(
            NewT0Path(directory, duplicateConnectionPid),
            AppServerConnected(clock.AppServer, connectionId: 7) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            AppServerConnected(
                clock.Routes.AddMilliseconds(5),
                connectionId: 7) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                duplicateConnectionPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Ready,
            "a duplicate app-server state-change record erased valid primary routes");

        const int supersededConnectionPid = 42007;
        File.WriteAllText(
            NewT0Path(directory, supersededConnectionPid),
            ValidInitialSequence(clock) +
            AppServerConnected(
                clock.Ready.AddMilliseconds(5),
                connectionId: 2) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                supersededConnectionPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an older ready handshake survived a newer incomplete app-server generation");

        const int supersededRendererPid = 42008;
        File.WriteAllText(
            NewT0Path(directory, supersededRendererPid),
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Ready) + "\n" +
            PrimaryRoute(clock.Ready.AddMilliseconds(5)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                supersededRendererPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Pending,
            "an older ready handshake survived a newer incomplete primary renderer generation");
    }

    private static void ValidateCrossFileRejection(string directory)
    {
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);

        const int initialPid = 43001;
        File.WriteAllText(
            NewT0Path(directory, initialPid),
            AppServerConnected(clock.AppServer) + "\n",
            Utf8WithoutBom);
        File.WriteAllText(
            NewT0Path(directory, initialPid),
            PrimaryRoute(clock.Routes) + "\n" + Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                initialPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "initial readiness was assembled across different log files");

        const int reloadPid = 43002;
        File.WriteAllText(
            NewT0Path(directory, reloadPid),
            PrimaryRoute(clock.Routes) + "\n",
            Utf8WithoutBom);
        File.WriteAllText(
            NewT0Path(directory, reloadPid),
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                reloadPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Pending,
            "renderer-reload readiness was assembled across different log files");

        const int rolledInitialPid = 43003;
        var initialSession = Guid.NewGuid();
        var initialFirstPath = NewT0Path(directory, rolledInitialPid, initialSession, part: 0);
        var initialSecondPath = NewT0Path(directory, rolledInitialPid, initialSession, part: 1);
        File.WriteAllText(
            initialFirstPath,
            AppServerConnected(clock.AppServer) + "\n",
            Utf8WithoutBom);
        File.WriteAllText(
            initialSecondPath,
            PrimaryRoute(clock.Routes) + "\n" + Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        File.SetCreationTimeUtc(
            initialFirstPath,
            baseline.CapturedAtUtc.AddMilliseconds(10).UtcDateTime);
        File.SetCreationTimeUtc(
            initialSecondPath,
            baseline.CapturedAtUtc.AddMilliseconds(20).UtcDateTime);
        ExpectState(
            CreateProbe(
                baseline,
                rolledInitialPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Ready,
            "a valid initial handshake split by a same-session log roll was rejected");

        const int rolledReloadPid = 43004;
        var reloadSession = Guid.NewGuid();
        var reloadFirstPath = NewT0Path(directory, rolledReloadPid, reloadSession, part: 0);
        var reloadSecondPath = NewT0Path(directory, rolledReloadPid, reloadSession, part: 1);
        File.WriteAllText(reloadFirstPath, PrimaryRoute(clock.Routes) + "\n", Utf8WithoutBom);
        File.WriteAllText(reloadSecondPath, Ready(clock.Ready) + "\n", Utf8WithoutBom);
        File.SetCreationTimeUtc(
            reloadFirstPath,
            baseline.CapturedAtUtc.AddMilliseconds(10).UtcDateTime);
        File.SetCreationTimeUtc(
            reloadSecondPath,
            baseline.CapturedAtUtc.AddMilliseconds(20).UtcDateTime);
        ExpectState(
            CreateProbe(
                baseline,
                rolledReloadPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Ready,
            "a valid renderer handshake split by a same-session log roll was rejected");
    }

    private static void ValidateTemporalOrderingAndBounds(string directory)
    {
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);

        const int stalePid = 44001;
        var staleAt = baseline.CapturedAtUtc.AddMinutes(-1);
        File.WriteAllText(
            NewT0Path(directory, stalePid),
            AppServerConnected(staleAt) + "\n" +
            PrimaryRoute(staleAt.AddSeconds(1)) + "\n" +
            Ready(staleAt.AddSeconds(2)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                stalePid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "evidence timestamped before the baseline/activation was accepted");

        const int reversedLinesPid = 44002;
        File.WriteAllText(
            NewT0Path(directory, reversedLinesPid),
            AppServerConnected(clock.AppServer) + "\n" +
            Ready(clock.Ready) + "\n" +
            PrimaryRoute(clock.Routes) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                reversedLinesPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "a ready line preceding routes mounted was accepted");

        const int reversedTimestampPid = 44003;
        File.WriteAllText(
            NewT0Path(directory, reversedTimestampPid),
            AppServerConnected(clock.AppServer) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Routes.AddMilliseconds(-1)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                reversedTimestampPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "a ready timestamp earlier than routes mounted was accepted");

        const int sameMillisecondPid = 44006;
        File.WriteAllText(
            NewT0Path(directory, sameMillisecondPid),
            AppServerConnected(clock.AppServer) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Routes) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                sameMillisecondPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Ready,
            "same-millisecond routes and ready lines were rejected despite their file order");

        const int longReadyGapPid = 44004;
        File.WriteAllText(
            NewT0Path(directory, longReadyGapPid),
            AppServerConnected(clock.AppServer) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Routes.AddSeconds(31)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                longReadyGapPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an unbounded routes-to-ready interval was accepted");

        const int longRoutesGapPid = 44005;
        var lateRoutes = clock.AppServer.AddSeconds(91);
        File.WriteAllText(
            NewT0Path(directory, longRoutesGapPid),
            AppServerConnected(clock.AppServer) + "\n" +
            PrimaryRoute(lateRoutes) + "\n" +
            Ready(lateRoutes.AddMilliseconds(5)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                longRoutesGapPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an unbounded app-server-to-routes interval was accepted");
    }

    private static void ValidateStaleNewAndReplacementFiles(string directory)
    {
        var replacementDirectory = CreateTestDirectory(directory, "replacement");
        const int replacementPid = 45001;
        var replacementPath = NewT0Path(replacementDirectory, replacementPid);
        File.WriteAllText(replacementPath, "baseline seed\n", Utf8WithoutBom);
        var replacementBaseline = CaptureUsable(replacementDirectory);
        var replacementClock = EvidenceClock.After(replacementBaseline);
        File.Delete(replacementPath);
        File.WriteAllText(
            replacementPath,
            ValidInitialSequence(replacementClock),
            Utf8WithoutBom);
        File.SetCreationTimeUtc(
            replacementPath,
            replacementBaseline.CapturedAtUtc.AddMinutes(2).UtcDateTime);
        ExpectState(
            CreateProbe(
                replacementBaseline,
                replacementPid,
                replacementClock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "a replacement at a snapshotted path was accepted");

        var truncatedDirectory = CreateTestDirectory(directory, "truncated");
        const int truncatedPid = 45003;
        var truncatedPath = NewT0Path(truncatedDirectory, truncatedPid);
        File.WriteAllText(truncatedPath, "baseline seed that must remain stable\n", Utf8WithoutBom);
        var truncatedBaseline = CaptureUsable(truncatedDirectory);
        var truncatedClock = EvidenceClock.After(truncatedBaseline);
        var truncatedProbe = CreateProbe(
            truncatedBaseline,
            truncatedPid,
            truncatedClock.Activation,
            OfficialCodexLogReadinessStage.InitialLaunch);
        File.WriteAllText(truncatedPath, string.Empty, Utf8WithoutBom);
        ExpectState(
            truncatedProbe,
            OfficialCodexLogReadinessState.Pending,
            "a truncated baseline file produced readiness evidence");
        File.WriteAllText(
            truncatedPath,
            new string('x', 128) + "\n" + ValidInitialSequence(truncatedClock),
            Utf8WithoutBom);
        ExpectState(
            truncatedProbe,
            OfficialCodexLogReadinessState.Pending,
            "a previously observed truncated file was accepted after regrowing");

        var staleNewDirectory = CreateTestDirectory(directory, "stale-new");
        var staleBaseline = CaptureUsable(staleNewDirectory);
        var staleClock = EvidenceClock.After(staleBaseline);
        const int staleNewPid = 45002;
        var staleNewPath = NewT0Path(staleNewDirectory, staleNewPid);
        File.WriteAllText(
            staleNewPath,
            ValidInitialSequence(staleClock),
            Utf8WithoutBom);
        File.SetCreationTimeUtc(
            staleNewPath,
            staleBaseline.CapturedAtUtc.AddMinutes(-2).UtcDateTime);
        ExpectState(
            CreateProbe(
                staleBaseline,
                staleNewPid,
                staleClock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "a stale file absent from the baseline path set was accepted as new");
    }

    private static void ValidatePersistedAtomFailure(string directory)
    {
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);

        const int failedPid = 46001;
        File.WriteAllText(
            NewT0Path(directory, failedPid),
            PersistedAtomFailure(clock.AppServer, appearance: "primary") + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                failedPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.PersistedAtomSyncFailed,
            "the strict primary persisted-atom timeout was not failed");

        const int avatarFailurePid = 46002;
        File.WriteAllText(
            NewT0Path(directory, avatarFailurePid),
            PersistedAtomFailure(clock.AppServer, appearance: "avatarOverlay") + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                avatarFailurePid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "an avatarOverlay persisted-atom timeout failed the primary probe");

        const int chatFailurePid = 46003;
        File.WriteAllText(
            NewT0Path(directory, chatFailurePid),
            Timestamp(clock.AppServer) +
            " info [electron-message-handler] Reasoning summary item completed " +
            "summary=[\"[persisted-atom] host did not respond to sync request; continuing with legacy state only\"]\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                chatFailurePid,
                clock.Activation,
                OfficialCodexLogReadinessStage.InitialLaunch),
            OfficialCodexLogReadinessState.Pending,
            "chat text containing the persisted-atom phrase failed the probe");

        const int halfFailurePid = 46004;
        var halfFailurePath = NewT0Path(directory, halfFailurePid);
        File.WriteAllText(
            halfFailurePath,
            PersistedAtomFailure(clock.AppServer, appearance: "primary"),
            Utf8WithoutBom);
        var halfFailureProbe = CreateProbe(
            baseline,
            halfFailurePid,
            clock.Activation,
            OfficialCodexLogReadinessStage.InitialLaunch);
        ExpectState(
            halfFailureProbe,
            OfficialCodexLogReadinessState.Pending,
            "a non-terminated persisted-atom failure was accepted");
        File.AppendAllText(halfFailurePath, "\n", Utf8WithoutBom);
        ExpectState(
            halfFailureProbe,
            OfficialCodexLogReadinessState.PersistedAtomSyncFailed,
            "a completed persisted-atom failure was not accepted");
    }

    private static void ValidateBaselineBoundary(string directory)
    {
        const int pid = 47001;
        var path = NewT0Path(directory, pid);
        const int boundaryMillisecondPid = 47002;
        var boundaryMillisecondPath = NewT0Path(directory, boundaryMillisecondPid);
        var oldRoute = PrimaryRoute(DateTimeOffset.UtcNow.AddMinutes(1));
        var split = oldRoute.Length / 2;
        File.WriteAllText(path, oldRoute[..split], Utf8WithoutBom);
        File.WriteAllText(boundaryMillisecondPath, "baseline seed\n", Utf8WithoutBom);
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);
        var probe = CreateProbe(
            baseline,
            pid,
            clock.Activation,
            OfficialCodexLogReadinessStage.RendererReload);
        File.AppendAllText(
            path,
            oldRoute[split..] + "\n" + Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            probe,
            OfficialCodexLogReadinessState.Pending,
            "a route line that began before the baseline boundary was accepted");

        File.AppendAllText(
            path,
            PrimaryRoute(clock.Routes.AddSeconds(2)) + "\n" +
            Ready(clock.Ready.AddSeconds(2)) + "\n",
            Utf8WithoutBom);
        ExpectState(
            probe,
            OfficialCodexLogReadinessState.Ready,
            "a later complete line pair after a split baseline fragment was rejected");

        var boundaryMillisecond = new DateTimeOffset(
            baseline.CapturedAtUtc.UtcDateTime.Ticks -
            baseline.CapturedAtUtc.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);
        var boundaryProbe = CreateProbe(
            baseline,
            boundaryMillisecondPid,
            baseline.CapturedAtUtc,
            OfficialCodexLogReadinessStage.RendererReload);
        File.AppendAllText(
            boundaryMillisecondPath,
            PrimaryRoute(boundaryMillisecond) + "\n" +
            Ready(boundaryMillisecond) + "\n",
            Utf8WithoutBom);
        ExpectState(
            boundaryProbe,
            OfficialCodexLogReadinessState.Pending,
            "ambiguous evidence from the baseline boundary millisecond was accepted");

        File.AppendAllText(
            boundaryMillisecondPath,
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Routes) + "\n",
            Utf8WithoutBom);
        ExpectState(
            boundaryProbe,
            OfficialCodexLogReadinessState.Ready,
            "fresh same-millisecond evidence after the baseline boundary was rejected");
    }

    private static void ValidateReadBounds(string directory)
    {
        var baseline = CaptureUsable(directory);
        var clock = EvidenceClock.After(baseline);

        const int oversizedLinePid = 48001;
        File.WriteAllText(
            NewT0Path(directory, oversizedLinePid),
            new string('x', 256 * 1024 + 1) + "\n" +
            PrimaryRoute(clock.Routes) + "\n" +
            Ready(clock.Ready) + "\n",
            Utf8WithoutBom);
        ExpectState(
            CreateProbe(
                baseline,
                oversizedLinePid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Ready,
            "an oversized unrelated line prevented later bounded readiness evidence");

        const int oversizedIncrementPid = 48002;
        var oversizedIncrementPath = NewT0Path(directory, oversizedIncrementPid);
        using (var stream = new FileStream(
                   oversizedIncrementPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(8L * 1024 * 1024 + 1);
        }
        ExpectState(
            CreateProbe(
                baseline,
                oversizedIncrementPid,
                clock.Activation,
                OfficialCodexLogReadinessStage.RendererReload),
            OfficialCodexLogReadinessState.Pending,
            "an oversized startup increment was not rejected");
    }

    private static OfficialCodexLogBaseline CaptureUsable(string directory)
    {
        var baseline = OfficialCodexLogBaseline.Capture(directory);
        Expect(baseline.IsUsable, "a complete baseline was not usable");
        return baseline;
    }

    private static OfficialCodexLogProbe CreateProbe(
        OfficialCodexLogBaseline baseline,
        int pid,
        DateTimeOffset activation,
        OfficialCodexLogReadinessStage stage)
    {
        return baseline.CreateProbe(pid, activation.UtcDateTime.Ticks, stage);
    }

    private static void ExpectState(
        OfficialCodexLogProbe probe,
        OfficialCodexLogReadinessState expected,
        string message)
    {
        var actual = probe.Poll();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Official Codex log readiness self-test failed: {message}; " +
                $"expected={expected}; actual={actual}.");
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Official Codex log readiness self-test failed: " + message + ".");
        }
    }

    private static string CreateTestDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string NewT0Path(string directory, int pid)
    {
        return NewThreadPath(directory, pid, thread: 0);
    }

    private static string NewT0Path(
        string directory,
        int pid,
        Guid session,
        int part)
    {
        return Path.Combine(
            directory,
            $"codex-desktop-{session:D}-{pid}-t0-i1-010203-{part}.log");
    }

    private static string NewThreadPath(string directory, int pid, int thread)
    {
        return Path.Combine(
            directory,
            $"codex-desktop-{Guid.NewGuid():D}-{pid}-t{thread}-i1-010203-0.log");
    }

    private static string ValidInitialSequence(EvidenceClock clock)
    {
        return AppServerConnected(clock.AppServer) + "\n" +
               PrimaryRoute(clock.Routes) + "\n" +
               Ready(clock.Ready) + "\n";
    }

    private static string AppServerConnected(
        DateTimeOffset at,
        int connectionId = 1,
        string transport = "stdio")
    {
        return Timestamp(at) +
               " info [AppServerConnection] app_server_connection.state_changed " +
               "cause=post_initialize_connection_state connectionError=null " +
               $"currentConnectionId={connectionId} currentState=connecting hasConnection=true " +
               "hostId=local initialized=true initializeInFlight=false next=connected " +
               "pendingClientRequests=0 pendingInternalRequests=0 previous=connecting " +
               "reconnectAttempt=0 reconnectTimerScheduled=false restartInFlight=false " +
               $"targetConnectionId={connectionId} targetReadyState=1 transport={transport}";
    }

    private static string PrimaryRoute(DateTimeOffset at)
    {
        return Route(at, appearance: "primary", visible: true);
    }

    private static string Route(
        DateTimeOffset at,
        string appearance,
        bool visible,
        int webContentsId = 1)
    {
        return Timestamp(at) +
               " info [electron-message-handler] [startup][renderer] app routes mounted after 4553ms " +
               $"rendererWebContentsId={webContentsId} rendererWindowAppearance={appearance} " +
               $"rendererWindowFocused=false rendererWindowId={webContentsId} " +
               $"rendererWindowVisible={visible.ToString().ToLowerInvariant()}";
    }

    private static string Ready(DateTimeOffset at)
    {
        return Timestamp(at) +
               " info [electron-message-handler] Handled 'ready' message, sent ide-context-updated";
    }

    private static string PersistedAtomFailure(DateTimeOffset at, string appearance)
    {
        return Timestamp(at) +
               " error [electron-message-handler] [persisted-atom] host did not respond to sync request; " +
               "continuing with legacy state only rendererWebContentsId=1 " +
               $"rendererWindowAppearance={appearance} rendererWindowFocused=true " +
               "rendererWindowId=1 rendererWindowVisible=true";
    }

    private static string ChatContainingMarkers(DateTimeOffset at)
    {
        return Timestamp(at) +
               " info [electron-message-handler] Reasoning summary item completed " +
               "summary=[\"app routes mounted\",\"Handled 'ready' message, sent ide-context-updated\"] " +
               "rendererWebContentsId=1 rendererWindowAppearance=primary rendererWindowFocused=true " +
               "rendererWindowId=1 rendererWindowVisible=true";
    }

    private static string Timestamp(DateTimeOffset at)
    {
        return at.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
    }

    private sealed record EvidenceClock(
        DateTimeOffset Activation,
        DateTimeOffset AppServer,
        DateTimeOffset Routes,
        DateTimeOffset Ready)
    {
        internal static EvidenceClock After(OfficialCodexLogBaseline baseline)
        {
            var activation = baseline.CapturedAtUtc.AddMilliseconds(100);
            return new EvidenceClock(
                activation,
                activation.AddMilliseconds(100),
                activation.AddMilliseconds(200),
                activation.AddMilliseconds(210));
        }

        internal EvidenceClock Shift(TimeSpan offset)
        {
            return new EvidenceClock(
                Activation + offset,
                AppServer + offset,
                Routes + offset,
                Ready + offset);
        }
    }
}
