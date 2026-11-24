using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray;
using Proxylayer.DevConduit.Tray.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>
/// Handing an update from the tray to the elevated helper: the transfer file, the helper's
/// argument checks, the declined UAC prompt through the helper seam, what the tray does with a
/// ready package, and the relauncher script. Nothing elevated is started and nothing installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateHandoffTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";
    private const string Exe = @"C:\Program Files\Proxylayer\Dev.Conduit\Dev.Conduit.exe";

    private static string TransferPath(string hex = Hex) => $@"C:\Users\u\AppData\Local\Temp\Dev.Conduit-update-{hex}.msi";

    // ---- The transfer file ---------------------------------------------------------------

    [Fact]
    public void Transfer_WriteTempFileCopiesThePackageToAProperlyNamedFile()
    {
        using var root = new TempDirectory();
        var source = Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi");
        File.WriteAllBytes(source, "package"u8.ToArray());

        var transfer = UpdateTransfer.WriteTempFile(source, root.Path);

        Assert.Matches("^Dev\\.Conduit-update-[0-9a-f]{32}\\.msi$", Path.GetFileName(transfer));
        Assert.True(UpdateTransfer.IsTransferPath(transfer));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(transfer));

        UpdateTransfer.Delete(transfer);
        Assert.False(File.Exists(transfer));
    }

    [Theory]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", true)]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789ABCDEF0123456789abcdef.msi", false)]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcde.msi", false)]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.exe", false)]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi.txt", false)]
    [InlineData(@"C:\t\Dev.Conduit-import-0123456789abcdef0123456789abcdef.msi", false)]
    [InlineData(@"C:\t\Dev.Conduit-1.2.0.msi", false)]
    [InlineData(@"Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Transfer_OnlyItsOwnNamePatternIsATransferPath(string? path, bool accepted) =>
        Assert.Equal(accepted, UpdateTransfer.IsTransferPath(path));

    [Fact]
    public void Transfer_DeleteLeavesAnyOtherFileAlone()
    {
        using var root = new TempDirectory();
        var other = Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi");
        File.WriteAllText(other, "keep");

        UpdateTransfer.Delete(other);

        Assert.True(File.Exists(other));
    }

    // ---- service-admin update arguments --------------------------------------------------

    [Fact]
    public void UpdateArguments_ATransferFileAlone()
    {
        Assert.True(UpdateAdminAction.TryParseArguments([TransferPath()], out var transfer, out var pid));
        Assert.Equal(TransferPath(), transfer);
        Assert.Null(pid);
    }

    [Fact]
    public void UpdateArguments_ATransferFileAndTheTrayProcess()
    {
        Assert.True(UpdateAdminAction.TryParseArguments([TransferPath(), "4321"], out _, out var pid));
        Assert.Equal(4321, pid);
    }

    [Fact]
    public void UpdateArguments_RefusesNone() =>
        Assert.False(UpdateAdminAction.TryParseArguments([], out _, out _));

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.msi")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", "0")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", "-4")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", "12ab")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", "99999999999")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", " 42")]
    [InlineData(@"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi", "42", "extra")]
    [InlineData("4321", @"C:\t\Dev.Conduit-update-0123456789abcdef0123456789abcdef.msi")]
    public void UpdateArguments_RefusesAnythingElse(params string[] arguments) =>
        Assert.False(UpdateAdminAction.TryParseArguments(arguments, out _, out _));

    [Fact]
    public void UpdateRun_WithBadArgumentsIsInvalidArgumentsAndTouchesNothing() =>
        Assert.Equal(ServiceAdminExitCode.InvalidArguments, UpdateAdminAction.Run([@"C:\Windows\System32\notepad.exe"]));

    [Fact]
    public void UpdateRun_ThroughTheHelperUnelevatedIsNotElevated()
    {
        Assert.SkipWhen(IsElevated(), "The test process is elevated, so the helper would act instead of refusing.");

        Assert.Equal((int)ServiceAdminExitCode.NotElevated, ServiceAdminCommand.Run([ServiceAdminCommand.UpdateAction, TransferPath()]));
    }

    [Fact]
    public void ExitCodes_TheRefusedAndFailedUpdatesHaveTheirOwnCodesAndMessages()
    {
        Assert.Equal(22, (int)ServiceAdminExitCode.UpdateRefused);
        Assert.Equal(23, (int)ServiceAdminExitCode.UpdateFailed);
        Assert.Contains("update-helper.log", ServiceAdminExitCodes.Describe(ServiceAdminExitCode.UpdateRefused), StringComparison.Ordinal);
        Assert.Contains("update-helper.log", ServiceAdminExitCodes.Describe(ServiceAdminExitCode.UpdateFailed), StringComparison.Ordinal);
        Assert.Equal("update", ServiceAdminCommand.UpdateAction);
    }

    // ---- The handoff, through the helper seam --------------------------------------------

    [Fact]
    public void Handoff_ADeclinedPromptStartsNothingAndRemovesTheTransferFile()
    {
        using var root = new TempDirectory();
        var package = Package(root);
        string? transfer = null;
        string[]? helperArguments = null;
        var relaunched = false;
        var handoff = new UpdateHandoff(
            (action, arguments) =>
            {
                helperArguments = [action, .. arguments];
                return new ServiceAdminStart(null, new ServiceAdminOutcome(Succeeded: false, Cancelled: true, string.Empty));
            },
            (_, _) => relaunched = true,
            source => transfer = UpdateTransfer.WriteTempFile(source, root.Path),
            NullLogger.Instance);

        var outcome = handoff.Run(package, Exe, trayProcessId: 4321);

        Assert.Equal(HandoffResult.Declined, outcome.Result);
        Assert.False(relaunched);
        Assert.NotNull(transfer);
        Assert.False(File.Exists(transfer));
        Assert.Equal(["update", transfer, "4321"], Assert.IsType<string[]>(helperArguments));
        Assert.True(File.Exists(package));
    }

    [Fact]
    public void Handoff_AHelperThatDidNotStartIsAFailureWithItsMessage()
    {
        using var root = new TempDirectory();
        var handoff = new UpdateHandoff(
            (_, _) => new ServiceAdminStart(null, new ServiceAdminOutcome(false, false, "The administrative helper could not be started.")),
            (_, _) => throw new InvalidOperationException("Not reached."),
            source => UpdateTransfer.WriteTempFile(source, root.Path),
            NullLogger.Instance);

        var outcome = handoff.Run(Package(root), Exe, 4321);

        Assert.Equal(HandoffResult.Failed, outcome.Result);
        Assert.Equal("The administrative helper could not be started.", outcome.Message);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "Dev.Conduit-update-*"));
    }

    [Fact]
    public void Handoff_AStartedHelperStartsTheRelauncherForItsProcessAndTheInstalledExe()
    {
        using var root = new TempDirectory();
        (int Pid, string Exe)? relauncher = null;
        var handoff = new UpdateHandoff(
            (_, _) => new ServiceAdminStart(Process.GetCurrentProcess(), new ServiceAdminOutcome(true, false, "started")),
            (pid, exe) =>
            {
                relauncher = (pid, exe);
                return true;
            },
            source => UpdateTransfer.WriteTempFile(source, root.Path),
            NullLogger.Instance);

        var outcome = handoff.Run(Package(root), Exe, 4321);

        Assert.Equal(HandoffResult.Started, outcome.Result);
        Assert.Equal((Environment.ProcessId, Exe), relauncher);
    }

    [Fact]
    public void Handoff_APackageThatCannotBeCopiedIsAFailureBeforeAnyPrompt()
    {
        var prompted = false;
        var handoff = new UpdateHandoff(
            (_, _) =>
            {
                prompted = true;
                return new ServiceAdminStart(null, new ServiceAdminOutcome(false, true, string.Empty));
            },
            (_, _) => true,
            _ => throw new IOException("The disk is full."),
            NullLogger.Instance);

        Assert.Equal(HandoffResult.Failed, handoff.Run(@"C:\nowhere\a.msi", Exe, 1).Result);
        Assert.False(prompted);
    }

    // ---- What the tray does with a ready package -----------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, true, true, false, null, TrayUpdateAction.Install)]
    [InlineData(true, true, false, false, null, TrayUpdateAction.Offer)]
    [InlineData(true, true, true, true, null, TrayUpdateAction.Offer)]
    [InlineData(true, true, true, false, 60, TrayUpdateAction.Offer)]
    [InlineData(true, true, true, false, -60, TrayUpdateAction.Install)]
    [InlineData(true, false, true, false, null, TrayUpdateAction.None)]
    [InlineData(false, true, true, false, null, TrayUpdateAction.None)]
    [InlineData(false, true, false, false, null, TrayUpdateAction.None)]
    public void Decide_InstallsAutomaticallyOnlyOnAWorkstationNotDeclinedNorFailedToday(
        bool installsUpdates, bool ready, bool autoInstall, bool declined, int? retryInMinutes, TrayUpdateAction expected) =>
        Assert.Equal(expected, TrayUpdateDecision.Decide(
            installsUpdates, ready, autoInstall, declined, retryInMinutes is { } minutes ? Now.AddMinutes(minutes) : null, Now));

    // ---- The relauncher ------------------------------------------------------------------

    [Fact]
    public void Relauncher_WaitsForTheHelperAndItsMsiexecThenStartsTheTray()
    {
        var script = UpdateRelauncher.Script(4321, Exe);

        Assert.Contains("Get-Process -Id 4321", script, StringComparison.Ordinal);
        Assert.Contains("ParentProcessId=4321 AND Name='msiexec.exe'", script, StringComparison.Ordinal);
        Assert.EndsWith($"Start-Process -FilePath '{Exe}' -ArgumentList 'tray'", script, StringComparison.Ordinal);
        Assert.Contains("AddHours(1)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Relauncher_QuotesAnApostropheInThePath()
    {
        var script = UpdateRelauncher.Script(1, @"C:\Bob's Apps\Dev.Conduit\Dev.Conduit.exe");

        Assert.Contains(@"-FilePath 'C:\Bob''s Apps\Dev.Conduit\Dev.Conduit.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Relauncher_RunsHiddenWithoutAProfileAndTheScriptEncoded()
    {
        var arguments = UpdateRelauncher.Arguments(4321, Exe);

        Assert.StartsWith("-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ", arguments, StringComparison.Ordinal);
        var encoded = arguments.Split(' ').Last();
        Assert.Equal(UpdateRelauncher.Script(4321, Exe), Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
        Assert.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", UpdateRelauncher.PowerShellPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, Exe)]
    [InlineData(5, "Dev.Conduit.exe")]
    public void Relauncher_RefusesABadProcessIdOrRelativeExe(int pid, string exe) =>
        Assert.ThrowsAny<ArgumentException>(() => UpdateRelauncher.Script(pid, exe));

    private static string Package(TempDirectory root)
    {
        var path = Path.Combine(root.Path, "Dev.Conduit-1.2.0.msi");
        File.WriteAllBytes(path, "package"u8.ToArray());
        return path;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
