using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Proxylayer.DevConduit.Updates;

/// <summary>What verifying a downloaded package found.</summary>
/// <param name="Accepted">True when every check passed.</param>
/// <param name="Version">The package's version, when its properties could be read.</param>
/// <param name="Reason">Why it was refused, or a summary of what passed.</param>
public sealed record PackageVerification(bool Accepted, Version? Version, string Reason);

/// <summary>
/// The checks every update package passes before anything installs it, in the tray, the service
/// and again in the elevated helper: its SHA-256 equals the release's checksum asset; its
/// Authenticode signature satisfies <see cref="SignaturePolicy"/> against the running exe's; and
/// its Property table, read read-only, names Dev.Conduit's UpgradeCode and ProductName and a
/// ProductVersion newer than the installed one (<see cref="MsiPackageCheck"/>).
/// </summary>
public sealed class UpdatePackageVerifier
{
    private readonly Func<string, SignatureInfo> _readSignature;
    private readonly Func<string, IReadOnlyDictionary<string, string>?> _readProperties;
    private readonly SignatureInfo _running;

    public UpdatePackageVerifier(
        Func<string, SignatureInfo> readSignature,
        Func<string, IReadOnlyDictionary<string, string>?> readProperties,
        SignatureInfo running)
    {
        _readSignature = readSignature ?? throw new ArgumentNullException(nameof(readSignature));
        _readProperties = readProperties ?? throw new ArgumentNullException(nameof(readProperties));
        _running = running ?? throw new ArgumentNullException(nameof(running));
    }

    /// <summary>The checks against this process's own exe and signature.</summary>
    [SupportedOSPlatform("windows")]
    public static UpdatePackageVerifier ForThisProcess() =>
        new(AuthenticodeSignature.Read, MsiPackageProperties.Read,
            Environment.ProcessPath is { } exe ? AuthenticodeSignature.Read(exe) : SignatureInfo.Unsigned);

    /// <summary>The running exe's signature, for the log.</summary>
    public SignatureInfo Running => _running;

    /// <param name="msiPath">The downloaded package.</param>
    /// <param name="expectedSha256">The digest from the release's checksum asset.</param>
    /// <param name="installed">The installed version, which the package must be newer than.</param>
    /// <param name="expectedVersion">The release's version, which the package must be; null to skip.</param>
    public PackageVerification Verify(string msiPath, byte[] expectedSha256, Version installed, Version? expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(msiPath);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(installed);

        byte[] actual;
        using (var stream = new FileStream(msiPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actual = SHA256.HashData(stream);
        }

        if (expectedSha256.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
        {
            return new PackageVerification(false, null,
                $"The package's SHA-256, {Convert.ToHexStringLower(actual)}, does not match the release's checksum {Convert.ToHexStringLower(expectedSha256)}.");
        }

        var signature = SignaturePolicy.Decide(_running, _readSignature(msiPath));
        if (!signature.Accepted)
        {
            return new PackageVerification(false, null, signature.Reason);
        }

        var package = MsiPackageCheck.Evaluate(_readProperties(msiPath), installed, expectedVersion);
        return package.Accepted
            ? new PackageVerification(true, package.Version,
                $"Dev.Conduit {ReleaseVersion.Format(package.Version!)} verified: checksum matches; {signature.Reason}")
            : new PackageVerification(false, package.Version, package.Reason);
    }
}
