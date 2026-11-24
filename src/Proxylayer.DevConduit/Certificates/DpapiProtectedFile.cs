using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// Reads and writes files whose content is encrypted with DPAPI. With machine scope, the
/// default, any account on this machine that can read the file can decrypt it, no account on
/// another machine can, and a password reset or a different service account changes nothing,
/// so the folder's access list is what keeps the content private. With user scope only the
/// account that wrote it can decrypt it. Writes go to a temporary file beside the target,
/// which is then moved over it, with a short retry while another process has it open.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiProtectedFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dev.Conduit certificate");

    public static byte[] Read(string path, DataProtectionScope scope = DataProtectionScope.LocalMachine) =>
        ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, scope);

    public static void Write(string path, byte[] content, DataProtectionScope scope = DataProtectionScope.LocalMachine) =>
        AtomicFile.Write(path, ProtectedData.Protect(content, Entropy, scope));
}
