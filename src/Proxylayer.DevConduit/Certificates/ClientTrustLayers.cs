using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Proxylayer.DevConduit.Certificates;

/// <summary>
/// A server's client trust as one user's relay sees it: the machine layer an import wrote
/// (<see cref="ClientCertificateDirectory"/>) overlaid with that user's own layer under
/// <c>%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client</c>, where the unelevated sync stores what it learns.
/// The effective pins are both layers' pins together. The effective identity is the user's
/// renewed one while it is valid and of the same enrollment as the machine's, else the
/// machine's. Without a machine identity there is none: the user layer alone never
/// authenticates. An elevated caller builds this without a user layer (<see cref="MachineOnly"/>),
/// so nothing a user wrote reaches what it does.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientTrustLayers
{
    public ClientTrustLayers(ClientCertificateDirectory machine, string? userRoot)
    {
        Machine = machine ?? throw new ArgumentNullException(nameof(machine));
        if (userRoot is not null && !Path.IsPathFullyQualified(userRoot))
        {
            throw new ArgumentException($"'{userRoot}' is not a fully qualified path.", nameof(userRoot));
        }

        UserRoot = userRoot is null ? null : Path.GetFullPath(userRoot);
    }

    public ClientCertificateDirectory Machine { get; }

    /// <summary>The folder holding this user's server folders, or null for the machine layer alone.</summary>
    public string? UserRoot { get; }

    /// <summary>The machine layer and the current user's layer on this computer.</summary>
    public static ClientTrustLayers ForThisUser() =>
        new(
            ClientCertificateDirectory.ForThisComputer(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                "Proxylayer",
                "Dev.Conduit",
                ClientCertificateDirectory.DirectoryName));

    /// <summary>The machine layer alone, for an elevated caller that must not read what a user wrote.</summary>
    public static ClientTrustLayers MachineOnly(ClientCertificateDirectory machine) => new(machine, null);

    public ClientTrustFolder MachineFolder(string hostname) => Machine.Folder(hostname);

    /// <summary>The user's folder for a server, or null without a user layer.</summary>
    public ClientTrustFolder? UserFolder(string hostname) =>
        UserRoot is null ? null : new ClientTrustFolder(UserRoot, hostname, ClientTrustLayer.User);

    /// <summary>True when the server was imported with a client identity, which the effective identity needs.</summary>
    public bool HasIdentity(string hostname) => MachineFolder(hostname).HasIdentity;

    /// <summary>The effective pins: <see cref="PinnedTrust.Union"/> of both layers.</summary>
    public IReadOnlyList<PublishedServerCertificate> Pins(string hostname) =>
        PinnedTrust.Union(MachineFolder(hostname).ReadTrust(), UserFolder(hostname)?.ReadTrust() ?? []);

    /// <summary>
    /// True when the machine identity is inside its renewal window and not yet expired
    /// (<see cref="ClientCertificateRenewal.IsRenewable"/>), so <c>service-admin trust</c> would
    /// renew it. A user's own renewal does not count: other users and <c>service-admin trust</c>
    /// authenticate with the machine's. False without a machine identity or when it cannot be read.
    /// </summary>
    public bool MachineIdentityRenewalDue(string hostname, DateTimeOffset now)
    {
        try
        {
            using var identity = MachineFolder(hostname).ReadIdentity();
            return identity is not null && ClientCertificateRenewal.IsRenewable(identity.NotAfter.ToUniversalTime(), now);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The effective identity, by <see cref="Choose"/>. The caller disposes it.</summary>
    public X509Certificate2? Identity(string hostname, DateTimeOffset now)
    {
        var machine = MachineFolder(hostname).ReadIdentity();
        if (machine is null)
        {
            return null;
        }

        X509Certificate2? user;
        try
        {
            user = UserFolder(hostname)?.ReadIdentity();
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // A user identity that cannot be read is no identity; the machine's stands.
            user = null;
        }

        return Choose(machine, user, now);
    }

    /// <summary>
    /// The user's identity when it has its key, is inside its validity period and belongs to
    /// the machine identity's enrollment; otherwise the machine's. After an import of a new
    /// <c>.conduit</c> the user's renewal of the old enrollment is no longer chosen. The one
    /// not returned is disposed.
    /// </summary>
    public static X509Certificate2 Choose(X509Certificate2 machine, X509Certificate2? user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (user is null)
        {
            return machine;
        }

        var enrollment = ClientEnrollmentId.Of(machine.SubjectName);
        var usable = user.HasPrivateKey
            && now >= user.NotBefore.ToUniversalTime()
            && now < user.NotAfter.ToUniversalTime()
            && enrollment is not null
            && ClientEnrollmentId.Of(user.SubjectName) == enrollment;

        if (usable)
        {
            machine.Dispose();
            return user;
        }

        user.Dispose();
        return machine;
    }

    /// <summary>Deletes this user's folder for a server, if there is one. Returns a line for the person who asked, or null without a user layer.</summary>
    public string? ForgetUser(string hostname) => UserFolder(hostname)?.Delete();
}
