using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The Servers page's rows and their plain-words status. Pure: routing, import records, hosts
/// addresses, trust facts and sync statuses are all passed in.
/// </summary>
public sealed class ImportedServerListTests
{
    private const string Host = "devconduit.example.internal";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServerTrustFacts Trusted = new(1, new DateTimeOffset(2027, 5, 31, 23, 0, 0, TimeSpan.Zero), false);

    private static RoutingConfiguration Routing(params (string Key, string? Value)[] devConduit) => TestOptions.ParseRouting(devConduit);

    private static IReadOnlyList<ImportedServerRow> Build(
        RoutingConfiguration routing,
        IReadOnlyList<ProxyConfigImportRecord>? records = null,
        Func<string, string?>? hosts = null,
        Func<string, ServerTrustFacts>? trust = null,
        IReadOnlyList<ClientTrustStatus>? statuses = null) =>
        ImportedServerList.Build(routing, records ?? [], hosts ?? (_ => null), trust ?? (_ => Trusted), statuses ?? []);

    [Fact]
    public void NoRemotesAndNoRecords_IsEmpty() =>
        Assert.Empty(Build(Routing()));

    [Fact]
    public void AnHttpsRemote_IsARowWithItsRoutes()
    {
        var routing = Routing(
            ("Remotes:uat:Endpoint", $"https://{Host.ToUpperInvariant()}:8091"),
            ("Routes:api:Target", "https://api.internal"),
            ("Routes:api:Remote", "UAT"),
            ("Routes:web", "https://web.internal"));

        var row = Assert.Single(Build(routing, hosts: host => host == Host ? "10.0.0.5" : null));

        Assert.Equal("uat", row.Name);
        Assert.Equal(Host, row.Hostname);
        Assert.Equal("10.0.0.5", row.Address);
        Assert.Equal("Trusted until 2027-05-31", row.Certificate);
        Assert.Equal(ImportedServerList.NeverSyncedText, row.LastSync);
        Assert.Equal(2, row.Routes);
        Assert.False(row.TrustUpdateNeeded);
    }

    [Theory]
    [InlineData("http://devconduit.example.internal:8091")]
    [InlineData("https://10.0.0.5:8091")]
    [InlineData("https://[fd00::5]:8091")]
    public void ARemoteThatIsNotHttpsToAHostName_IsNotListed(string endpoint) =>
        Assert.Empty(Build(Routing(("Remotes:x", endpoint))));

    [Fact]
    public void AnImportWhoseRemoteIsGone_IsStillListedWithoutAName()
    {
        var record = new ProxyConfigImportRecord(Host, "uat", ["api"], Now, "10.0.0.5");

        var row = Assert.Single(Build(Routing(), [record]));

        Assert.Equal(string.Empty, row.Name);
        Assert.Equal(Host, row.Hostname);
        Assert.Equal("10.0.0.5", row.Address);
        Assert.Equal(0, row.Routes);
    }

    [Fact]
    public void ARemoteAndItsImportRecord_AreOneRow()
    {
        var routing = Routing(("Remotes:uat", $"https://{Host}:8091"));
        var record = new ProxyConfigImportRecord(Host.ToUpperInvariant(), "uat", [], Now, "10.0.0.9");

        var row = Assert.Single(Build(routing, [record]));

        Assert.Equal("uat", row.Name);
    }

    [Fact]
    public void Address_PrefersTheHostsFileThenTheRecordThenDns()
    {
        var routing = Routing(("Remotes:a", "https://a.example"), ("Remotes:b", "https://b.example"), ("Remotes:c", "https://c.example"));
        ProxyConfigImportRecord[] records =
        [
            new("a.example", "a", [], Now, "10.0.0.1"),
            new("b.example", "b", [], Now, "10.0.0.2"),
            new("c.example", "c", [], Now)
        ];

        var rows = Build(routing, records, hosts: host => host == "a.example" ? "10.9.9.9" : null);

        Assert.Equal(["10.9.9.9", "10.0.0.2", "(resolved by DNS)"], rows.Select(row => row.Address));
    }

    [Fact]
    public void Rows_AreNamedFirstByNameThenUnnamedByHost()
    {
        var routing = Routing(("Remotes:Zeta", "https://z.example"), ("Remotes:alpha", "https://a.example"));
        ProxyConfigImportRecord[] records =
        [
            new("y.example", "gone", [], Now),
            new("b.example", "gone", [], Now)
        ];

        var rows = Build(routing, records);

        Assert.Equal(["a.example", "z.example", "b.example", "y.example"], rows.Select(row => row.Hostname));
    }

    [Fact]
    public void ARecordThatIsNotAHostName_IsSkipped() =>
        Assert.Empty(Build(Routing(), [new ProxyConfigImportRecord("10.0.0.5", "x", [], Now)]));

    // ---- Certificate status --------------------------------------------------------------

    [Fact]
    public void Certificate_WithNothingPinned_IsNotTrusted()
    {
        var row = Assert.Single(Build(Routing(("Remotes:uat", $"https://{Host}")), trust: _ => ServerTrustFacts.None));

        Assert.Equal(ImportedServerList.NotTrustedText, row.Certificate);
        Assert.Equal("Not trusted", row.Certificate);
        Assert.False(row.TrustUpdateNeeded);
    }

    [Fact]
    public void Certificate_PinnedButMissingFromWindowsTrust_IsAnUpdateNeedingAdmin()
    {
        var facts = new ServerTrustFacts(2, Trusted.TrustedUntil, RootStoreUpdateNeeded: true);

        var row = Assert.Single(Build(Routing(("Remotes:uat", $"https://{Host}")), trust: _ => facts));

        Assert.Equal("Update ready (needs admin)", row.Certificate);
        Assert.True(row.TrustUpdateNeeded);
    }

    [Fact]
    public void Certificate_TrustedButTheMachineIdentityIsDue_IsAnUpdateNeedingAdmin()
    {
        var facts = Trusted with { MachineIdentityRenewalDue = true };

        var row = Assert.Single(Build(Routing(("Remotes:uat", $"https://{Host}")), trust: _ => facts));

        Assert.Equal("Update ready (needs admin)", row.Certificate);
        Assert.True(row.TrustUpdateNeeded);
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(1, true, false, true)]
    [InlineData(1, false, true, true)]
    [InlineData(1, true, true, true)]
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, true)]
    public void UpdateNeeded_IsWindowsTrustForAValidPinOrAMachineIdentityRenewal(int pinned, bool rootUpdate, bool renewalDue, bool expected) =>
        Assert.Equal(expected, new ServerTrustFacts(pinned, null, rootUpdate, renewalDue).UpdateNeeded);

    [Fact]
    public void Certificate_TrustFactsAreAskedForTheNormalizedHost()
    {
        var asked = new List<string>();

        Build(Routing(("Remotes:uat", $"https://{Host.ToUpperInvariant()}")), trust: host => { asked.Add(host); return Trusted; });

        Assert.Equal([Host], asked);
    }

    [Theory]
    [InlineData(0, null, false, "Not trusted")]
    [InlineData(0, "2027-01-01", true, "Not trusted")]
    [InlineData(1, null, false, "Not trusted")]
    [InlineData(1, null, true, "Update ready (needs admin)")]
    [InlineData(1, "2027-01-01", true, "Update ready (needs admin)")]
    [InlineData(1, "2027-01-01", false, "Trusted until 2027-01-01")]
    public void DescribeCertificate_SaysWhetherItIsTrusted(int pinned, string? until, bool update, string expected)
    {
        var facts = new ServerTrustFacts(pinned, until is null ? null : DateTimeOffset.Parse(until + "T00:00:00Z"), update);

        Assert.Equal(expected, ImportedServerList.DescribeCertificate(facts, update));
    }

    [Fact]
    public void DescribeCertificate_UsesTheUtcDate() =>
        Assert.Equal(
            "Trusted until 2027-01-01",
            ImportedServerList.DescribeCertificate(new ServerTrustFacts(1, new DateTimeOffset(2027, 1, 1, 23, 30, 0, TimeSpan.Zero), false), false));

    // ---- Sync status ---------------------------------------------------------------------

    [Fact]
    public void DescribeSync_WithoutAStatus_IsNeverSynced()
    {
        Assert.Equal("Not since the local proxy started", ImportedServerList.DescribeSync(null));
        Assert.Equal(ImportedServerList.NeverSyncedText, ImportedServerList.DescribeSync(new ClientTrustStatus(Host, 0, false, null, null, null)));
        Assert.Equal(ImportedServerList.NeverSyncedText, ImportedServerList.DescribeSync(new ClientTrustStatus(Host, 0, false, null, null, "")));
    }

    [Fact]
    public void DescribeSync_ShowsTheLastSyncInLocalTime() =>
        Assert.Equal(
            Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ImportedServerList.DescribeSync(new ClientTrustStatus(Host, 1, false, null, Now, null)));

    [Fact]
    public void DescribeSync_ShowsAFailureAfterAnEarlierSuccess() =>
        Assert.Equal(
            $"{Now.ToLocalTime():yyyy-MM-dd HH:mm}; the last try failed: timed out",
            ImportedServerList.DescribeSync(new ClientTrustStatus(Host, 1, false, null, Now, "timed out")));

    [Fact]
    public void DescribeSync_ShowsAFailureWithNoSuccessYet() =>
        Assert.Equal("Failed: refused", ImportedServerList.DescribeSync(new ClientTrustStatus(Host, 1, false, null, null, "refused")));

    [Fact]
    public void Rows_PickTheStatusForTheirHost()
    {
        var routing = Routing(("Remotes:a", "https://a.example"), ("Remotes:b", "https://b.example"));
        ClientTrustStatus[] statuses = [new("B.EXAMPLE", 1, false, null, null, "boom")];

        var rows = Build(routing, statuses: statuses);

        Assert.Equal([ImportedServerList.NeverSyncedText, "Failed: boom"], rows.Select(row => row.LastSync));
    }

    [Fact]
    public void Build_RefusesNullInputs()
    {
        var routing = Routing();
        Assert.Throws<ArgumentNullException>(() => ImportedServerList.Build(null!, [], _ => null, _ => Trusted, []));
        Assert.Throws<ArgumentNullException>(() => ImportedServerList.Build(routing, null!, _ => null, _ => Trusted, []));
        Assert.Throws<ArgumentNullException>(() => ImportedServerList.Build(routing, [], null!, _ => Trusted, []));
        Assert.Throws<ArgumentNullException>(() => ImportedServerList.Build(routing, [], _ => null, null!, []));
        Assert.Throws<ArgumentNullException>(() => ImportedServerList.Build(routing, [], _ => null, _ => Trusted, null!));
    }
}
