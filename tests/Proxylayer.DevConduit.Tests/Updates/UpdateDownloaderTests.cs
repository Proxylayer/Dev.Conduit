using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>
/// Downloading a release into an (unrestricted, throwaway) update folder: written under a
/// temporary name, verified, then moved; a refused package is deleted; only the latest is kept.
/// The feed is a fake handler and the package checks read fake properties.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateDownloaderTests
{
    private static readonly byte[] PackageBytes = "the 1.2.0 package"u8.ToArray();

    private static UpdateRelease Release() => new(
        new Version(1, 2, 0),
        "v1.2.0",
        new Uri($"{ReleaseJson.Download}/v1.2.0/Dev.Conduit-1.2.0.msi"),
        PackageBytes.Length,
        new Uri($"{ReleaseJson.Download}/v1.2.0/Dev.Conduit-1.2.0.msi.sha256"));

    private static RecordingHandler Assets(string checksum, byte[]? package = null) => new((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        HttpContent content = path.EndsWith(".sha256", StringComparison.Ordinal)
            ? new StringContent(checksum)
            : new ByteArrayContent(package ?? PackageBytes);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    });

    private static string GoodChecksum => ChecksumFile.Format(SHA256.HashData(PackageBytes), "Dev.Conduit-1.2.0.msi");

    private static UpdateDownloader Downloader(TempDirectory root, RecordingHandler handler, string productVersion = "1.2.0.0")
    {
        var feed = new GitHubReleaseFeed(new GitHubRepository("Proxylayer", "Dev.Conduit"), false, new Version(1, 1, 0), handler,
            TimeProvider.System, NullLogger.Instance);
        var verifier = new UpdatePackageVerifier(
            _ => SignatureInfo.Unsigned,
            _ => new Dictionary<string, string>
            {
                ["UpgradeCode"] = $"{{{InstallerOwnership.UpgradeCode}}}",
                ["ProductName"] = "Dev.Conduit",
                ["ProductVersion"] = productVersion
            },
            SignatureInfo.Unsigned);
        return new UpdateDownloader(feed, new UpdateFolder(root.Path, restricted: false), verifier, NullLogger.Instance);
    }

    [Fact]
    public async Task Download_VerifiesThenKeepsThePackageUnderItsReleaseName()
    {
        using var root = new TempDirectory();
        var downloader = Downloader(root, Assets(GoodChecksum));

        var result = await downloader.DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.True(result.IsReady, result.Message);
        Assert.Equal(Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi"), result.Path);
        Assert.Equal(PackageBytes, File.ReadAllBytes(result.Path!));
        Assert.Equal(["Dev.Conduit-1.2.0.msi"], Directory.EnumerateFiles(root.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Download_RemovesOlderPackagesAndLeftoverPartials()
    {
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, "Dev.Conduit-1.1.0.msi"), "old");
        File.WriteAllText(Path.Combine(root.Path, "Dev.Conduit-1.1.5.msi.0123.partial"), "partial");
        File.WriteAllText(Path.Combine(root.Path, "install-1.1.0.log"), "a log is kept");

        var result = await Downloader(root, Assets(GoodChecksum)).DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.True(result.IsReady, result.Message);
        Assert.Equal(
            ["Dev.Conduit-1.2.0.msi", "install-1.1.0.log"],
            Directory.EnumerateFiles(root.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Download_AChecksumMismatchIsRefusedAndNothingIsKept()
    {
        using var root = new TempDirectory();
        var wrong = ChecksumFile.Format(SHA256.HashData("something else"u8.ToArray()), "Dev.Conduit-1.2.0.msi");

        var result = await Downloader(root, Assets(wrong)).DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("SHA-256", result.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(root.Path));
    }

    [Fact]
    public async Task Download_AChecksumFileForAnotherNameIsRefusedBeforeTheDownload()
    {
        using var root = new TempDirectory();
        var handler = Assets(ChecksumFile.Format(SHA256.HashData(PackageBytes), "Dev.Conduit-1.1.0.msi"));

        var result = await Downloader(root, handler).DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.EndsWith(".sha256", handler.Sent!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_APackageForAnotherVersionIsRefusedAndDeleted()
    {
        using var root = new TempDirectory();

        var result = await Downloader(root, Assets(GoodChecksum), productVersion: "1.3.0.0")
            .DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Empty(Directory.EnumerateFiles(root.Path));
    }

    [Fact]
    public async Task Download_AnAlreadyDownloadedPackageIsVerifiedNotFetchedAgain()
    {
        using var root = new TempDirectory();
        File.WriteAllBytes(Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi"), PackageBytes);
        var requests = new List<string>();
        var handler = new RecordingHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GoodChecksum) });
        });

        var result = await Downloader(root, handler).DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.True(result.IsReady, result.Message);
        Assert.Equal(["/Proxylayer/Dev.Conduit/releases/download/v1.2.0/Dev.Conduit-1.2.0.msi.sha256"], requests);
    }

    [Fact]
    public async Task Download_OfflineIsAFailureNotAnException()
    {
        using var root = new TempDirectory();
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("offline"));

        var result = await Downloader(root, handler).DownloadAsync(Release(), new Version(1, 1, 0), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("offline", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_NamesPackagesPartialsAndLogsByVersion()
    {
        var folder = new UpdateFolder(@"C:\ProgramData\Proxylayer\Dev.Conduit\updates", restricted: true);

        Assert.Equal(@"C:\ProgramData\Proxylayer\Dev.Conduit\updates\Dev.Conduit-1.2.0.msi", folder.PackagePath(new Version(1, 2, 0)));
        Assert.Equal(@"C:\ProgramData\Proxylayer\Dev.Conduit\updates\install-1.2.0.log", folder.LogPath(new Version(1, 2, 0, 0)));
        Assert.Matches(@"\\Dev\.Conduit-1\.2\.0\.msi\.[0-9a-f]{32}\.partial$", folder.PartialPath(new Version(1, 2, 0)));
        Assert.Throws<ArgumentException>(() => new UpdateFolder(@"relative\updates", restricted: false));
    }

    [Fact]
    public void Folder_TheRestrictedListAllowsOnlySystemAndAdministrators()
    {
        var security = UpdateFolder.BuildSecurity();

        Assert.True(security.AreAccessRulesProtected);
        Assert.True(UpdateFolder.IsRestricted(security));
        var accounts = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(rule => ((System.Security.Principal.SecurityIdentifier)rule.IdentityReference).Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], accounts);
    }
}
