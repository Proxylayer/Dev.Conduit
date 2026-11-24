using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Updates;

/// <summary>
/// Reads an MSI's Property table through the Windows Installer API, opening the package
/// read-only. Nothing is installed, and the package's custom actions never run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MsiPackageProperties
{
    private const int ErrorSuccess = 0;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorMoreData = 234;

    /// <summary>The Property table of the package at <paramref name="path"/>, or null when it is not a readable MSI.</summary>
    public static IReadOnlyDictionary<string, string>? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            // A null persist mode is MSIDBOPEN_READONLY.
            if (MsiOpenDatabase(path, IntPtr.Zero, out var database) != ErrorSuccess)
            {
                return null;
            }

            try
            {
                if (MsiDatabaseOpenView(database, "SELECT `Property`, `Value` FROM `Property`", out var view) != ErrorSuccess)
                {
                    return null;
                }

                try
                {
                    if (MsiViewExecute(view, IntPtr.Zero) != ErrorSuccess)
                    {
                        return null;
                    }

                    var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                    while (true)
                    {
                        var fetched = MsiViewFetch(view, out var record);
                        if (fetched == ErrorNoMoreItems)
                        {
                            return properties;
                        }

                        if (fetched != ErrorSuccess)
                        {
                            return null;
                        }

                        try
                        {
                            if (Field(record, 1) is { } name && Field(record, 2) is { } value)
                            {
                                properties[name] = value;
                            }
                        }
                        finally
                        {
                            MsiCloseHandle(record);
                        }
                    }
                }
                finally
                {
                    MsiCloseHandle(view);
                }
            }
            finally
            {
                MsiCloseHandle(database);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? Field(IntPtr record, int field)
    {
        var length = 256;
        var buffer = new StringBuilder(length);
        var result = MsiRecordGetString(record, field, buffer, ref length);
        if (result == ErrorMoreData)
        {
            length++;
            buffer = new StringBuilder(length);
            result = MsiRecordGetString(record, field, buffer, ref length);
        }

        return result == ErrorSuccess ? buffer.ToString() : null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW", ExactSpelling = true)]
    private static extern int MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW", ExactSpelling = true)]
    private static extern int MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern int MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern int MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW", ExactSpelling = true)]
    private static extern int MsiRecordGetString(IntPtr record, int field, StringBuilder value, ref int length);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern int MsiCloseHandle(IntPtr handle);
}

/// <summary>What the package check found.</summary>
/// <param name="Accepted">True when the package is this product, newer than the installed version.</param>
/// <param name="Version">The package's ProductVersion as major.minor.patch, when it parsed.</param>
/// <param name="Reason">Why it was refused; empty when accepted.</param>
public sealed record MsiPackageCheck(bool Accepted, Version? Version, string Reason)
{
    public const string ProductName = "Dev.Conduit";

    /// <summary>
    /// Checks a package's properties: its UpgradeCode must be <see cref="InstallerOwnership.UpgradeCode"/>,
    /// its ProductName <c>Dev.Conduit</c>, and its ProductVersion newer than <paramref name="installed"/>
    /// and, when given, equal to <paramref name="expected"/>, the release it was downloaded as. Pure.
    /// </summary>
    public static MsiPackageCheck Evaluate(IReadOnlyDictionary<string, string>? properties, Version installed, Version? expected)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (properties is null)
        {
            return new MsiPackageCheck(false, null, "The file is not a Windows Installer package that could be read.");
        }

        var upgradeCode = properties.GetValueOrDefault("UpgradeCode")?.Trim().Trim('{', '}');
        if (!string.Equals(upgradeCode, InstallerOwnership.UpgradeCode, StringComparison.OrdinalIgnoreCase))
        {
            return new MsiPackageCheck(false, null, $"The package's UpgradeCode, {upgradeCode ?? "(none)"}, is not Dev.Conduit's.");
        }

        var name = properties.GetValueOrDefault("ProductName");
        if (!string.Equals(name, ProductName, StringComparison.Ordinal))
        {
            return new MsiPackageCheck(false, null, $"The package's ProductName, {name ?? "(none)"}, is not {ProductName}.");
        }

        var productVersion = properties.GetValueOrDefault("ProductVersion");
        if (!ReleaseVersion.TryParse(productVersion, out var version) || ReleaseVersion.IsPrereleaseLabel(productVersion))
        {
            return new MsiPackageCheck(false, null, $"The package's ProductVersion, {productVersion ?? "(none)"}, is not a version.");
        }

        if (expected is not null && version != ReleaseVersion.Normalize(expected))
        {
            return new MsiPackageCheck(false, version,
                $"The package is version {ReleaseVersion.Format(version)}, not {ReleaseVersion.Format(expected)} as the release says.");
        }

        if (!ReleaseVersion.IsNewer(version, installed))
        {
            return new MsiPackageCheck(false, version,
                $"The package's version, {ReleaseVersion.Format(version)}, is not newer than the installed {ReleaseVersion.Format(installed)}.");
        }

        return new MsiPackageCheck(true, version, string.Empty);
    }
}
