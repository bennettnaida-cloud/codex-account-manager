using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CodexAccountManager;

/// <summary>
/// Authenticates control-plane calls to the loopback PAT gateway.  A fixed marker
/// identifies the protocol, while this per-installation secret prevents an
/// unrelated local listener from being mistaken for our gateway.
/// </summary>
internal static class LocalPatGatewayControl
{
    internal const string ChallengeHeader = "X-Codex-Account-Manager-Challenge";
    internal const string ProofHeader = "X-Codex-Account-Manager-Proof";

    private const string SecretFileName = "pat-gateway-control-v1";
    private const string HealthPurpose = "health";
    private const string ShutdownPurpose = "shutdown";
    private static readonly object Gate = new();

    internal static string LoadOrCreateSecret()
    {
        lock (Gate)
        {
            var path = GetSecretPath();
            if (TryReadSecret(path, out var existing))
            {
                return existing;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    generated,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                try
                {
                    File.Move(temporaryPath, path);
                }
                catch (IOException) when (TryReadSecret(path, out existing))
                {
                    // Another process created the control file first.
                    return existing;
                }

                return generated;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // A leftover temporary file is harmless.
                }
            }
        }
    }

    internal static string CreateChallenge()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    internal static string CreateProof(string secret, string challenge, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes(purpose + "\n" + challenge);
        return Convert.ToHexString(hmac.ComputeHash(payload));
    }

    internal static bool ValidateRequest(
        HttpListenerRequest request,
        string secret,
        string purpose)
    {
        var challenge = request.Headers[ChallengeHeader]?.Trim();
        var proof = request.Headers[ProofHeader]?.Trim();
        if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(proof))
        {
            return false;
        }

        var expected = CreateProof(secret, challenge, purpose);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(proof));
    }

    internal static string CreateHealthProof(string secret, string challenge)
    {
        return CreateProof(secret, challenge, HealthPurpose);
    }

    internal static string CreateShutdownProof(string secret, string challenge)
    {
        return CreateProof(secret, challenge, ShutdownPurpose);
    }

    internal static string ComputeProxyKey(string? proxy)
    {
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    }

    private static string GetSecretPath()
    {
        return Path.Combine(new AccountStore().RootPath, ".cache", SecretFileName);
    }

    private static bool TryReadSecret(string path, out string secret)
    {
        secret = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var value = File.ReadAllText(path).Trim();
            if (value.Length < 32 || value.Any(char.IsWhiteSpace))
            {
                return false;
            }

            secret = value;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
