using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// Which data root a launch defaults to: an explicit one always, the machine folder for a
/// server run as or beside the service, the per-user folder otherwise. The service probe is
/// injected, so the real service is never queried.
/// </summary>
public sealed class DataRootDefaultTests
{
    private static ConfigurationManager Configuration(string? dataRoot = null)
    {
        var configuration = new ConfigurationManager();
        if (dataRoot is not null)
        {
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(DataRootDefault.Key, dataRoot)]);
        }

        return configuration;
    }

    private static string LocalDefault =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Proxylayer", "Dev.Conduit");

    private static string MachineDefault =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Proxylayer", "Dev.Conduit");

    [Fact]
    public void Constants_AreTheKeyAndTheUnexpandedMachineFolder()
    {
        Assert.Equal("DevConduit:DataRoot", DataRootDefault.Key);
        Assert.Equal(@"%ProgramData%\Proxylayer\Dev.Conduit", DataRootDefault.MachineDataRoot);
    }

    // ---- UsesMachineFolder ---------------------------------------------------------------

    [Theory]
    [InlineData(DevConduitMode.Server, true, false, true)]
    [InlineData(DevConduitMode.Server, false, true, true)]
    [InlineData(DevConduitMode.Server, true, true, true)]
    [InlineData(DevConduitMode.Server, false, false, false)]
    [InlineData(DevConduitMode.Local, false, false, false)]
    [InlineData(DevConduitMode.Local, false, true, false)]
    [InlineData(DevConduitMode.Local, true, true, false)]
    public void UsesMachineFolder_OnlyForAServerRunAsOrBesideTheService(DevConduitMode mode, bool asService, bool installed, bool expected) =>
        Assert.Equal(expected, DataRootDefault.UsesMachineFolder(mode, asService, installed));

    // ---- Apply ---------------------------------------------------------------------------

    [Theory]
    [InlineData(DevConduitMode.Server, true)]
    [InlineData(DevConduitMode.Server, false)]
    [InlineData(DevConduitMode.Local, false)]
    public void Apply_AnExplicitDataRootWinsAndTheProbeIsNotAsked(DevConduitMode mode, bool asService)
    {
        var configuration = Configuration(@"D:\explicit");
        var asked = false;

        Assert.False(DataRootDefault.Apply(configuration, mode, asService, () => asked = true));

        Assert.Equal(@"D:\explicit", configuration[DataRootDefault.Key]);
        Assert.False(asked);
    }

    [Fact]
    public void Apply_ServerAsAServiceUsesTheMachineFolderWithoutAskingTheProbe()
    {
        var configuration = Configuration();
        var asked = false;

        Assert.True(DataRootDefault.Apply(configuration, DevConduitMode.Server, runningAsService: true, () => asked = true));

        Assert.Equal(DataRootDefault.MachineDataRoot, configuration[DataRootDefault.Key]);
        Assert.False(asked);
    }

    [Fact]
    public void Apply_ServerConsoleWithTheServiceInstalledUsesTheMachineFolder()
    {
        var configuration = Configuration();

        Assert.True(DataRootDefault.Apply(configuration, DevConduitMode.Server, runningAsService: false, () => true));

        Assert.Equal(DataRootDefault.MachineDataRoot, configuration[DataRootDefault.Key]);
    }

    [Fact]
    public void Apply_ServerConsoleWithoutTheServiceLeavesThePerUserDefault()
    {
        var configuration = Configuration();
        var asked = 0;

        Assert.False(DataRootDefault.Apply(configuration, DevConduitMode.Server, runningAsService: false, () => { asked++; return false; }));

        Assert.Null(configuration[DataRootDefault.Key]);
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_LocalModeNeverUsesTheMachineFolderNorAsksTheProbe(bool asService)
    {
        var configuration = Configuration();
        var asked = false;

        Assert.False(DataRootDefault.Apply(configuration, DevConduitMode.Local, asService, () => asked = true));

        Assert.Null(configuration[DataRootDefault.Key]);
        Assert.False(asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_ABlankDataRootCountsAsNone(string blank)
    {
        var configuration = Configuration(blank);

        Assert.True(DataRootDefault.Apply(configuration, DevConduitMode.Server, runningAsService: true, () => false));

        Assert.Equal(DataRootDefault.MachineDataRoot, configuration[DataRootDefault.Key]);
    }

    [Fact]
    public void Apply_RefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => DataRootDefault.Apply(null!, DevConduitMode.Server, false, () => false));
        Assert.Throws<ArgumentNullException>(() => DataRootDefault.Apply(Configuration(), DevConduitMode.Server, false, null!));
    }

    // ---- Resolve -------------------------------------------------------------------------

    [Fact]
    public void Resolve_AConfiguredRootWinsAndIsExpanded()
    {
        Assert.Equal(@"D:\data", DataRootDefault.Resolve(@"D:\data", usesMachineFolder: true));
        Assert.Equal(MachineDefault, DataRootDefault.Resolve(@"%ProgramData%\Proxylayer\Dev.Conduit", usesMachineFolder: false));
    }

    [Fact]
    public void Resolve_WithoutOneIsTheMachineFolderWhenItApplies() =>
        Assert.Equal(MachineDefault, DataRootDefault.Resolve(null, usesMachineFolder: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_WithoutOneIsThePerUserFolderOtherwise(string? configured) =>
        Assert.Equal(LocalDefault, DataRootDefault.Resolve(configured, usesMachineFolder: false));
}
