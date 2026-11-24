using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// Whether the Dev.Conduit MSI owns the service, worked out from injected lookups. Nothing is
/// written: HKLM is never opened for writing, and only <see cref="Detect_OnThisMachine_FindsNoInstallation"/>
/// asks Windows Installer, read only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerOwnershipTests
{
    /// <summary>The UpgradeCode of POC.DevProxy, this product's previous name, which the package only detects.</summary>
    private const string PreviousProductUpgradeCode = "C76F2433-85EB-4A36-8EE6-C8684EE02F80";

    private static string Unbraced(string guid) => guid.Trim().Trim('{', '}');

    private static Func<string, string?> Saved(string? role, string? environment) => name => name switch
    {
        InstallerOwnership.RoleValueName => role,
        InstallerOwnership.EnvironmentValueName => environment,
        InstallerOwnership.InstallFolderValueName => null,
        _ => throw new InvalidOperationException($"Unexpected saved value {name}.")
    };

    // ---- UpgradeCode ---------------------------------------------------------------------

    [Fact]
    public void UpgradeCode_MatchesThePackageInTheWixSource()
    {
        var fromWxs = InstallerSource.Package().Attribute("UpgradeCode")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(fromWxs), "Package.wxs has no UpgradeCode on its Package element.");

        Assert.Equal(Unbraced(fromWxs), Unbraced(InstallerOwnership.UpgradeCode), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpgradeCode_IsABareGuid() =>
        Assert.True(Guid.TryParseExact(InstallerOwnership.UpgradeCode, "D", out _));

    [Fact]
    public void UpgradeCode_IsNotThePreviousProducts() =>
        Assert.NotEqual(Unbraced(PreviousProductUpgradeCode), Unbraced(InstallerOwnership.UpgradeCode), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ThePackage_OnlyDetectsThePreviousProductAndStopsTheInstallWhileItIsPresent()
    {
        var package = InstallerSource.Package();

        var upgrade = Assert.Single(package.Elements(InstallerSource.Wix + "Upgrade"));
        Assert.Equal(Unbraced(PreviousProductUpgradeCode), Unbraced((string?)upgrade.Attribute("Id") ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        var version = Assert.Single(upgrade.Elements(InstallerSource.Wix + "UpgradeVersion"));
        Assert.Equal("yes", (string?)version.Attribute("OnlyDetect"));
        var property = (string?)version.Attribute("Property");
        Assert.False(string.IsNullOrWhiteSpace(property));

        var launch = Assert.Single(
            package.Elements(InstallerSource.Wix + "Launch"),
            element => ((string?)element.Attribute("Condition") ?? string.Empty).Contains(property, StringComparison.Ordinal));
        Assert.Equal($"Installed OR NOT {property}", (string?)launch.Attribute("Condition"));
        Assert.Equal(
            "POC.DevProxy is installed. Uninstall it first (Settings > Apps), then install Dev.Conduit and import your .conduit file.",
            (string?)launch.Attribute("Message"));
    }

    // ---- Detect --------------------------------------------------------------------------

    [Fact]
    public void Detect_NotInstalled_HasNoRoleOrEnvironmentAndNeverReadsTheSavedValues()
    {
        var reads = 0;

        var ownership = InstallerOwnership.Detect(_ => false, name =>
        {
            reads++;
            return "Server";
        });

        Assert.False(ownership.IsInstalled);
        Assert.Null(ownership.Role);
        Assert.Null(ownership.Environment);
        Assert.False(ownership.IsServerRole);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Detect_Installed_TakesTheRoleEnvironmentAndInstallFolderFromTheSavedValues()
    {
        var read = new List<string>();

        var ownership = InstallerOwnership.Detect(_ => true, name =>
        {
            read.Add(name);
            return Saved("Server", "Uat")(name);
        });

        Assert.True(ownership.IsInstalled);
        Assert.Equal("Server", ownership.Role);
        Assert.Equal("Uat", ownership.Environment);
        Assert.True(ownership.IsServerRole);
        Assert.Equal([InstallerOwnership.RoleValueName, InstallerOwnership.EnvironmentValueName, InstallerOwnership.InstallFolderValueName], read);
    }

    [Fact]
    public void Detect_Installed_WorkstationRoleIsNotTheServerRole()
    {
        var ownership = InstallerOwnership.Detect(_ => true, Saved("Workstation", null));

        Assert.True(ownership.IsInstalled);
        Assert.Equal("Workstation", ownership.Role);
        Assert.False(ownership.IsServerRole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Detect_Installed_BlankSavedValuesAreNull(string? blank)
    {
        var ownership = InstallerOwnership.Detect(_ => true, Saved(blank, blank));

        Assert.True(ownership.IsInstalled);
        Assert.Null(ownership.Role);
        Assert.Null(ownership.Environment);
        Assert.False(ownership.IsServerRole);
    }

    [Fact]
    public void Detect_Installed_TrimsTheSavedValues()
    {
        var ownership = InstallerOwnership.Detect(_ => true, Saved("  Server ", "\tUat\r\n"));

        Assert.Equal("Server", ownership.Role);
        Assert.Equal("Uat", ownership.Environment);
        Assert.True(ownership.IsServerRole);
    }

    [Theory]
    [InlineData("Server")]
    [InlineData("server")]
    [InlineData("SERVER")]
    [InlineData("sErVeR")]
    [InlineData(" server ")]
    public void IsServerRole_IgnoresCase(string role) =>
        Assert.True(InstallerOwnership.Detect(_ => true, Saved(role, null)).IsServerRole);

    [Theory]
    [InlineData("Workstation")]
    [InlineData("Servers")]
    [InlineData("Serve")]
    [InlineData("Server Role")]
    public void IsServerRole_IsFalseForAnyOtherRole(string role) =>
        Assert.False(InstallerOwnership.Detect(_ => true, Saved(role, null)).IsServerRole);

    [Fact]
    public void IsServerRole_IsFalseWhenNotInstalledWhateverTheRole()
    {
        var ownership = new InstallerOwnership(isInstalled: false, role: "Server", environment: "Uat");

        Assert.False(ownership.IsInstalled);
        Assert.Null(ownership.Role);
        Assert.Null(ownership.Environment);
        Assert.False(ownership.IsServerRole);
    }

    // ---- Which service changes are the MSI's ---------------------------------------------

    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(false, "Server", false, false)]
    [InlineData(true, "Server", true, true)]
    [InlineData(true, "server", true, true)]
    [InlineData(true, "Workstation", true, false)]
    [InlineData(true, null, true, false)]
    [InlineData(true, " ", true, false)]
    public void OwnsService_InstallInAnyRole_UninstallOnlyInTheServerRole(
        bool isInstalled, string? role, bool ownsInstall, bool ownsUninstall)
    {
        var ownership = new InstallerOwnership(isInstalled, role, environment: null);

        Assert.Equal(ownsInstall, ownership.OwnsServiceInstall);
        Assert.Equal(ownsUninstall, ownership.OwnsServiceUninstall);
    }

    [Fact]
    public void Detect_LooksUpTheUpgradeCodeAsABareGuidThatBracesMakeAProductCode()
    {
        var asked = new List<string>();

        InstallerOwnership.Detect(code =>
        {
            asked.Add(code);
            return false;
        }, Saved(null, null));

        var code = Assert.Single(asked);
        Assert.Equal(InstallerOwnership.UpgradeCode, code);

        // MsiEnumRelatedProducts takes the GUID in braces, 38 characters; the wrapper around it
        // adds them, so the lookup is handed the bare form and the braced form must parse as "B".
        Assert.DoesNotContain('{', code);
        Assert.DoesNotContain('}', code);
        var braced = $"{{{code}}}";
        Assert.Equal(38, braced.Length);
        Assert.True(Guid.TryParseExact(braced, "B", out var parsed));
        Assert.Equal(parsed.ToString("B"), braced, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_RefusesMissingLookups()
    {
        Assert.Throws<ArgumentNullException>(() => InstallerOwnership.Detect(null!, Saved(null, null)));
        Assert.Throws<ArgumentNullException>(() => InstallerOwnership.Detect(_ => true, null!));
    }

    [Fact]
    public void Detect_OnThisMachine_FindsNoInstallation()
    {
        var ownership = InstallerOwnership.Detect();

        Assert.SkipWhen(ownership.IsInstalled, "The Dev.Conduit MSI is installed on this machine.");

        Assert.False(ownership.IsInstalled);
        Assert.Null(ownership.Role);
        Assert.Null(ownership.Environment);
        Assert.False(ownership.IsServerRole);
    }

    // ---- IsInstalledResult ---------------------------------------------------------------

    [Fact]
    public void IsInstalledResult_IsTrueOnlyForSuccess() =>
        Assert.True(InstallerOwnership.IsInstalledResult(0));

    [Theory]
    [InlineData(259)]  // ERROR_NO_MORE_ITEMS: no related product
    [InlineData(87)]   // ERROR_INVALID_PARAMETER
    [InlineData(1605)] // ERROR_UNKNOWN_PRODUCT
    [InlineData(1)]
    [InlineData(-1)]
    public void IsInstalledResult_IsFalseForAnyError(int errorCode) =>
        Assert.False(InstallerOwnership.IsInstalledResult(errorCode));

    // ---- UpdateSavedEnvironment ----------------------------------------------------------

    [Fact]
    public void UpdateSavedEnvironment_WithoutTheMsiWritesNothingAndIsFalse()
    {
        var ownership = new InstallerOwnership(isInstalled: false, role: null, environment: null);

        Assert.False(ownership.UpdateSavedEnvironment("Uat"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UpdateSavedEnvironment_RefusesABlankName(string? name)
    {
        var ownership = new InstallerOwnership(isInstalled: false, role: null, environment: null);

        Assert.ThrowsAny<ArgumentException>(() => ownership.UpdateSavedEnvironment(name!));
    }

    // ---- Constants -----------------------------------------------------------------------

    [Fact]
    public void Constants_NameTheMsisSavedChoice()
    {
        Assert.Equal(@"SOFTWARE\Proxylayer\Dev.Conduit", InstallerOwnership.SavedChoiceKey);
        Assert.Equal("Role", InstallerOwnership.RoleValueName);
        Assert.Equal("Environment", InstallerOwnership.EnvironmentValueName);
        Assert.Equal("Server", InstallerOwnership.ServerRole);
    }

    [Fact]
    public void Constants_MatchTheKeyAndValuesThePackageSavesItsChoiceIn()
    {
        var savedChoice = InstallerSource.Document()
            .Descendants(InstallerSource.Wix + "RegistryKey")
            .Where(key => string.Equals((string?)key.Attribute("Root"), "HKLM", StringComparison.OrdinalIgnoreCase)
                && string.Equals((string?)key.Attribute("Key"), InstallerOwnership.SavedChoiceKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var key = Assert.Single(savedChoice);
        var names = key.Elements(InstallerSource.Wix + "RegistryValue").Select(value => (string?)value.Attribute("Name")).ToList();
        Assert.Contains(InstallerOwnership.RoleValueName, names);
        Assert.Contains(InstallerOwnership.EnvironmentValueName, names);
    }
}
