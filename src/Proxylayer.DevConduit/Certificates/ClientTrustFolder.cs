using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>Which of the two layers of a server's client trust a folder holds.</summary>
public enum ClientTrustLayer
{
    /// <summary>
    /// <c>%ProgramData%\Proxylayer\Dev.Conduit\client\&lt;hostname&gt;\</c>: what an import or
    /// <c>service-admin trust</c> wrote, elevated or as SYSTEM. Administrators and SYSTEM have
    /// full control, Users read. The identity is protected with machine-scope DPAPI.
    /// </summary>
    Machine,

    /// <summary>
    /// <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client\&lt;hostname&gt;\</c>: what one user's unelevated sync
    /// learned. The user profile's own access list, checked to reach no other user. The
    /// identity is protected with user-scope DPAPI.
    /// </summary>
    User
}

/// <summary>
/// One server's client folder in one <see cref="ClientTrustLayer"/>: <c>trust.json</c>, the
/// server certificates the relay pins, and <c>identity.pfx</c>, a client certificate and key
/// that authenticate to the server's <c>/cert</c>, DPAPI protected for the layer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientTrustFolder
{
    public const string TrustFileName = "trust.json";
    public const string IdentityFileName = "identity.pfx";

    /// <summary>What Users may do in a machine-layer folder: read.</summary>
    private const FileSystemRights UsersRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    private readonly bool _restrict;

    /// <param name="clientRoot">The folder holding one folder per server.</param>
    /// <param name="hostname">The server.</param>
    /// <param name="layer">Which layer the folder belongs to.</param>
    /// <param name="restrict">
    /// For the machine layer, whether <see cref="Create"/> applies <see cref="BuildSecurity"/>.
    /// Only a folder that is not the real machine's, written by an unelevated test, does without.
    /// </param>
    public ClientTrustFolder(string clientRoot, string hostname, ClientTrustLayer layer = ClientTrustLayer.Machine, bool restrict = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRoot);
        var normalized = DnsHostname.Normalize(hostname);
        if (!DnsHostname.IsValid(normalized))
        {
            throw new ArgumentException($"'{hostname}' is not a DNS host name.", nameof(hostname));
        }

        Hostname = normalized;
        Layer = layer;
        Directory = Path.Combine(Path.GetFullPath(clientRoot), normalized);
        _restrict = restrict;
    }

    public string Hostname { get; }

    public ClientTrustLayer Layer { get; }

    public string Directory { get; }

    public string TrustPath => Path.Combine(Directory, TrustFileName);

    public string IdentityPath => Path.Combine(Directory, IdentityFileName);

    public bool HasIdentity => File.Exists(IdentityPath);

    private DataProtectionScope Scope => Layer == ClientTrustLayer.Machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;

    /// <summary>
    /// Creates the folder. In the machine layer it gets <see cref="BuildSecurity"/>, also when
    /// it exists already, which needs administrator rights. In the user layer it keeps the
    /// profile's access list unless that reaches another account, which <see cref="IsPrivateTo"/>
    /// checks; then it is replaced by one for this user, SYSTEM and Administrators alone.
    /// </summary>
    public void Create()
    {
        var directory = new DirectoryInfo(Directory);
        if (Layer == ClientTrustLayer.User)
        {
            directory.Create();
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            if (!IsPrivateTo(directory.GetAccessControl(AccessControlSections.Access), user))
            {
                directory.SetAccessControl(BuildPrivateSecurity(user));
            }

            return;
        }

        if (!_restrict)
        {
            directory.Create();
            return;
        }

        if (!directory.Exists)
        {
            directory.Parent?.Create();
            directory.Create(BuildSecurity());
            return;
        }

        directory.SetAccessControl(BuildSecurity());
    }

    /// <summary>
    /// For an existing machine-layer folder whose access list is broader than
    /// <see cref="BuildSecurity"/> (<see cref="IsRestricted"/>), such as one an earlier build
    /// left with Users allowed to modify it, applies <see cref="BuildSecurity"/>, which needs
    /// administrator rights. True when it did. A user-layer folder, a missing one, or one not
    /// restricted by design (an unelevated test's) is left alone.
    /// </summary>
    public bool Tighten()
    {
        if (Layer != ClientTrustLayer.Machine || !_restrict)
        {
            return false;
        }

        var directory = new DirectoryInfo(Directory);
        if (!directory.Exists || IsRestricted(directory.GetAccessControl(AccessControlSections.Access)))
        {
            return false;
        }

        directory.SetAccessControl(BuildSecurity());
        return true;
    }
    public IReadOnlyList<PublishedServerCertificate> ReadTrust()
    {
        try
        {
            if (!File.Exists(TrustPath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<ClientTrustDocument>(File.ReadAllText(TrustPath), StorageJson.Options);
            return document is not null && string.Equals(DnsHostname.Normalize(document.Hostname), Hostname, StringComparison.Ordinal)
                ? document.Certificates ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void WriteTrust(IReadOnlyList<PublishedServerCertificate> pins) =>
        AtomicFile.Write(TrustPath, JsonSerializer.SerializeToUtf8Bytes(new ClientTrustDocument(Hostname, pins), StorageJson.Options));

    /// <summary>The client certificate with its key, or null when there is none.</summary>
    public X509Certificate2? ReadIdentity() =>
        File.Exists(IdentityPath) ? CertificateFactory.LoadPkcs12(DpapiProtectedFile.Read(IdentityPath, Scope)) : null;

    /// <summary>Stores the client certificate and key, given as PKCS#12 with no password.</summary>
    public void WriteIdentity(byte[] pkcs12) => DpapiProtectedFile.Write(IdentityPath, pkcs12, Scope);

    /// <summary>
    /// Deletes the files in the folder and then the folder. Nothing is followed: a reparse
    /// point is removed as a link, never entered, and a subfolder, which the app never writes,
    /// is left. Returns a line for the person who asked.
    /// </summary>
    public string Delete()
    {
        var directory = new DirectoryInfo(Directory);
        if (!directory.Exists)
        {
            return $"No client folder at {Directory}.";
        }

        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete(recursive: false);
            return $"Removed the link {Directory}.";
        }

        // Deleting a file that is a link deletes the link, not what it points at.
        foreach (var file in directory.EnumerateFiles())
        {
            file.Delete();
        }

        if (directory.EnumerateFileSystemInfos().Any())
        {
            return $"Removed the client certificate and pinned certificates; {Directory} holds folders this app did not write and was left.";
        }

        directory.Delete(recursive: false);
        return $"Removed the client certificate and pinned certificates in {Directory}.";
    }

    /// <summary>The host names that have a client folder under <paramref name="clientRoot"/>.</summary>
    public static IReadOnlyList<string> Hostnames(string clientRoot)
    {
        try
        {
            return System.IO.Directory.Exists(clientRoot)
                ? System.IO.Directory.GetDirectories(clientRoot)
                    .Select(Path.GetFileName)
                    .Where(name => DnsHostname.IsValid(name))
                    .Select(name => name!)
                    .ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The machine layer: Administrators and SYSTEM full control, Users read, applied to the
    /// folder and its files, not inherited. Users read the identity by design: the unelevated
    /// sync authenticates with it.
    /// </summary>
    public static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(Inherited(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
        security.AddAccessRule(Inherited(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));
        security.AddAccessRule(Inherited(WellKnownSidType.BuiltinUsersSid, UsersRights));
        return security;
    }

    /// <summary>
    /// True when the access list is no broader than <see cref="BuildSecurity"/>: it does not
    /// inherit from the parent, and it allows nobody but Administrators and SYSTEM anything
    /// beyond Users' read. Deny entries narrow access, so they are accepted as they are.
    /// </summary>
    public static bool IsRestricted(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var full = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null)
        };
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny
                || full.Contains((SecurityIdentifier)rule.IdentityReference)
                || (users.Equals(rule.IdentityReference) && (rule.FileSystemRights & ~UsersRights) == 0));
    }
    /// <summary>
    /// True when nobody but <paramref name="user"/>, SYSTEM and Administrators is allowed to
    /// read the folder. CREATOR OWNER and OWNER RIGHTS entries stand for the owner, who is the
    /// user in a profile, and are allowed.
    /// </summary>
    public static bool IsPrivateTo(DirectorySecurity security, SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(user);

        var allowed = new HashSet<SecurityIdentifier>
        {
            user,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new("S-1-3-4")
        };

        const FileSystemRights Reading = FileSystemRights.ReadData | FileSystemRights.ReadAttributes
            | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions;

        return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Reading) != 0)
            .All(rule => allowed.Contains((SecurityIdentifier)rule.IdentityReference));
    }

    /// <summary>The user layer's access list when the profile's own reaches someone else: the user, SYSTEM and Administrators, full control, not inherited.</summary>
    public static DirectorySecurity BuildPrivateSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(Inherited(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));
        security.AddAccessRule(Inherited(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
        return security;
    }

    private static FileSystemAccessRule Inherited(WellKnownSidType sid, FileSystemRights rights) =>
        new(
            new SecurityIdentifier(sid, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
}
