using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The server trusting its own current and next certificates, and taking out the ones it added
/// once they are replaced, expire or the host name changes, never anything it did not add. The
/// current user's AddressBook store stands in for <c>LocalMachine\Root</c>; whatever lands in it
/// is removed afterwards.
/// </summary>
public sealed class ServerSelfTrustTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const StoreName ScratchStore = StoreName.AddressBook;
    private const StoreLocation ScratchLocation = StoreLocation.CurrentUser;

    private readonly TempDirectory _root = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly List<X509Certificate2> _certificates = [];
    private readonly RecordingLogger _logger = new();

    public void Dispose()
    {
        using (var store = new X509Store(ScratchStore, ScratchLocation))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in _certificates)
            {
                foreach (var match in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false))
                {
                    store.Remove(match);
                    match.Dispose();
                }

                certificate.Dispose();
            }
        }

        _root.Dispose();
    }

    private string RecordPath => Path.Combine(_root.Path, "certs", ServerSelfTrust.RecordFileName);

    private ServerSelfTrust Trust() => new(new RootStoreTrust(RecordPath, ScratchStore, ScratchLocation), _logger);

    private X509Certificate2 Server(string hostname = Host, int daysFromNow = 0) =>
        Track(CertificateFactory.CreateServerCertificate(hostname, _now.AddDays(daysFromNow)));

    private X509Certificate2 Track(X509Certificate2 certificate)
    {
        _certificates.Add(certificate);
        return certificate;
    }

    private ServerCertificateSet Set(X509Certificate2 current, X509Certificate2? next = null, string hostname = Host) =>
        new(hostname, current, next, Track(CertificateFactory.CreateClientAuthority(hostname, _now)));

    private static bool InStore(X509Certificate2 certificate)
    {
        using var store = new X509Store(ScratchStore, ScratchLocation);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
        var present = found.Count > 0;
        foreach (var match in found)
        {
            match.Dispose();
        }

        return present;
    }

    private static void AddToStore(X509Certificate2 certificate)
    {
        using var store = new X509Store(ScratchStore, ScratchLocation);
        store.Open(OpenFlags.ReadWrite);
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        store.Add(publicOnly);
    }

    private IReadOnlyList<string> Recorded() =>
        new RootStoreTrust(RecordPath, ScratchStore, ScratchLocation).Added().Select(entry => entry.Thumbprint).ToList();

    // ---- Synchronize ----------------------------------------------------------------------

    [Fact]
    public void Synchronize_TrustsTheCurrentAndNextCertificatesAndRecordsThem()
    {
        var current = Server();
        var next = Server(daysFromNow: 300);

        var change = Trust().Synchronize(Set(current, next), _now);

        Assert.NotNull(change);
        Assert.Equal([current.Thumbprint, next.Thumbprint], change.Added);
        Assert.True(InStore(current));
        Assert.True(InStore(next));
        Assert.Equal([current.Thumbprint, next.Thumbprint], Recorded());
    }

    [Fact]
    public void Synchronize_RemovesTheReplacedCertificateWhenTheNextIsPromoted()
    {
        var old = Server();
        var promoted = Server(daysFromNow: 300);
        var newer = Server(daysFromNow: 600);
        var trust = Trust();
        trust.Synchronize(Set(old, promoted), _now);

        var change = trust.Synchronize(Set(promoted, newer), _now);

        Assert.NotNull(change);
        Assert.Equal([old.Thumbprint], change.Removed);
        Assert.Equal([newer.Thumbprint], change.Added);
        Assert.False(InStore(old));
        Assert.True(InStore(promoted));
        Assert.True(InStore(newer));
        Assert.Equal([promoted.Thumbprint, newer.Thumbprint], Recorded());
    }

    [Fact]
    public void Synchronize_RemovesTheOldHostNamesCertificatesWhenTheHostNameChanges()
    {
        var old = Server();
        var renamed = Server("workstation01");
        var trust = Trust();
        trust.Synchronize(Set(old), _now);

        var change = trust.Synchronize(Set(renamed, hostname: "workstation01"), _now);

        Assert.Equal([old.Thumbprint], change!.Removed);
        Assert.False(InStore(old));
        Assert.True(InStore(renamed));
    }

    [Fact]
    public void Synchronize_RemovesAnExpiredCertificateItAdded()
    {
        var current = Server();
        var trust = Trust();
        trust.Synchronize(Set(current), _now);

        var replacement = Server(daysFromNow: 366);
        var change = trust.Synchronize(Set(replacement), _now.AddDays(366));

        Assert.Contains(current.Thumbprint, change!.Removed);
        Assert.False(InStore(current));
    }

    [Fact]
    public void Synchronize_NeverRemovesACertificateItDidNotAdd()
    {
        var foreign = Server();
        AddToStore(foreign);
        var trust = Trust();

        var first = trust.Synchronize(Set(foreign), _now);
        trust.Synchronize(Set(Server()), _now);

        Assert.Equal([foreign.Thumbprint], first!.AlreadyPresent);
        Assert.True(InStore(foreign));
        Assert.DoesNotContain(foreign.Thumbprint, Recorded());
    }

    [Fact]
    public void Synchronize_AddsNothingThatFailsTheServerCertificateRules()
    {
        var authority = Track(CertificateFactory.CreateClientAuthority(Host, _now));
        var wrongName = Server("other.devconduit.internal");

        Assert.Null(Trust().Synchronize(Set(authority), _now));
        Assert.Null(Trust().Synchronize(Set(wrongName), _now));

        Assert.False(InStore(authority));
        Assert.False(InStore(wrongName));
        Assert.Equal(2, _logger.Count(LogLevel.Error));
    }

    [Fact]
    public void Synchronize_WarnsOnceAndCarriesOnWhenTheStoreCannotBeWritten()
    {
        var denied = new RootStoreTrust(RecordPath, @"LocalMachine\Root", _ => throw new CryptographicException("Access is denied."));
        var trust = new ServerSelfTrust(denied, _logger);

        Assert.Null(trust.Synchronize(Set(Server()), _now));
        Assert.Null(trust.Synchronize(Set(Server()), _now));

        var warning = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(@"LocalMachine\Root", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", warning.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void Synchronize_DoesNothingWhenSelfTrustIsOff()
    {
        var current = Server();

        Assert.Null(new ServerSelfTrust(null, _logger).Synchronize(Set(current), _now));
        Assert.False(InStore(current));
    }

    // ---- RemoveAll and certs untrust -------------------------------------------------------

    [Fact]
    public void RemoveAll_RemovesOnlyWhatWasAdded()
    {
        var foreign = Server();
        AddToStore(foreign);
        var added = Server(daysFromNow: 1);
        var trust = Trust();
        trust.Synchronize(Set(foreign, added), _now);

        var removed = trust.RemoveAll();

        Assert.Equal([added.Thumbprint], removed);
        Assert.False(InStore(added));
        Assert.True(InStore(foreign));
        Assert.Empty(Recorded());
    }

    [Fact]
    public void Untrust_RemovesTheServersCertificatesFromTheStoreItsSettingsName()
    {
        using var layout = new SettingsLayout();
        var dataRoot = Path.Combine(_root.Path, "data");
        var store = ServerCertificateStore.ForDataRoot(dataRoot);
        var current = Server();
        new ServerSelfTrust(new RootStoreTrust(ServerSelfTrust.RecordPath(store), ScratchStore, ScratchLocation), _logger)
            .Synchronize(Set(current), _now);
        using var output = new StringWriter();

        var exitCode = CertsCommand.Untrust(
            [$"--DevConduit:DataRoot={dataRoot}", $@"--{ServerSelfTrust.StoreKey}=CurrentUser\AddressBook"], layout.Files, output);

        Assert.Equal(ServiceAdminExitCode.Success, exitCode);
        Assert.False(InStore(current));
        Assert.Contains(current.Thumbprint, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Untrust_SaysSoWhenNothingWasAdded()
    {
        using var layout = new SettingsLayout();
        using var output = new StringWriter();

        var exitCode = CertsCommand.Untrust(
            [$"--DevConduit:DataRoot={Path.Combine(_root.Path, "empty")}", $@"--{ServerSelfTrust.StoreKey}=CurrentUser\AddressBook"], layout.Files, output);

        Assert.Equal(ServiceAdminExitCode.Success, exitCode);
        Assert.Contains("no certificates", output.ToString(), StringComparison.Ordinal);
    }

    // ---- The store setting -----------------------------------------------------------------

    [Theory]
    [InlineData(null, StoreLocation.LocalMachine, StoreName.Root)]
    [InlineData("", StoreLocation.LocalMachine, StoreName.Root)]
    [InlineData(@"LocalMachine\Root", StoreLocation.LocalMachine, StoreName.Root)]
    [InlineData(@"currentuser\addressbook", StoreLocation.CurrentUser, StoreName.AddressBook)]
    public void TryParseStore_ReadsLocationAndName(string? setting, StoreLocation location, StoreName name)
    {
        Assert.True(ServerSelfTrust.TryParseStore(setting, out var parsedLocation, out var parsedName, out _));
        Assert.Equal(location, parsedLocation);
        Assert.Equal(name, parsedName);
    }

    [Fact]
    public void TryParseStore_ReadsNoneAsOff()
    {
        Assert.True(ServerSelfTrust.TryParseStore("none", out var location, out _, out _));
        Assert.Null(location);
    }

    [Theory]
    [InlineData(@"CurrentUser\Root")]
    [InlineData(@"Nowhere\Root")]
    [InlineData("LocalMachine")]
    [InlineData(@"LocalMachine\Root\Extra")]
    [InlineData(@"LocalMachine\7")]
    [InlineData(@"2\Root")]
    public void TryParseStore_RefusesAnythingElse(string setting)
    {
        Assert.False(ServerSelfTrust.TryParseStore(setting, out _, out _, out var problem));
        Assert.Contains(ServerSelfTrust.StoreKey, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_TurnsSelfTrustOffAndLogsWhenTheSettingIsUnusable()
    {
        var trust = ServerSelfTrust.Create(ServerCertificateStore.ForDataRoot(_root.Path), @"CurrentUser\Root", _logger);

        Assert.Null(trust.StoreDescription);
        Assert.Equal(1, _logger.Count(LogLevel.Error));
    }

    // ---- The certificate manager -----------------------------------------------------------

    [Fact]
    public void Manager_TrustsTheCertificateItServes()
    {
        var store = ServerCertificateStore.ForDataRoot(Path.Combine(_root.Path, "data"));
        var authority = new ServerCertificateAuthority(store, new ManualClock(_now), NullLogger.Instance);
        var trust = new ServerSelfTrust(new RootStoreTrust(ServerSelfTrust.RecordPath(store), ScratchStore, ScratchLocation), _logger);
        var options = Options.Create(new DevConduitOptions { Server = new ServerHostOptions { Hostname = Host } });
        var manager = new ServerCertificateManager(authority, trust, options, new ManualClock(_now), NullLogger<ServerCertificateManager>.Instance);

        var served = manager.Certificates.Current;
        _certificates.Add(X509CertificateLoader.LoadCertificate(served.RawData));

        Assert.True(InStore(served));
        Assert.Contains(served.Thumbprint, new RootStoreTrust(ServerSelfTrust.RecordPath(store), ScratchStore, ScratchLocation).Added().Select(entry => entry.Thumbprint));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public int Count(LogLevel level) => Entries.Count(entry => entry.Level == level);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
