using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Proxylayer.DevConduit.Hosting;

/// <summary>
/// Whether the Dev.Conduit MSI is installed on this machine, and the role, environment and install
/// folder it saved for its upgrades under <c>HKLM\SOFTWARE\Proxylayer\Dev.Conduit</c>. While it is installed, in
/// any role, the tray and the elevated helper leave installing the service to the MSI. Only its
/// Server role installs the service, so only then is removing it the MSI's too; a service on a
/// machine with the Workstation role came some other way and may be uninstalled here. An
/// environment saved from the tray is written to the MSI's saved choice too, so an upgrade or
/// repair keeps it. Reading needs no administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerOwnership
{
    /// <summary>
    /// The package's UpgradeCode. <c>installer/Package.wxs</c> holds the same GUID in its
    /// <c>Package</c> element; change both together.
    /// </summary>
    public const string UpgradeCode = "7F06EDB1-709D-4AA7-8416-2A77F1AB825C";

    /// <summary>The key, in the 64-bit registry view, where the MSI saves its choice.</summary>
    public const string SavedChoiceKey = @"SOFTWARE\Proxylayer\Dev.Conduit";

    public const string RoleValueName = "Role";

    public const string EnvironmentValueName = "Environment";

    /// <summary>The folder the MSI installed to, saved so an upgrade installs to it again and the updater can find it.</summary>
    public const string InstallFolderValueName = "InstallFolder";

    /// <summary>The MSI's role that installs the service.</summary>
    public const string ServerRole = "Server";

    /// <summary>The MSI's default role, the tray hosting local mode.</summary>
    public const string WorkstationRole = "Workstation";

    private const int ErrorSuccess = 0;

    /// <summary>A product code in braces, with its terminating null.</summary>
    private const int ProductCodeLength = 39;

    public InstallerOwnership(bool isInstalled, string? role, string? environment, string? installFolder = null)
    {
        IsInstalled = isInstalled;
        Role = isInstalled ? Blank(role) : null;
        Environment = isInstalled ? Blank(environment) : null;
        InstallFolder = isInstalled ? Blank(installFolder) : null;
    }

    /// <summary>True when a product with <see cref="UpgradeCode"/> is installed, so the MSI owns the service.</summary>
    public bool IsInstalled { get; }

    /// <summary>The role the MSI saved, Workstation or Server, or null when it is not installed or saved none.</summary>
    public string? Role { get; }

    /// <summary>The environment the MSI saved, or null when it is not installed or saved none.</summary>
    public string? Environment { get; }

    /// <summary>
    /// The install folder the MSI saved, or null when it is not installed or saved none (a version
    /// from before the value was added).
    /// </summary>
    public string? InstallFolder { get; }

    /// <summary>True when the MSI is installed in its Server role, the one that installs the service.</summary>
    public bool IsServerRole => IsInstalled && string.Equals(Role, ServerRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when installing the service is the MSI's: whenever it is installed, in any role.
    /// With the MSI present, a server comes from reinstalling it with <c>ROLE=Server</c>.
    /// </summary>
    public bool OwnsServiceInstall => IsInstalled;

    /// <summary>
    /// True when removing the service is the MSI's: only in its Server role, the one that
    /// installs the service. With the Workstation role any service present was installed some
    /// other way, so it may be uninstalled here. An installation with no saved role counts as
    /// Workstation, the MSI's default role.
    /// </summary>
    public bool OwnsServiceUninstall => IsServerRole;

    /// <summary>This machine's MSI installation, read from Windows Installer and the registry.</summary>
    public static InstallerOwnership Detect() => Detect(IsRelatedProductInstalled, ReadSavedValue);

    /// <summary>
    /// The installation as two lookups report it: whether a product with the given UpgradeCode
    /// is installed, and a value under <see cref="SavedChoiceKey"/> by name. The saved values
    /// are read only when the product is installed: a key left behind by anything else means
    /// nothing.
    /// </summary>
    public static InstallerOwnership Detect(Func<string, bool> isRelatedProductInstalled, Func<string, string?> readSavedValue)
    {
        ArgumentNullException.ThrowIfNull(isRelatedProductInstalled);
        ArgumentNullException.ThrowIfNull(readSavedValue);

        return isRelatedProductInstalled(UpgradeCode)
            ? new InstallerOwnership(true, readSavedValue(RoleValueName), readSavedValue(EnvironmentValueName), readSavedValue(InstallFolderValueName))
            : new InstallerOwnership(false, null, null);
    }

    /// <summary>
    /// What a <c>MsiEnumRelatedProducts</c> call for the first product means: installed only
    /// on ERROR_SUCCESS. ERROR_NO_MORE_ITEMS means none is, and any other error that Windows
    /// Installer could not tell, which leaves the tray and the helper working as without the MSI.
    /// </summary>
    public static bool IsInstalledResult(int errorCode) => errorCode == ErrorSuccess;

    /// <summary>
    /// Writes <paramref name="environmentName"/> over the environment the MSI saved, so its
    /// next upgrade or repair keeps it. Only an existing value is replaced: with no MSI
    /// installation, or no saved environment, nothing is written and the result is false.
    /// Needs an administrator.
    /// </summary>
    public bool UpdateSavedEnvironment(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (!IsInstalled)
        {
            return false;
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(SavedChoiceKey, writable: true);
        if (key?.GetValue(EnvironmentValueName) is null)
        {
            return false;
        }

        key.SetValue(EnvironmentValueName, environmentName, RegistryValueKind.String);
        return true;
    }

    private static bool IsRelatedProductInstalled(string upgradeCode)
    {
        var productCode = new char[ProductCodeLength];
        try
        {
            return IsInstalledResult(MsiEnumRelatedProducts($"{{{upgradeCode}}}", 0, 0, productCode));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string? ReadSavedValue(string name)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(SavedChoiceKey, writable: false);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW", ExactSpelling = true)]
    private static extern int MsiEnumRelatedProducts(
        string upgradeCode,
        int reserved,
        int productIndex,
        [Out] char[] productCode);
}
