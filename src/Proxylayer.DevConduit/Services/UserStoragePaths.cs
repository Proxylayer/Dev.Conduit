using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Builds the on-disk paths for a user's data, rejecting any identifier that
/// could escape the data root.
/// </summary>
public sealed partial class UserStoragePaths
{
    private readonly string _dataRoot;

    public UserStoragePaths(IOptions<DevConduitOptions> options)
    {
        _dataRoot = options.Value.ResolveDataRoot();
    }

    public string DataRoot => _dataRoot;

    public string UserDirectory(string userId) =>
        Path.Combine(_dataRoot, "users", Validate(userId, nameof(userId)));

    public string HeaderSetsFile(string userId) =>
        Path.Combine(UserDirectory(userId), "headers.json");

    public string HistoryDirectory(string userId) =>
        Path.Combine(UserDirectory(userId), "history");

    /// <summary>Traffic forwarded through /route, kept apart from console history.</summary>
    public string RoutedDirectory(string userId) =>
        Path.Combine(UserDirectory(userId), "routed");

    /// <summary>True when the value is usable as a single path segment.</summary>
    public static bool IsSafeSegment(string? value) =>
        !string.IsNullOrEmpty(value)
        && value is not ("." or "..")
        && SafeSegment().IsMatch(value)
        && !ReservedDeviceName().IsMatch(value);

    private static string Validate(string value, string parameterName)
    {
        if (!IsSafeSegment(value))
        {
            throw new ArgumentException("Value is not usable as a storage path segment.", parameterName);
        }

        return value;
    }

    // \A and \z rather than ^ and $: $ also matches immediately before a trailing line
    // feed, which would let "abc" and "abc\n" both pass and resolve to different directories.
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();

    // Reserved on Windows however they are spelled, and reserved with any extension too:
    // opening "CON.txt" opens the console device, not a file.
    [GeneratedRegex(
        @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDeviceName();
}
