using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>Which strings are DNS host names, the form they are stored in, and the first label.</summary>
public sealed class DnsHostnameTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("devconduit.example.internal")]
    [InlineData("a")]
    [InlineData("a1.b2.c3")]
    [InlineData("DevConduit.Example.Internal")]
    [InlineData("xn--bcher-kva.example")]
    [InlineData("123.example")]
    public void IsValid_AcceptsDnsNames(string value) =>
        Assert.True(DnsHostname.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading.example")]
    [InlineData("trailing-.example")]
    [InlineData("under_score.example")]
    [InlineData("two..dots")]
    [InlineData(".leading")]
    [InlineData("host.")]
    [InlineData(" host")]
    [InlineData("host ")]
    [InlineData("*.example.com")]
    [InlineData("host:8091")]
    [InlineData("https://host")]
    [InlineData("bücher.example")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("127.0.0.1")]
    public void IsValid_RefusesAnythingElse(string? value) =>
        Assert.False(DnsHostname.IsValid(value));

    [Fact]
    public void IsValid_AcceptsASixtyThreeCharacterLabelButNotSixtyFour()
    {
        Assert.True(DnsHostname.IsValid(new string('a', 63) + ".example"));
        Assert.False(DnsHostname.IsValid(new string('a', 64) + ".example"));
    }

    [Fact]
    public void IsValid_AcceptsTwoHundredFiftyThreeCharactersButNotMore()
    {
        // Four labels of 63 with three dots make 255; trimmed to the limit and one past it.
        var label = new string('a', 63);
        var name = string.Join('.', label, label, label, label);
        Assert.False(DnsHostname.IsValid(name));
        Assert.True(DnsHostname.IsValid(name[..253].TrimEnd('.')));
        Assert.False(DnsHostname.IsValid(name[..254]));
    }

    [Theory]
    [InlineData("DevConduit.Example.Internal", "devconduit.example.internal")]
    [InlineData("  host.example  ", "host.example")]
    [InlineData("host.example.", "host.example")]
    [InlineData(" Host.Example. ", "host.example")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalize_TrimsDropsTheTrailingDotAndLowerCases(string? value, string expected) =>
        Assert.Equal(expected, DnsHostname.Normalize(value));

    [Theory]
    [InlineData("devconduit.example.internal", "devconduit")]
    [InlineData("DevConduit.Example.Internal", "devconduit")]
    [InlineData("localhost", "localhost")]
    [InlineData("Single.", "single")]
    public void FirstLabel_IsTheNormalizedFirstLabel(string hostname, string expected) =>
        Assert.Equal(expected, DnsHostname.FirstLabel(hostname));

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData(" 10.0.0.5 ")]
    [InlineData("::1")]
    [InlineData("host.example")]
    [InlineData("HOST.example.")]
    public void IsAddressOrHostname_AcceptsAnAddressOrAName(string value) =>
        Assert.True(DnsHostname.IsAddressOrHostname(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("host:8091")]
    [InlineData("http://10.0.0.5")]
    [InlineData("under_score")]
    public void IsAddressOrHostname_RefusesAnythingElse(string? value) =>
        Assert.False(DnsHostname.IsAddressOrHostname(value));
}
