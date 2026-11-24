using System.Runtime.Versioning;
using System.ServiceProcess;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// Which service buttons the Server page offers, with and without the MSI. The installation is
/// constructed, never detected, and no service is queried.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceActionsTests
{
    private static readonly InstallerOwnership NoMsi = new(isInstalled: false, role: null, environment: null);
    private static readonly InstallerOwnership ServerMsi = new(isInstalled: true, role: "Server", environment: "Uat");
    private static readonly InstallerOwnership WorkstationMsi = new(isInstalled: true, role: "Workstation", environment: null);

    // ---- Without the MSI: the tray installs and removes the service ----------------------

    [Fact]
    public void NoMsi_ServiceMissing_OffersInstallOnly() =>
        Assert.Equal(
            new ServiceActions(CanInstall: true, CanUninstall: false, CanRestart: false, InstallerNote: null),
            ServiceActions.For(null, NoMsi));

    [Fact]
    public void NoMsi_ServiceStopped_OffersUninstallOnly() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: false, InstallerNote: null),
            ServiceActions.For(ServiceControllerStatus.Stopped, NoMsi));

    [Fact]
    public void NoMsi_ServiceRunning_OffersUninstallAndRestart() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: true, InstallerNote: null),
            ServiceActions.For(ServiceControllerStatus.Running, NoMsi));

    [Theory]
    [InlineData(ServiceControllerStatus.StartPending)]
    [InlineData(ServiceControllerStatus.StopPending)]
    [InlineData(ServiceControllerStatus.Paused)]
    public void NoMsi_ServiceInTransition_OffersUninstallButNotRestart(ServiceControllerStatus status) =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: false, InstallerNote: null),
            ServiceActions.For(status, NoMsi));

    // ---- The MSI in its Server role owns the service -------------------------------------

    [Fact]
    public void ServerMsi_ServiceRunning_OffersRestartOnlyWithTheManagedNote() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: false, CanRestart: true, ServiceActions.ManagedByInstallerNote),
            ServiceActions.For(ServiceControllerStatus.Running, ServerMsi));

    [Theory]
    [InlineData(ServiceControllerStatus.Stopped)]
    [InlineData(ServiceControllerStatus.StartPending)]
    [InlineData(ServiceControllerStatus.StopPending)]
    public void ServerMsi_ServiceNotRunning_OffersNothingWithTheManagedNote(ServiceControllerStatus status) =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: false, CanRestart: false, ServiceActions.ManagedByInstallerNote),
            ServiceActions.For(status, ServerMsi));

    [Fact]
    public void ServerMsi_ServiceMissing_OffersNothingAndSaysToRepair() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: false, CanRestart: false, ServiceActions.MissingServiceNote),
            ServiceActions.For(null, ServerMsi));

    [Theory]
    [InlineData("server")]
    [InlineData("SERVER")]
    public void ServerMsi_RoleInAnyCase_SaysToRepairAMissingService(string role) =>
        Assert.Equal(
            ServiceActions.MissingServiceNote,
            ServiceActions.For(null, new InstallerOwnership(isInstalled: true, role, environment: null)).InstallerNote);

    // ---- The MSI in its Workstation role installs no service, so one present is stray ----

    [Fact]
    public void WorkstationMsi_ServiceMissing_OffersNothingWithTheWorkstationNote() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: false, CanRestart: false, ServiceActions.WorkstationInstallerNote),
            ServiceActions.For(null, WorkstationMsi));

    [Fact]
    public void WorkstationMsi_ServiceStopped_OffersUninstallWithTheStrayNote() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: false, ServiceActions.StrayServiceNote),
            ServiceActions.For(ServiceControllerStatus.Stopped, WorkstationMsi));

    [Fact]
    public void WorkstationMsi_ServiceRunning_OffersUninstallAndRestartWithTheStrayNote() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: true, ServiceActions.StrayServiceNote),
            ServiceActions.For(ServiceControllerStatus.Running, WorkstationMsi));

    [Theory]
    [InlineData(ServiceControllerStatus.StartPending)]
    [InlineData(ServiceControllerStatus.StopPending)]
    [InlineData(ServiceControllerStatus.Paused)]
    public void WorkstationMsi_ServiceInTransition_OffersUninstallButNotRestart(ServiceControllerStatus status) =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, CanRestart: false, ServiceActions.StrayServiceNote),
            ServiceActions.For(status, WorkstationMsi));

    [Fact]
    public void MsiWithNoSavedRole_ServiceMissing_GetsTheWorkstationNote() =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: false, CanRestart: false, ServiceActions.WorkstationInstallerNote),
            ServiceActions.For(null, new InstallerOwnership(isInstalled: true, role: null, environment: null)));

    [Theory]
    [InlineData(ServiceControllerStatus.Stopped, false)]
    [InlineData(ServiceControllerStatus.Running, true)]
    public void MsiWithNoSavedRole_ServicePresent_CountsAsWorkstationAndOffersUninstall(ServiceControllerStatus status, bool canRestart) =>
        Assert.Equal(
            new ServiceActions(CanInstall: false, CanUninstall: true, canRestart, ServiceActions.StrayServiceNote),
            ServiceActions.For(status, new InstallerOwnership(isInstalled: true, role: null, environment: null)));

    // ---- Arguments and notes -------------------------------------------------------------

    [Fact]
    public void For_RefusesAMissingInstallation() =>
        Assert.Throws<ArgumentNullException>(() => ServiceActions.For(null, null!));

    [Fact]
    public void Notes_AreDistinctAndPointAtTheMsi()
    {
        string[] notes =
        [
            ServiceActions.ManagedByInstallerNote,
            ServiceActions.WorkstationInstallerNote,
            ServiceActions.MissingServiceNote,
            ServiceActions.StrayServiceNote
        ];

        Assert.Equal(notes.Length, notes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(notes, note => Assert.Contains("MSI", note, StringComparison.Ordinal));
        Assert.Contains("ROLE=Server", ServiceActions.WorkstationInstallerNote, StringComparison.Ordinal);
        Assert.Contains("ROLE=Server", ServiceActions.StrayServiceNote, StringComparison.Ordinal);
        Assert.Contains("not installed by", ServiceActions.StrayServiceNote, StringComparison.Ordinal);
    }
}
