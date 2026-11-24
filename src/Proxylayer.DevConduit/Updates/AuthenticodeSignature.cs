using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Updates;

/// <summary>A file's Authenticode signature as Windows sees it.</summary>
/// <param name="IsSigned">True when the file carries a signature, valid or not.</param>
/// <param name="IsTrusted">True when WinVerifyTrust accepts the signature: intact, from a trusted chain, not revoked.</param>
/// <param name="Subject">The signing certificate's subject, when it could be read.</param>
/// <param name="Status">WinVerifyTrust's result, for the log.</param>
public sealed record SignatureInfo(bool IsSigned, bool IsTrusted, string? Subject, string Status)
{
    public static SignatureInfo Unsigned { get; } = new(false, false, null, "not signed");
}

/// <summary>
/// Reads a file's Authenticode signature with WinVerifyTrust (generic verify, no UI, revocation
/// checked for the whole chain except the root) and the signer's subject. Used on the running
/// exe and on a downloaded MSI.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AuthenticodeSignature
{
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x80;

    private const int TrustNoSignature = unchecked((int)0x800B0100);
    private const int TrustSubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustProviderUnknown = unchecked((int)0x800B0001);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        int result;
        try
        {
            result = Verify(path);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new SignatureInfo(false, false, null, "WinVerifyTrust is not available");
        }

        if (result is TrustNoSignature or TrustSubjectFormUnknown or TrustProviderUnknown)
        {
            return SignatureInfo.Unsigned;
        }

        return new SignatureInfo(true, result == 0, Subject(path), result == 0 ? "trusted" : $"0x{result:X8}");
    }

    private static string? Subject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // The loader has no Authenticode equivalent; this reads the signer of a signed file.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static int Verify(string path)
    {
        var file = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };

        var fileHandle = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, fileHandle, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeWholeChain,
                UnionChoice = ChoiceFile,
                File = fileHandle,
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckChainExcludeRoot
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.StateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileHandle);
            Marshal.FreeHGlobal(fileHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}

/// <summary>Whether a candidate's signature is acceptable, and why not when it is not.</summary>
public sealed record SignatureDecision(bool Accepted, string Reason);

/// <summary>
/// The signature rule for an update. When the running exe is signed, the package must carry a
/// signature Windows trusts from the same subject: an unsigned package is refused, which also
/// stops a downgrade from signed to unsigned builds. When the running exe is unsigned there is
/// nothing to compare with, and the checksum and package checks stand alone. Pure.
/// </summary>
public static class SignaturePolicy
{
    public static SignatureDecision Decide(SignatureInfo running, SignatureInfo candidate)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!running.IsSigned)
        {
            return new SignatureDecision(true, "The running build is unsigned, so the package's signature is not required.");
        }

        if (!candidate.IsSigned)
        {
            return new SignatureDecision(false, "The running build is signed but the package is not.");
        }

        if (!candidate.IsTrusted)
        {
            return new SignatureDecision(false, $"The package's signature is not trusted by Windows ({candidate.Status}).");
        }

        if (string.IsNullOrEmpty(running.Subject) || !string.Equals(running.Subject, candidate.Subject, StringComparison.Ordinal))
        {
            return new SignatureDecision(false,
                $"The package is signed by '{candidate.Subject}', not by '{running.Subject}' as the running build is.");
        }

        return new SignatureDecision(true, $"The package is signed by '{candidate.Subject}', as the running build is.");
    }
}
