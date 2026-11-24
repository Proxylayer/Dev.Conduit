using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// Versions as releases, the exe and the MSI carry them, compared on major.minor.patch. A release
/// tag is <c>vX.Y.Z</c>; the exe's informational version is the build's <c>-p:Version</c>, with any
/// <c>+metadata</c> suffix ignored; the MSI's ProductVersion is the exe's four-part file version.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Parses <c>X.Y.Z</c>, optionally with a leading <c>v</c>, a fourth part, a <c>-prerelease</c>
    /// label or a <c>+metadata</c> suffix, into major.minor.patch. The fourth part is dropped.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        var cut = value.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        var parts = value.Split('.');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0
                || !parts[index].All(char.IsAsciiDigit)
                || !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>True when the text carries a <c>-prerelease</c> label, as in <c>v1.2.0-beta.1</c>.</summary>
    public static bool IsPrereleaseLabel(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return value.Contains('-');
    }

    /// <summary>Major.minor.patch of <paramref name="version"/>, a missing patch counting as 0.</summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="current"/> on major.minor.patch.</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary><c>X.Y.Z</c>.</summary>
    public static string Format(Version version)
    {
        var normalized = Normalize(version);
        return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }

    /// <summary>This build's informational version without any <c>+metadata</c> suffix, for display.</summary>
    public static string CurrentDisplay { get; } = Display(typeof(ReleaseVersion).Assembly);

    /// <summary>This build's version, major.minor.patch.</summary>
    public static Version Current { get; } = Of(typeof(ReleaseVersion).Assembly);

    /// <summary>An assembly's version from its informational version, else its assembly version.</summary>
    public static Version Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return TryParse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var version)
            ? version
            : Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    private static string Display(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return Format(Of(assembly));
        }

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
