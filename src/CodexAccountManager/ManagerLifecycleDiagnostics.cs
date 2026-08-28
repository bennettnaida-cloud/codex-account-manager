using System.Diagnostics;
using System.Text;

namespace CodexAccountManager;

internal static class ManagerLifecycleDiagnostics
{
    private static readonly object WriteLock = new();

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexAccountManager",
        "Logs",
        "manager-lifecycle.log");

    internal static void Write(string eventName, string? details = null)
    {
        WriteCore(eventName, exception: null, details);
    }

    internal static void WriteException(
        string eventName,
        Exception exception,
        string? details = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteCore(eventName, exception, details);
    }

    private static void WriteCore(
        string eventName,
        Exception? exception,
        string? details)
    {
        try
        {
            var safeEvent = Sanitize(eventName, 120);
            var safeDetails = Sanitize(details, 600);
            var exceptionType = exception?.GetType().FullName ?? "none";
            var target = exception?.TargetSite == null
                ? "none"
                : exception.TargetSite.DeclaringType?.FullName + "." +
                  exception.TargetSite.Name;
            var line = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append("\tpid=").Append(Environment.ProcessId)
                .Append("\tevent=").Append(safeEvent)
                .Append("\texception=").Append(Sanitize(exceptionType, 240))
                .Append("\thresult=").Append(exception?.HResult.ToString("X8") ?? "none")
                .Append("\ttarget=").Append(Sanitize(target, 300));
            if (!string.IsNullOrWhiteSpace(safeDetails))
            {
                line.Append("\tdetails=").Append(safeDetails);
            }
            line.AppendLine();

            lock (WriteLock)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become a new shutdown path.
        }
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}
