using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// The content root a launch uses, and the start-up refusals that happen before anything is
/// registered. No application is ever built: every TryBuild here fails before LocalModeStartup
/// or the token warmup is reached, and blank Entra ids are passed as well.
/// </summary>
public sealed class DevConduitApplicationTests
{
    private static readonly string BaseDirectory = AppContext.BaseDirectory;

    private static string[] WithBlankIdentity(params string[] args) =>
        [.. args, "--AzureAd:TenantId=", "--AzureAd:ClientId=", "--DevConduit:Local:InteractiveFallback=false"];

    [Fact]
    public void ResolveContentRoot_IsTheBaseDirectoryForAService() =>
        Assert.Equal(BaseDirectory, DevConduitApplication.ResolveContentRoot(runningAsService: true));

    [Fact]
    public void ResolveContentRoot_KeepsTheDefaultForAConsoleRunWithoutWwwroot()
    {
        Assert.SkipWhen(
            Directory.Exists(Path.Combine(BaseDirectory, "wwwroot")),
            "The test output folder has a wwwroot of its own.");

        Assert.Null(DevConduitApplication.ResolveContentRoot(runningAsService: false));
    }

    [Fact]
    public void ResolveContentRoot_IsTheBaseDirectoryForAConsoleRunFromAPublishedFolder()
    {
        // The method reads only AppContext.BaseDirectory, so a published layout is imitated by
        // giving the test output folder an empty wwwroot for the duration of this test.
        var wwwroot = Path.Combine(BaseDirectory, "wwwroot");
        Assert.SkipWhen(Directory.Exists(wwwroot), "The test output folder already has a wwwroot.");

        Directory.CreateDirectory(wwwroot);
        try
        {
            Assert.Equal(BaseDirectory, DevConduitApplication.ResolveContentRoot(runningAsService: false));
        }
        finally
        {
            Directory.Delete(wwwroot);
        }
    }

    [Theory]
    [InlineData("--mode", "local")]
    [InlineData("--mode", "LOCAL")]
    [InlineData("--mode=local")]
    [InlineData("--DevConduit:Mode=Local")]
    public void TryBuild_RefusesLocalModeAsAService(params string[] mode)
    {
        using var layout = new SettingsLayout();

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity(mode),
            runningAsService: true,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.StartsWith("Local mode cannot run as a Windows service:", fatalMessage);
        Assert.Contains("DevConduit:Mode", fatalMessage);
        Assert.Contains("--mode local", fatalMessage);
    }

    [Fact]
    public void TryBuild_RefusesAnUnknownModeAsAService()
    {
        using var layout = new SettingsLayout();

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity("--mode", "relay"),
            runningAsService: true,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.Equal("'relay' is not a Dev.Conduit mode. --mode accepts 'server' or 'local'.", fatalMessage);
    }

    [Fact]
    public void FatalExitCode_IsOne() =>
        Assert.Equal(1, DevConduitApplication.FatalExitCode);

    // The overload taking the settings files is the only one used here: only temp files are
    // injected, so no test reads the real ProgramData or LocalAppData settings files, and every
    // build stops at the settings files or at the mode, before anything is registered.

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[ 1, 2 ]")]
    public void TryBuild_RefusesAMalformedUserSettingsFileNamingIt(string text)
    {
        using var layout = new SettingsLayout();
        layout.User(text);

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity("--mode", "local"),
            runningAsService: false,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.StartsWith($"The settings file {layout.UserPath} is not valid:", fatalMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryBuild_RefusesAMalformedMachineSettingsFileNamingIt(bool runningAsService)
    {
        using var layout = new SettingsLayout();
        layout.Machine("{ \"DevConduit\": ");

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity("--mode", "server"),
            runningAsService,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.StartsWith($"The settings file {layout.MachinePath} is not valid:", fatalMessage);
    }

    [Fact]
    public void TryBuild_RefusesAMachineSettingsFileAnOrdinaryUserCouldHaveWritten()
    {
        using var layout = new SettingsLayout();
        layout.Machine("""{ "DevConduit": { "Mode": "server" } }""");

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity("--mode", "server"),
            runningAsService: true,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.StartsWith($"The machine settings file {layout.MachinePath} ", fatalMessage);
    }

    [Fact]
    public void TryBuild_AsAServiceNeverReadsTheUserSettingsFile()
    {
        using var layout = new SettingsLayout();
        layout.User("{ broken");

        // Gets past the settings files and stops at the mode, so nothing is built.
        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity("--mode", "relay"),
            runningAsService: true,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.Equal("'relay' is not a Dev.Conduit mode. --mode accepts 'server' or 'local'.", fatalMessage);
    }

    [Fact]
    public void TryBuild_ReadsTheModeFromTheUserSettingsFile()
    {
        using var layout = new SettingsLayout();
        layout.User("""{ "DevConduit": { "Mode": "relay" } }""");

        var built = DevConduitApplication.TryBuild(
            WithBlankIdentity(),
            runningAsService: false,
            layout.Files,
            logSink: null,
            out var app,
            out var fatalMessage);

        Assert.False(built);
        Assert.Null(app);
        Assert.Equal("'relay' is not a Dev.Conduit mode. DevConduit:Mode accepts 'server' or 'local'.", fatalMessage);
    }

    [Fact]
    public void TryBuild_RefusesNullSettingsFiles() =>
        Assert.Throws<ArgumentNullException>(() =>
            DevConduitApplication.TryBuild([], runningAsService: false, null!, logSink: null, out _, out _));
}
