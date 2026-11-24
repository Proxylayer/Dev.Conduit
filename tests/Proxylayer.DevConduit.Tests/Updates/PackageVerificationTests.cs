using System.Runtime.Versioning;
using System.Security.Cryptography;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>
/// The checks a package passes before anything installs it: the checksum file, the signature
/// policy, the MSI's properties (pure, and read from a real MSI when the build has made one),
/// and the three together.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageVerificationTests
{
    private const string Digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly SignatureInfo SignedByUs = new(true, true, "CN=Proxylayer", "trusted");
    private static readonly SignatureInfo SignedByOthers = new(true, true, "CN=Someone Else", "trusted");
    private static readonly SignatureInfo SignedUntrusted = new(true, false, "CN=Proxylayer", "0x800B0109");

    private static Dictionary<string, string> Properties(string? upgradeCode = null, string name = "Dev.Conduit", string version = "1.2.0.0") => new()
    {
        ["UpgradeCode"] = upgradeCode ?? $"{{{InstallerOwnership.UpgradeCode}}}",
        ["ProductName"] = name,
        ["ProductVersion"] = version
    };

    // ---- Checksum file -------------------------------------------------------------------

    [Theory]
    [InlineData(Digest)]
    [InlineData(Digest + "\n")]
    [InlineData(Digest + "  Dev.Conduit-1.2.0.msi")]
    [InlineData(Digest + " *Dev.Conduit-1.2.0.msi\r\n")]
    [InlineData(Digest + "\tdev.conduit-1.2.0.MSI")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("﻿" + Digest)]
    public void ChecksumFile_ReadsADigestOptionallyFollowedByTheMsiName(string text)
    {
        Assert.True(ChecksumFile.TryParse(text, "Dev.Conduit-1.2.0.msi", out var digest));
        Assert.Equal(Convert.FromHexString(Digest), digest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9f86d081")]
    [InlineData(Digest + "00")]
    [InlineData("zz86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData(Digest + "  Dev.Conduit-1.1.0.msi")]
    [InlineData(Digest + "  other.msi")]
    public void ChecksumFile_RefusesAnythingElse(string text) =>
        Assert.False(ChecksumFile.TryParse(text, "Dev.Conduit-1.2.0.msi", out _));

    [Fact]
    public void ChecksumFile_FormatIsWhatItReads()
    {
        var line = ChecksumFile.Format(Convert.FromHexString(Digest), "Dev.Conduit-1.2.0.msi");

        Assert.Equal($"{Digest}  Dev.Conduit-1.2.0.msi", line);
        Assert.True(ChecksumFile.TryParse(line, "Dev.Conduit-1.2.0.msi", out _));
    }

    // ---- Signature policy: the signed/unsigned matrix ------------------------------------

    public static TheoryData<SignatureInfo, SignatureInfo, bool> SignatureMatrix => new()
    {
        { SignatureInfo.Unsigned, SignatureInfo.Unsigned, true },
        { SignatureInfo.Unsigned, SignedByUs, true },
        { SignatureInfo.Unsigned, SignedByOthers, true },
        { SignedByUs, SignedByUs, true },
        { SignedByUs, SignatureInfo.Unsigned, false },
        { SignedByUs, SignedByOthers, false },
        { SignedByUs, SignedUntrusted, false },
        { new SignatureInfo(true, true, null, "trusted"), SignedByUs, false }
    };

    [Theory]
    [MemberData(nameof(SignatureMatrix))]
    public void SignaturePolicy_RequiresTheSameTrustedSignerOnlyWhenTheRunningExeIsSigned(SignatureInfo running, SignatureInfo candidate, bool accepted) =>
        Assert.Equal(accepted, SignaturePolicy.Decide(running, candidate).Accepted);

    [Fact]
    public void AuthenticodeSignature_AnUnsignedFileIsUnsigned()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "plain.bin");
        File.WriteAllBytes(path, new byte[64]);

        var signature = AuthenticodeSignature.Read(path);

        Assert.False(signature.IsSigned);
        Assert.False(signature.IsTrusted);
    }

    // ---- MSI properties ------------------------------------------------------------------

    [Fact]
    public void MsiPackageCheck_AcceptsDevConduitNewerThanInstalled()
    {
        var check = MsiPackageCheck.Evaluate(Properties(), new Version(1, 1, 0), expected: new Version(1, 2, 0));

        Assert.True(check.Accepted, check.Reason);
        Assert.Equal(new Version(1, 2, 0), check.Version);
    }

    [Theory]
    [InlineData("C76F2433-85EB-4A36-8EE6-C8684EE02F80", "Dev.Conduit", "1.2.0.0", "UpgradeCode")]
    [InlineData(null, "POC.DevProxy", "1.2.0.0", "ProductName")]
    [InlineData(null, "dev.conduit", "1.2.0.0", "ProductName")]
    [InlineData(null, "Dev.Conduit", "1.1.0.0", "not newer")]
    [InlineData(null, "Dev.Conduit", "1.0.0.0", "not newer")]
    [InlineData(null, "Dev.Conduit", "abc", "not a version")]
    public void MsiPackageCheck_RefusesAnotherProductOrNoNewerVersion(string? upgradeCode, string name, string version, string reason)
    {
        var check = MsiPackageCheck.Evaluate(Properties(upgradeCode, name, version), new Version(1, 1, 0), expected: null);

        Assert.False(check.Accepted);
        Assert.Contains(reason, check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MsiPackageCheck_RefusesAVersionOtherThanTheReleases()
    {
        var check = MsiPackageCheck.Evaluate(Properties(version: "1.3.0.0"), new Version(1, 1, 0), expected: new Version(1, 2, 0));

        Assert.False(check.Accepted);
        Assert.Contains("as the release says", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MsiPackageCheck_RefusesAFileThatIsNotAPackage() =>
        Assert.False(MsiPackageCheck.Evaluate(null, new Version(1, 0, 0), null).Accepted);

    [Fact]
    public void MsiPackageProperties_AFileThatIsNotAnMsiReadsAsNull()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "fake.msi");
        File.WriteAllText(path, "not a package");

        Assert.Null(MsiPackageProperties.Read(path));
    }

    /// <summary>The build's own MSI, read read-only; skipped when no build has produced one.</summary>
    [Fact]
    public void MsiPackageProperties_TheBuiltMsiIsDevConduitWithItsUpgradeCode()
    {
        var msi = BuiltMsi();
        Assert.SkipWhen(msi is null, "No MSI has been built (run build.ps1 -Configuration Release).");

        var properties = MsiPackageProperties.Read(msi!);

        Assert.NotNull(properties);
        Assert.Equal($"{{{InstallerOwnership.UpgradeCode}}}", properties["UpgradeCode"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Dev.Conduit", properties["ProductName"]);
        Assert.True(ReleaseVersion.TryParse(properties["ProductVersion"], out var version));
        Assert.True(MsiPackageCheck.Evaluate(properties, new Version(0, 0, 1), expected: version).Accepted);
        Assert.False(MsiPackageCheck.Evaluate(properties, version, expected: null).Accepted);
    }

    // ---- The three together --------------------------------------------------------------

    [Fact]
    public void Verify_AcceptsAMatchingPackage()
    {
        using var root = new TempDirectory();
        var (path, digest) = Package(root);
        var verifier = new UpdatePackageVerifier(_ => SignatureInfo.Unsigned, _ => Properties(), SignatureInfo.Unsigned);

        var result = verifier.Verify(path, digest, new Version(1, 1, 0), new Version(1, 2, 0));

        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(new Version(1, 2, 0), result.Version);
    }

    [Fact]
    public void Verify_RefusesAChecksumMismatchBeforeLookingFurther()
    {
        using var root = new TempDirectory();
        var (path, digest) = Package(root);
        digest[0] ^= 0xFF;
        var looked = false;
        var verifier = new UpdatePackageVerifier(_ => { looked = true; return SignatureInfo.Unsigned; }, _ => Properties(), SignatureInfo.Unsigned);

        var result = verifier.Verify(path, digest, new Version(1, 1, 0), null);

        Assert.False(result.Accepted);
        Assert.Contains("SHA-256", result.Reason, StringComparison.Ordinal);
        Assert.False(looked);
    }

    [Fact]
    public void Verify_RefusesAnUnsignedPackageWhenTheRunningExeIsSigned()
    {
        using var root = new TempDirectory();
        var (path, digest) = Package(root);
        var verifier = new UpdatePackageVerifier(_ => SignatureInfo.Unsigned, _ => Properties(), SignedByUs);

        Assert.False(verifier.Verify(path, digest, new Version(1, 1, 0), null).Accepted);
    }

    [Fact]
    public void Verify_RefusesAPackageThatIsNotNewer()
    {
        using var root = new TempDirectory();
        var (path, digest) = Package(root);
        var verifier = new UpdatePackageVerifier(_ => SignedByUs, _ => Properties(version: "1.1.0.0"), SignedByUs);

        Assert.False(verifier.Verify(path, digest, new Version(1, 1, 0), null).Accepted);
    }

    private static (string Path, byte[] Digest) Package(TempDirectory root)
    {
        var path = Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi");
        File.WriteAllBytes(path, "package bytes"u8.ToArray());
        return (path, SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static string? BuiltMsi()
    {
        var repository = InstallerSource.RepositoryRoot();
        var artifacts = Path.Combine(repository, "artifacts");
        var fromArtifacts = Directory.Exists(artifacts)
            ? Directory.EnumerateFiles(artifacts, "Dev.Conduit-*.msi").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var fromInstaller = Path.Combine(repository, "installer", "bin", "Release", "Dev.Conduit.msi");
        return fromArtifacts ?? (File.Exists(fromInstaller) ? fromInstaller : null);
    }
}
