using System.Net;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Services;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>The networks outbound calls may never reach, and how configuration entries are parsed into them.</summary>
public sealed class EgressNetworkGuardTests
{
    private static EgressNetworkGuard DefaultGuard() => new(new DevConduitOptions().BlockedEgressNetworks);

    [Fact]
    public void Defaults_AreTheLinkLocalAndAzureHostNetworks()
    {
        Assert.Equal(
            ["169.254.0.0/16", "168.63.129.16/32", "fe80::/10"],
            DefaultGuard().Networks.Select(network => network.ToString()));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("168.63.129.16")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("fe80::1")]
    public void IsBlocked_RefusesAnAddressInADefaultNetwork(string address)
    {
        Assert.True(DefaultGuard().IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("168.63.129.17")]
    [InlineData("8.8.8.8")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void IsBlocked_AllowsAnAddressOutsideTheDefaultNetworks(string address)
    {
        Assert.False(DefaultGuard().IsBlocked(IPAddress.Parse(address)));
    }

    [Fact]
    public void Constructor_RemovesDuplicatesAndKeepsConfigurationOrder()
    {
        var guard = new EgressNetworkGuard(
        [
            "169.254.0.0/16",
            "10.0.0.0/8",
            "169.254.0.0/16",
            " 10.0.0.0/8 "
        ]);

        Assert.Equal(["169.254.0.0/16", "10.0.0.0/8"], guard.Networks.Select(network => network.ToString()));
        Assert.Empty(guard.Problems);
    }

    [Fact]
    public void Constructor_ReportsBadEntriesInsteadOfUsingThem()
    {
        var guard = new EgressNetworkGuard(["nonsense", "10.0.0.1/99", "", null, "169.254.0.0/16"]);

        Assert.Equal(["169.254.0.0/16"], guard.Networks.Select(network => network.ToString()));
        Assert.Equal(4, guard.Problems.Count);
        Assert.Contains(guard.Problems, problem => problem.Contains("'nonsense'"));
        Assert.Contains(guard.Problems, problem => problem.Contains("'10.0.0.1/99'"));
        Assert.Equal(2, guard.Problems.Count(problem => problem == "An empty entry was ignored."));
    }

    [Fact]
    public void Constructor_RefusesANullList() =>
        Assert.Throws<ArgumentNullException>(() => new EgressNetworkGuard(null!));

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("8.8.8.8")]
    public void IsBlocked_BlocksNothingWhenNoNetworkIsConfigured(string address)
    {
        var guard = new EgressNetworkGuard([]);

        Assert.Empty(guard.Networks);
        Assert.False(guard.IsBlocked(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsBlocked_MatchesAnIpv4MappedAddressAgainstAnIpv4Network()
    {
        IPNetwork[] networks = [IPNetwork.Parse("10.0.0.0/8")];

        Assert.True(EgressNetworkGuard.IsBlocked(IPAddress.Parse("::ffff:10.1.2.3"), networks));
        Assert.False(EgressNetworkGuard.IsBlocked(IPAddress.Parse("::ffff:11.1.2.3"), networks));
    }

    [Fact]
    public void Normalize_UnwrapsAnIpv4MappedAddress()
    {
        var normalized = EgressNetworkGuard.Normalize(IPAddress.Parse("::ffff:169.254.169.254"));

        Assert.Equal(IPAddress.Parse("169.254.169.254"), normalized);
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, normalized.AddressFamily);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    public void Normalize_LeavesAnyOtherAddressAlone(string text)
    {
        var address = IPAddress.Parse(text);

        Assert.Same(address, EgressNetworkGuard.Normalize(address));
    }

    [Fact]
    public void EnsureAllowed_ThrowsWhenAnyOneAddressIsBlocked()
    {
        var blocked = IPAddress.Parse("169.254.169.254");

        var exception = Assert.Throws<EgressBlockedException>(() => DefaultGuard().EnsureAllowed(
            "metadata.example",
            [IPAddress.Parse("8.8.8.8"), blocked, IPAddress.Parse("10.0.0.1")]));

        Assert.Equal("metadata.example", exception.Host);
        Assert.Equal(blocked, exception.BlockedAddress);
        Assert.Contains("metadata.example", exception.Message);
        Assert.Contains("169.254.169.254", exception.Message);
    }

    [Fact]
    public void EnsureAllowed_PassesWhenEveryAddressIsAllowed()
    {
        DefaultGuard().EnsureAllowed(
            "example.com",
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:db8::1"), IPAddress.Parse("127.0.0.1")]);
    }

    [Fact]
    public void EnsureAllowed_PassesForNoAddresses() =>
        DefaultGuard().EnsureAllowed("example.com", []);
}

/// <summary>The exception raised for a refused destination.</summary>
public sealed class EgressBlockedExceptionTests
{
    [Fact]
    public void Constructor_CarriesTheHostAndAddressAndNamesTheSetting()
    {
        var address = IPAddress.Parse("168.63.129.16");

        var exception = new EgressBlockedException("wireserver", address);

        Assert.IsAssignableFrom<IOException>(exception);
        Assert.Equal("wireserver", exception.Host);
        Assert.Same(address, exception.BlockedAddress);
        Assert.Contains("wireserver", exception.Message);
        Assert.Contains("168.63.129.16", exception.Message);
        Assert.Contains("DevConduit:BlockedEgressNetworks", exception.Message);
    }
}
