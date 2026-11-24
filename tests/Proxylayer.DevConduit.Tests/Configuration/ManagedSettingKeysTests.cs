using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>The keys the setup window and an import may write to each settings file.</summary>
public sealed class ManagedSettingKeysTests
{
    [Fact]
    public void Machine_IsExactlyTheServerPageRoutesAndUpdateKeys() =>
        Assert.Equal(
            [
                "AzureAd:ClientCredentials",
                "AzureAd:ClientId",
                "AzureAd:Scopes",
                "AzureAd:TenantId",
                "DevConduit:AllowedGroupIds",
                "DevConduit:AllowedUserOids",
                "DevConduit:Route:AllowedTargets",
                "DevConduit:Routes",
                "DevConduit:Server:ClientAddress",
                "DevConduit:Server:Hostname",
                "DevConduit:Server:Port",
                "DevConduit:Update:AutoInstall"
            ],
            ManagedSettingKeys.Machine.Order(StringComparer.Ordinal));

    [Fact]
    public void Imported_IsExactlyTheEntraIdsRemotesAndRoutes() =>
        Assert.Equal(
            [
                "AzureAd:ClientId",
                "AzureAd:TenantId",
                "DevConduit:Remotes",
                "DevConduit:Routes"
            ],
            ManagedSettingKeys.Imported.Order(StringComparer.Ordinal));

    [Fact]
    public void Seed_IsExactlyTheEntraIdsScopeAndRoutes() =>
        Assert.Equal(
            [
                "AzureAd:ClientId",
                "AzureAd:Scopes",
                "AzureAd:TenantId",
                "DevConduit:Routes"
            ],
            ManagedSettingKeys.Seed.Order(StringComparer.Ordinal));

    [Fact]
    public void User_IsExactlyTheRoutesLocalModeTrayAndUpdateKeys() =>
        Assert.Equal(
            [
                "DevConduit:Local:CaptureEnabled",
                "DevConduit:Local:ForwardProxyEnabled",
                "DevConduit:Local:ForwardProxyPort",
                "DevConduit:Local:Port",
                "DevConduit:Routes",
                "DevConduit:Tray:AutoStart",
                "DevConduit:Update:AutoInstall"
            ],
            ManagedSettingKeys.User.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(ManagedSettingKeys.LocalPort)]
    [InlineData(ManagedSettingKeys.LocalForwardProxyEnabled)]
    [InlineData(ManagedSettingKeys.Remotes)]
    [InlineData("Kestrel:Endpoints:Https")]
    [InlineData("DevConduit:DataRoot")]
    [InlineData("DevConduit:DefaultRemote")]
    public void Machine_HoldsNoLocalModeRemoteCertificateOrDataKey(string key) =>
        Assert.DoesNotContain(key, ManagedSettingKeys.Machine);

    [Theory]
    [InlineData(ManagedSettingKeys.TenantId)]
    [InlineData(ManagedSettingKeys.ClientCredentials)]
    [InlineData(ManagedSettingKeys.ServerHostname)]
    [InlineData(ManagedSettingKeys.AllowedUserOids)]
    [InlineData(ManagedSettingKeys.Remotes)]
    [InlineData("DevConduit:Local:SharedSecret")]
    public void User_HoldsNoServerRemoteOrSecretKey(string key) =>
        Assert.DoesNotContain(key, ManagedSettingKeys.User);

    [Fact]
    public void BothLists_MatchIgnoringCase()
    {
        Assert.Contains("devconduit:routes", ManagedSettingKeys.Machine);
        Assert.Contains("AZUREAD:TENANTID", ManagedSettingKeys.Machine);
        Assert.Contains("devconduit:local:port", ManagedSettingKeys.User);
    }

    [Fact]
    public void Unmanaged_IsEmptyWhenEveryKeyIsAllowed()
    {
        var changes = new SettingsChangeSet()
            .Set(ManagedSettingKeys.LocalPort, 9000)
            .Set("devconduit:routes", new System.Text.Json.Nodes.JsonObject())
            .Remove(ManagedSettingKeys.LocalCaptureEnabled);

        Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.User));
    }

    [Fact]
    public void Unmanaged_FlagsSetAndRemovedKeysOutsideTheList()
    {
        var changes = new SettingsChangeSet()
            .Set(ManagedSettingKeys.TenantId, "t")
            .Set(ManagedSettingKeys.LocalPort, 9000)
            .Remove("Logging:LogLevel:Default");

        Assert.Equal(
            ["AzureAd:TenantId", "Logging:LogLevel:Default"],
            ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.User).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("DevConduit:Routes:orders")]
    [InlineData("DevConduit:Routes:orders:Target")]
    [InlineData("DevConduit")]
    [InlineData("DevConduit:Server")]
    [InlineData("Kestrel:Endpoints:Https:Certificate:Path")]
    [InlineData("AzureAd:ClientSecret")]
    [InlineData("DevConduit:Mode")]
    public void Unmanaged_FlagsAParentOrChildOfAManagedKey(string key)
    {
        var changes = new SettingsChangeSet().Set(key, "x");

        Assert.Equal([key], ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Machine));
    }

    [Fact]
    public void Unmanaged_FlagsAMachineKeyAgainstTheUserList() =>
        Assert.Equal(
            [ManagedSettingKeys.ServerHostname],
            ManagedSettingKeys.Unmanaged(new SettingsChangeSet().Set(ManagedSettingKeys.ServerHostname, "vm.example"), ManagedSettingKeys.User));
}
