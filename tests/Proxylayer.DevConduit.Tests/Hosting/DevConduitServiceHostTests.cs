using System.Runtime.Versioning;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>The install-time checks of the Windows service host. Nothing is installed.</summary>
[SupportedOSPlatform("windows")]
public sealed class DevConduitServiceHostTests
{
    [Theory]
    [InlineData("Uat", "Uat")]
    [InlineData("Production", "Production")]
    [InlineData("Development", "Development")]
    [InlineData("  Uat  ", "Uat")]
    [InlineData("\tStaging\r\n", "Staging")]
    public void ValidateEnvironmentName_AcceptsANameAndTrimsIt(string value, string expected) =>
        Assert.Equal(expected, DevConduitServiceHost.ValidateEnvironmentName(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("Uat=1")]
    [InlineData("=Uat")]
    [InlineData("DOTNET_ENVIRONMENT=Uat")]
    [InlineData("Uat\0")]
    [InlineData("U\0at")]
    [InlineData("U\nat")]
    [InlineData("U\tat")]
    public void ValidateEnvironmentName_RejectsAValueThatCannotBeOneEntry(string? value)
    {
        var exception = Assert.Throws<ArgumentException>(() => DevConduitServiceHost.ValidateEnvironmentName(value));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("-environment:Uat", exception.Message);
    }

    // ---- LeavesToInstaller ---------------------------------------------------------------

    private static Func<InstallerOwnership> Counted(bool installed, Action counted) =>
        Counted(installed, installed ? "Server" : null, counted);

    private static Func<InstallerOwnership> Counted(bool installed, string? role, Action counted) => () =>
    {
        counted();
        return new InstallerOwnership(installed, role, null);
    };

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("INSTALL")]
    [InlineData("install", "-environment:Uat")]
    [InlineData("-servicename:Dev.Conduit", "Uninstall")]
    public void LeavesToInstaller_IsTrueForInstallOrUninstallWhileTheServerMsiIsInstalled(params string[] args)
    {
        var detections = 0;

        Assert.True(DevConduitServiceHost.LeavesToInstaller(args, Counted(installed: true, () => detections++)));
        Assert.Equal(1, detections);
    }

    [Theory]
    [InlineData("Workstation", "install")]
    [InlineData("Workstation", "INSTALL", "-environment:Uat")]
    [InlineData(null, "install")]
    public void LeavesToInstaller_IsTrueForInstallWhileTheMsiIsInstalledInAnyRole(string? role, params string[] args)
    {
        var detections = 0;

        Assert.True(DevConduitServiceHost.LeavesToInstaller(args, Counted(installed: true, role, () => detections++)));
        Assert.Equal(1, detections);
    }

    [Theory]
    [InlineData("Workstation", "uninstall")]
    [InlineData("Workstation", "-servicename:Dev.Conduit", "Uninstall")]
    [InlineData(null, "uninstall")]
    public void LeavesToInstaller_IsFalseForUninstallUnlessTheMsiHasItsServerRole(string? role, params string[] args)
    {
        var detections = 0;

        Assert.False(DevConduitServiceHost.LeavesToInstaller(args, Counted(installed: true, role, () => detections++)));
        Assert.Equal(1, detections);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("Uninstall", "-instance:x")]
    public void LeavesToInstaller_IsFalseForInstallOrUninstallWithoutTheMsi(params string[] args)
    {
        var detections = 0;

        Assert.False(DevConduitServiceHost.LeavesToInstaller(args, Counted(installed: false, () => detections++)));
        Assert.Equal(1, detections);
    }

    [Theory]
    [InlineData]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("help")]
    [InlineData("STOP")]
    [InlineData("-displayname:Dev.Conduit", "-servicename:Dev.Conduit")]
    [InlineData("--mode", "server")]
    public void LeavesToInstaller_NeverLooksUpTheMsiForAnyOtherCommandLine(params string[] args)
    {
        var detections = 0;

        Assert.False(DevConduitServiceHost.LeavesToInstaller(args, Counted(installed: true, () => detections++)));
        Assert.Equal(0, detections);
    }

    [Fact]
    public void LeavesToInstaller_RefusesAMissingLookup() =>
        Assert.Throws<ArgumentNullException>(() => DevConduitServiceHost.LeavesToInstaller(["start"], null!));
}
