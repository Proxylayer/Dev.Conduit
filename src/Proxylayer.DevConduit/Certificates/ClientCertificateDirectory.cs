using System.Runtime.Versioning;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// The machine layer of what <c>.conduit</c> imports bring: <c>%ProgramData%\Proxylayer\Dev.Conduit\client</c>,
/// one <see cref="ClientTrustFolder"/> per server, and beside them the records only an
/// administrator writes: which certificates the app added to Windows trust, and which routes
/// each import added. The root inherits the access list of <c>%ProgramData%\Proxylayer\Dev.Conduit</c>;
/// each server's folder gets its own (<see cref="ClientTrustFolder.BuildSecurity"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientCertificateDirectory
{
    public const string DirectoryName = "client";
    public const string RootStoreRecordFileName = "root-store.json";
    public const string ImportRecordFileName = "imports.json";

    /// <param name="root">The folder holding the server folders.</param>
    /// <param name="restrictFolders">
    /// Whether a server folder gets the machine layer's access list when created. False only
    /// for a throwaway directory an unelevated process must keep writing.
    /// </param>
    public ClientCertificateDirectory(string root, bool restrictFolders = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException($"'{root}' is not a fully qualified path.", nameof(root));
        }

        Root = Path.GetFullPath(root);
        RestrictFolders = restrictFolders;
    }

    public string Root { get; }

    public bool RestrictFolders { get; }

    public string RootStoreRecordPath => Path.Combine(Root, RootStoreRecordFileName);

    public string ImportRecordPath => Path.Combine(Root, ImportRecordFileName);

    /// <summary>The fixed location on this computer, beside the machine settings file.</summary>
    public static ClientCertificateDirectory ForThisComputer() =>
        new(Path.Combine(Path.GetDirectoryName(SettingsFiles.ForThisComputer().MachinePath)!, DirectoryName));

    public ClientTrustFolder Folder(string hostname) => new(Root, hostname, ClientTrustLayer.Machine, RestrictFolders);

    /// <summary>The servers this machine has imported.</summary>
    public IReadOnlyList<ClientTrustFolder> Folders() =>
        ClientTrustFolder.Hostnames(Root).Select(Folder).ToList();
}
