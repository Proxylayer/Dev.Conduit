using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Updates;

namespace Proxylayer.DevConduit.Tests.Updates;

/// <summary>
/// The installed-build gate, the msiexec command an upgrade runs, the update settings, and the
/// MSI authoring that saves and restores the install folder. Nothing is installed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstalledBuildTests
{
    private const string Folder = @"C:\Program Files\Proxylayer\Dev.Conduit\";

    private static InstallerOwnership Installed(string? folder = Folder, string? role = "Workstation", string? environment = "Production") =>
        new(isInstalled: true, role, environment, folder);

    // ---- The gate ------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\Proxylayer\Dev.Conduit\Dev.Conduit.exe")]
    [InlineData(@"c:\program files\proxylayer\dev.conduit\dev.conduit.exe")]
    public void Evaluate_TheExeInTheSavedFolderIsAnInstalledBuild(string processPath)
    {
        var build = InstalledBuild.Evaluate(Installed(), processPath);

        Assert.True(build.IsInstalledBuild, build.Reason);
        Assert.Equal(@"C:\Program Files\Proxylayer\Dev.Conduit\Dev.Conduit.exe", build.ExePath);
    }

    [Fact]
    public void Evaluate_ASavedFolderWithoutItsTrailingSeparatorMatchesToo() =>
        Assert.True(InstalledBuild.Evaluate(Installed(Folder.TrimEnd('\\')), Folder + "Dev.Conduit.exe").IsInstalledBuild);

    [Fact]
    public void Evaluate_NoMsiIsNotAnInstalledBuild()
    {
        var build = InstalledBuild.Evaluate(new InstallerOwnership(false, null, null), Folder + "Dev.Conduit.exe");

        Assert.False(build.IsInstalledBuild);
        Assert.Null(build.ExePath);
        Assert.Contains("not installed", build.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AnInstallThatSavedNoFolderIsNotAnInstalledBuild() =>
        Assert.False(InstalledBuild.Evaluate(Installed(folder: null), Folder + "Dev.Conduit.exe").IsInstalledBuild);

    [Theory]
    [InlineData(@"C:\src\Dev.Conduit\bin\Debug\net10.0-windows\Dev.Conduit.exe")]
    [InlineData(@"C:\Program Files\Proxylayer\Dev.Conduit\sub\Dev.Conduit.exe")]
    [InlineData(@"C:\Program Files\Proxylayer\Dev.Conduit\Other.exe")]
    [InlineData(@"Dev.Conduit.exe")]
    [InlineData(null)]
    public void Evaluate_AnExeAnywhereElseIsNotAnInstalledBuild(string? processPath) =>
        Assert.False(InstalledBuild.Evaluate(Installed(), processPath).IsInstalledBuild);

    [Fact]
    public void Evaluate_ARelativeSavedFolderIsRefused() =>
        Assert.False(InstalledBuild.Evaluate(Installed(@"Proxylayer\Dev.Conduit"), Folder + "Dev.Conduit.exe").IsInstalledBuild);

    // ---- msiexec -------------------------------------------------------------------------

    [Fact]
    public void Msiexec_PassesTheSavedRoleEnvironmentAndFolder()
    {
        Assert.True(MsiexecCommand.TryBuild(
            @"C:\Windows\System32\msiexec.exe", @"C:\ProgramData\Proxylayer\Dev.Conduit\updates\Dev.Conduit-1.2.0.msi",
            @"C:\ProgramData\Proxylayer\Dev.Conduit\updates\install-1.2.0.log", Installed(role: "server", environment: "Uat"),
            out var command, out var error), error);

        Assert.Equal(@"C:\Windows\System32\msiexec.exe", command.FileName);
        Assert.Equal(
            "/i \"C:\\ProgramData\\Proxylayer\\Dev.Conduit\\updates\\Dev.Conduit-1.2.0.msi\" /qn /norestart "
            + "/l*v \"C:\\ProgramData\\Proxylayer\\Dev.Conduit\\updates\\install-1.2.0.log\" "
            + "ROLE=Server ENVIRONMENT=Uat INSTALLFOLDER=\"C:\\Program Files\\Proxylayer\\Dev.Conduit\"",
            command.Arguments);
    }

    [Fact]
    public void Msiexec_UsesTheSystemFolder() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "msiexec.exe"),
            MsiexecCommand.MsiexecPath,
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Msiexec_NoSavedRoleIsWorkstationAndNoEnvironmentIsLeftOut()
    {
        Assert.True(MsiexecCommand.TryBuild(@"C:\u\a.msi", @"C:\u\a.log", Installed(role: null, environment: null), out var command, out _));

        Assert.Contains(" ROLE=Workstation INSTALLFOLDER=", command.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("ENVIRONMENT", command.Arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\u\a.msi", @"C:\u\a.log", "Admin", "Production", Folder)]
    [InlineData(@"C:\u\a.msi", @"C:\u\a.log", "Server", "Uat ROLE=Server", Folder)]
    [InlineData(@"C:\u\a.msi", @"C:\u\a.log", "Server", "Production", null)]
    [InlineData(@"C:\u\a.msi", @"C:\u\a.log", "Server", "Production", "relative\\folder")]
    [InlineData(@"C:\u\a.msi", @"C:\u\a.log", "Server", "Production", "C:\\x\" EVIL=\"1")]
    [InlineData(@"a.msi", @"C:\u\a.log", "Server", "Production", Folder)]
    [InlineData(@"C:\u\a.exe", @"C:\u\a.log", "Server", "Production", Folder)]
    [InlineData(@"C:\u\a"".msi", @"C:\u\a.log", "Server", "Production", Folder)]
    [InlineData(@"C:\u\a.msi", @"a.log", "Server", "Production", Folder)]
    public void Msiexec_RefusesAnythingThatCannotBePassedSafely(string msi, string log, string role, string environment, string? folder) =>
        Assert.False(MsiexecCommand.TryBuild(msi, log, Installed(folder, role, environment), out _, out _));

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1603, false)]
    [InlineData(1618, false)]
    public void Msiexec_SuccessIsZeroOrRebootRequired(int exitCode, bool succeeded) =>
        Assert.Equal(succeeded, MsiexecCommand.Succeeded(exitCode));

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    public void Msiexec_ExitCodeFromLogReadsTheLastEngineResult(string encoding)
    {
        using var root = new TempDirectory();
        var log = Path.Combine(root.Path, "install.log");
        var text = "=== Verbose logging started ===\r\nMSI (s) (A0:B4) [03:00:12:345]: MainEngineThread is returning 1603\r\n"
            + "MSI (c) (C8:D0) [03:00:12:400]: MainEngineThread is returning 1603\r\n=== Verbose logging stopped ===\r\n";
        File.WriteAllText(log, text, encoding == "utf-16" ? Encoding.Unicode : new UTF8Encoding(false));

        Assert.Equal(1603, MsiexecCommand.ExitCodeFromLog(log));
        Assert.Null(MsiexecCommand.ExitCodeFromLog(Path.Combine(root.Path, "missing.log")));
    }

    // ---- Settings ------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultsAreTheReleasesFeedHourlyAutomaticAtThree()
    {
        var options = UpdateOptions.From(new ConfigurationBuilder().Build());

        Assert.Equal("https://github.com/Proxylayer/Dev.Conduit", options.FeedUrl);
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(1), options.CheckInterval);
        Assert.True(options.AutoInstall);
        Assert.Equal(3, options.EffectiveInstallHour);
        Assert.False(options.IncludePrerelease);
    }

    [Fact]
    public void Options_AreReadFromTheSectionAndAnEmptyFeedTurnsUpdatesOff()
    {
        var options = UpdateOptions.From(Configuration(new()
        {
            ["DevConduit:Update:FeedUrl"] = "",
            ["DevConduit:Update:CheckIntervalHours"] = "0",
            ["DevConduit:Update:AutoInstall"] = "false",
            ["DevConduit:Update:InstallHour"] = "22",
            ["DevConduit:Update:IncludePrerelease"] = "true"
        }));

        Assert.False(options.Enabled);
        Assert.Null(options.CheckInterval);
        Assert.False(options.AutoInstall);
        Assert.Equal(22, options.EffectiveInstallHour);
        Assert.True(options.IncludePrerelease);
    }

    [Fact]
    public void Options_AnUnreadableValueKeepsItsDefault()
    {
        var options = UpdateOptions.From(Configuration(new()
        {
            ["DevConduit:Update:CheckIntervalHours"] = "often",
            ["DevConduit:Update:AutoInstall"] = "maybe",
            ["DevConduit:Update:InstallHour"] = "three"
        }));

        Assert.Equal(TimeSpan.FromHours(1), options.CheckInterval);
        Assert.True(options.AutoInstall);
        Assert.Equal(3, options.EffectiveInstallHour);
    }

    [Fact]
    public void Options_TheSwitchIsTheManagedKey() =>
        Assert.Equal("DevConduit:Update:AutoInstall", UpdateOptions.AutoInstallKey);

    // ---- The MSI saves and restores the install folder -----------------------------------

    [Fact]
    public void ThePackage_SavesTheInstallFolderWithTheRoleAndEnvironment()
    {
        var registration = InstallerSource.Document().Descendants(InstallerSource.Wix + "Component")
            .Single(component => (string?)component.Attribute("Id") == "RoleRegistration");
        var value = registration.Descendants(InstallerSource.Wix + "RegistryValue")
            .Single(element => (string?)element.Attribute("Name") == InstallerOwnership.InstallFolderValueName);

        Assert.Equal("[INSTALLFOLDER]", (string?)value.Attribute("Value"));
        Assert.Equal(@"SOFTWARE\Proxylayer\Dev.Conduit", (string?)value.Parent!.Attribute("Key"));
        Assert.Equal(InstallerOwnership.SavedChoiceKey, (string?)value.Parent!.Attribute("Key"));
    }

    [Fact]
    public void ThePackage_ReadsTheSavedFolderBackOnUpgradeUnlessOneIsGiven()
    {
        var package = InstallerSource.Package();
        var search = package.Descendants(InstallerSource.Wix + "RegistrySearch")
            .Single(element => (string?)element.Attribute("Name") == InstallerOwnership.InstallFolderValueName);
        Assert.Equal("SAVED_INSTALLFOLDER", (string?)search.Parent!.Attribute("Id"));
        Assert.Equal(@"SOFTWARE\Proxylayer\Dev.Conduit", (string?)search.Attribute("Key"));
        Assert.Equal("always64", (string?)search.Attribute("Bitness"));

        var set = package.Elements(InstallerSource.Wix + "SetProperty")
            .Single(element => (string?)element.Attribute("Id") == "INSTALLFOLDER");
        Assert.Equal("[SAVED_INSTALLFOLDER]", (string?)set.Attribute("Value"));
        Assert.Equal("NOT INSTALLFOLDER AND SAVED_INSTALLFOLDER", (string?)set.Attribute("Condition"));
        Assert.Equal("both", (string?)set.Attribute("Sequence"));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
