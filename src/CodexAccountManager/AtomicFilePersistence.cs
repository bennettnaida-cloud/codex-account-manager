using System.Text;

namespace CodexAccountManager;

/// <summary>
/// Durable same-directory replacement used by the manager and gateway state files.
/// Readers never observe a partially-written document; bounded retries absorb the
/// short sharing violations produced by antivirus scanners and overlapping processes.
/// </summary>
internal static class AtomicFilePersistence
{
    internal const int DefaultAttempts = 5;

    internal static string ReadAllTextWithRetry(
        string path,
        Encoding? encoding = null,
        int attempts = DefaultAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        encoding ??= Encoding.UTF8;
        Exception? lastError = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(
                    stream,
                    encoding,
                    detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt + 1 < attempts)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        throw new IOException("The file remained unavailable after bounded retries.", lastError);
    }

    internal static void WriteAllText(
        string path,
        string contents,
        Encoding? encoding = null,
        int attempts = DefaultAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ??
                        throw new InvalidOperationException("Atomic file target has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + ".pending-" + Guid.NewGuid().ToString("N"));
        var bytes = encoding.GetBytes(contents);
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            Exception? lastError = null;
            for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    if (attempt + 1 < attempts)
                    {
                        Thread.Sleep(40 * (attempt + 1));
                    }
                }
            }

            throw new IOException("Atomic file replacement failed after bounded retries.", lastError);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // The unique same-directory temporary file is ignored by readers.
            }
        }
    }
}
