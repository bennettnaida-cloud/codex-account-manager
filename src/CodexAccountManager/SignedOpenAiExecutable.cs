using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace CodexAccountManager;

// Observation-only trust check; this is NOT a process-termination whitelist.
internal static class SignedOpenAiExecutable
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsTrusted(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            return Cache.GetOrAdd(key, _ => Verify(file.FullName));
        }
        catch { return false; }
    }

    private static bool Verify(string path)
    {
        var info = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
                File = pointer, ProviderFlags = 0x1000 // Cache-only, no network/revocation fetch during startup.
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
#pragma warning disable SYSLIB0057 // Authenticode extraction has no X509CertificateLoader PE equivalent.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return signer.GetNameInfo(X509NameType.SimpleName, false)
                .Equals("OpenAI OpCo, LLC", StringComparison.Ordinal);
        }
        catch { return false; }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
