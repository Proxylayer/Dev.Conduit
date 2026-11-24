using System.Runtime.Versioning;

namespace Proxylayer.DevConduit.Updates;

/// <summary>A package ready to install, or why there is none.</summary>
/// <param name="Path">The verified package in the update folder; null when the download or a check failed.</param>
/// <param name="Release">The release it came from.</param>
/// <param name="Message">What happened, for the log and the About page.</param>
public sealed record DownloadedUpdate(string? Path, UpdateRelease Release, string Message)
{
    public bool IsReady => Path is not null;
}

/// <summary>
/// Downloads a release's package to the update folder under a temporary name, verifies it with
/// <see cref="UpdatePackageVerifier"/>, and only then moves it to <c>Dev.Conduit-X.Y.Z.msi</c>;
/// a package that fails a check is deleted and logged. Older packages are removed, so only the
/// latest is kept. A package already downloaded is verified again rather than fetched again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateDownloader
{
    private readonly GitHubReleaseFeed _feed;
    private readonly UpdateFolder _folder;
    private readonly UpdatePackageVerifier _verifier;
    private readonly ILogger _logger;

    public UpdateDownloader(GitHubReleaseFeed feed, UpdateFolder folder, UpdatePackageVerifier verifier, ILogger logger)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public UpdateFolder Folder => _folder;

    public async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, Version installed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(installed);

        byte[] digest;
        try
        {
            _folder.Ensure();
            var text = await _feed.DownloadTextAsync(release.ChecksumUrl, ChecksumFile.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (!ChecksumFile.TryParse(text, release.MsiName, out var parsed))
            {
                return Refuse(release, $"{release.ChecksumName} is not a SHA-256 checksum of {release.MsiName}.");
            }

            digest = parsed;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsTransient(exception))
        {
            return Fail(release, $"The checksum of {release.Tag} could not be downloaded: {exception.Message}");
        }

        var final = _folder.PackagePath(release.Version);
        if (File.Exists(final))
        {
            var existing = Verify(final, digest, installed, release);
            if (existing.IsReady)
            {
                return existing;
            }
        }

        var partial = _folder.PartialPath(release.Version);
        try
        {
            _logger.LogInformation("Downloading Dev.Conduit {Tag} ({Size:N1} MB).", release.Tag, release.MsiSize / (1024.0 * 1024.0));
            await _feed.DownloadFileAsync(release.MsiUrl, partial, GitHubReleaseFeed.MaxMsiBytes, cancellationToken).ConfigureAwait(false);

            var verified = Verify(partial, digest, installed, release);
            if (!verified.IsReady)
            {
                return verified;
            }

            File.Move(partial, final, overwrite: true);
            _folder.Prune(keep: final);
            return verified with { Path = final };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsTransient(exception))
        {
            return Fail(release, $"{release.MsiName} could not be downloaded: {exception.Message}");
        }
        finally
        {
            UpdateFolder.TryDelete(partial);
        }
    }

    private DownloadedUpdate Verify(string path, byte[] digest, Version installed, UpdateRelease release)
    {
        PackageVerification verification;
        try
        {
            verification = _verifier.Verify(path, digest, installed, release.Version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateFolder.TryDelete(path);
            return Fail(release, $"{release.MsiName} could not be read to verify it: {exception.Message}");
        }

        if (!verification.Accepted)
        {
            UpdateFolder.TryDelete(path);
            return Refuse(release, $"{release.MsiName} was refused: {verification.Reason}");
        }

        _logger.LogInformation("{Message}", verification.Reason);
        return new DownloadedUpdate(path, release, $"Dev.Conduit {release.Tag} is downloaded and verified.");
    }

    private DownloadedUpdate Refuse(UpdateRelease release, string message)
    {
        _logger.LogWarning("Update refused: {Message}", message);
        return new DownloadedUpdate(null, release, message);
    }

    private DownloadedUpdate Fail(UpdateRelease release, string message)
    {
        _logger.LogWarning("{Message}", message);
        return new DownloadedUpdate(null, release, message);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException;
}
