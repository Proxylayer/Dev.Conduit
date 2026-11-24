using System.Security.Cryptography.X509Certificates;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>
/// The setup window's pages saving one after another over the same files, driven the way the
/// window drives them: every page loads when the window opens, edits are made, a page saves
/// against the file as it is at that moment, and then every page reloads, the page that saved
/// taking the files as they are and every other page keeping its unsaved edits. Saving one page
/// must never lose another page's settings, saved or still being edited. Machine saves go
/// through the transfer file and its allow-list as the elevated helper applies them.
/// </summary>
public sealed class SetupSaveSequenceTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Client = "66666666-7777-8888-9999-000000000000";
    private const string UserOid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string ImportHost = "vm-b.devconduit.internal";

    private const string ServerMachine = $$"""
        {
          "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
          "DevConduit": {
            "Server": { "Hostname": "vm-a.devconduit.internal", "ClientAddress": "10.0.0.1" },
            "Routes": { "web": "http://web.internal", "api": { "Target": "http://api.internal" } }
          }
        }
        """;

    private const string WorkstationMachine = $$"""
        {
          "AzureAd": { "TenantId": "{{Tenant}}", "ClientId": "{{Client}}" },
          "DevConduit": {
            "Remotes": { "vm-a": { "Endpoint": "https://vm-a.devconduit.internal:8091" } },
            "Routes": { "web": { "Target": "http://web.internal", "Remote": "vm-a" } }
          }
        }
        """;

    private const string WorkstationUser = """
        { "DevConduit": { "Local": { "ForwardProxyPort": 8193 } } }
        """;

    public static TheoryData<string, string, bool> ServerPairs() => Pairs(["Server", "Routes"]);

    public static TheoryData<string, string, bool> WorkstationPairs() => Pairs(["Servers", "Routes", "LocalProxy"]);

    // ---- Server role: Server and Routes, both on the machine file --------------------------

    [Theory]
    [MemberData(nameof(ServerPairs))]
    public void ServerRole_SavingOnePageKeepsTheOther(string first, string second, bool editBothFirst)
    {
        using var layout = new SettingsLayout().Machine(ServerMachine);
        var window = new Window(layout, [new ServerPage(), new RoutesPage(SettingsTarget.Machine)]);

        window.Run(first, second, editBothFirst);

        foreach (var page in window.Pages)
        {
            page.AssertSaved(layout);
        }

        // What neither page edits is still there.
        var machine = layout.View(SettingsTarget.Machine).Effective;
        Assert.Equal(Tenant, machine[ManagedSettingKeys.TenantId]);
        Assert.Equal("http://api.internal", machine[$"{ManagedSettingKeys.Routes}:api:Target"]);
    }

    // ---- Workstation role: Servers (machine file), Routes and Local proxy (user file) -------

    [Theory]
    [MemberData(nameof(WorkstationPairs))]
    public void WorkstationRole_SavingOnePageKeepsTheOther(string first, string second, bool editBothFirst)
    {
        using var layout = new SettingsLayout().Machine(WorkstationMachine).User(WorkstationUser);
        using var import = new ServersPage();
        var window = new Window(layout, [import, new RoutesPage(SettingsTarget.User), new LocalProxyPage()]);

        window.Run(first, second, editBothFirst);

        foreach (var page in window.Pages.Where(page => page.Name == first || page.Name == second))
        {
            page.AssertSaved(layout);
        }

        var user = layout.View(SettingsTarget.User).Effective;
        Assert.Equal("8193", user[ManagedSettingKeys.LocalForwardProxyPort]);
        Assert.Equal("https://vm-a.devconduit.internal:8091", user[$"{ManagedSettingKeys.Remotes}:vm-a:Endpoint"]);
    }

    // ---- Each page's change touches only its own keys ---------------------------------------

    [Fact]
    public void ServerSettingsModel_ChangesOnlyServerKeys()
    {
        using var layout = new SettingsLayout().Machine(ServerMachine);
        var model = ServerSettingsModel.Load(layout.View(SettingsTarget.Machine));
        model.Hostname = string.Empty;
        model.ClientAddress = string.Empty;
        model.TenantId = string.Empty;
        model.ClientId = string.Empty;

        var keys = model.ToChanges().Keys.ToList();

        Assert.All(keys, key => Assert.Contains(key, ServerKeys));
        Assert.DoesNotContain(keys, key => key.StartsWith(ManagedSettingKeys.Routes, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SettingsTarget.Machine)]
    [InlineData(SettingsTarget.User)]
    public void RoutingEditModel_ChangesOnlyTheRoutes(SettingsTarget target)
    {
        using var layout = new SettingsLayout().Machine(target == SettingsTarget.Machine ? ServerMachine : WorkstationMachine).User(WorkstationUser);
        var model = RoutingEditModel.Load(layout.View(target));
        model.Routes.Clear();

        Assert.Equal([ManagedSettingKeys.Routes], model.ToChanges().Keys);
    }

    [Fact]
    public void LocalProxySettingsModel_ChangesOnlyLocalKeys()
    {
        using var layout = new SettingsLayout().Machine(WorkstationMachine).User(WorkstationUser);
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));
        model.Port = 9123;
        model.ForwardProxyEnabled = false;

        Assert.All(model.ToChanges().Keys, key => Assert.StartsWith("DevConduit:Local:", key, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Reload keeps unsaved edits ---------------------------------------------------------

    [Fact]
    public void ServerSettingsModel_ReloadKeepsEditedFieldsAndTakesTheRestFromTheFile()
    {
        using var layout = new SettingsLayout().Machine(ServerMachine);
        var model = ServerSettingsModel.Load(layout.View(SettingsTarget.Machine));
        model.ClientAddress = "10.9.9.9";

        layout.Machine(ServerMachine.Replace("vm-a.devconduit", "vm-z.devconduit", StringComparison.Ordinal));
        var reloaded = model.Reload(layout.View(SettingsTarget.Machine));

        Assert.Equal("10.9.9.9", reloaded.ClientAddress);
        Assert.Equal("vm-z.devconduit.internal", reloaded.Hostname);
        Assert.True(reloaded.HasEdits);
        Assert.False(ServerSettingsModel.Load(layout.View(SettingsTarget.Machine)).HasEdits);
    }

    [Fact]
    public void RoutingEditModel_ReloadKeepsAddedChangedAndDeletedRows()
    {
        using var layout = new SettingsLayout().Machine(ServerMachine);
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.Machine));
        model.Routes.Add(new RouteRow { Name = "added", Target = "http://added.internal" });
        model.Routes.Single(row => row.Name == "web").Target = "http://web2.internal";
        model.Routes.Remove(model.Routes.Single(row => row.Name == "api"));

        // Meanwhile another save added a route of its own.
        layout.Machine(ServerMachine.Replace("\"web\":", "\"other\": \"http://other.internal\", \"web\":", StringComparison.Ordinal));
        var reloaded = model.Reload(layout.View(SettingsTarget.Machine));

        Assert.Equal(["other", "web", "added"], reloaded.Routes.Select(row => row.Name));
        Assert.Equal("http://web2.internal", reloaded.Routes.Single(row => row.Name == "web").Target);
        Assert.True(reloaded.HasEdits);
    }

    [Fact]
    public void RoutingEditModel_ReloadWithoutEditsIsTheFile()
    {
        using var layout = new SettingsLayout().Machine(ServerMachine);
        var model = RoutingEditModel.Load(layout.View(SettingsTarget.Machine));

        layout.Machine(ServerMachine.Replace("http://web.internal", "http://web3.internal", StringComparison.Ordinal));
        var reloaded = model.Reload(layout.View(SettingsTarget.Machine));

        Assert.Equal("http://web3.internal", reloaded.Routes.Single(row => row.Name == "web").Target);
        Assert.False(reloaded.HasEdits);
    }

    [Fact]
    public void LocalProxySettingsModel_ReloadKeepsEditedFields()
    {
        using var layout = new SettingsLayout().Machine(WorkstationMachine).User(WorkstationUser);
        var model = LocalProxySettingsModel.Load(layout.View(SettingsTarget.User));
        model.CaptureEnabled = true;

        layout.User("""{ "DevConduit": { "Local": { "ForwardProxyPort": 8193, "Port": 9200 } } }""");
        var reloaded = model.Reload(layout.View(SettingsTarget.User));

        Assert.True(reloaded.CaptureEnabled);
        Assert.Equal(9200, reloaded.Port);
        Assert.True(reloaded.HasEdits);
    }

    // ---- The window, as the setup window drives its pages ----------------------------------

    private static readonly string[] ServerKeys =
    [
        ManagedSettingKeys.ClientCredentials, ManagedSettingKeys.ClientId, ManagedSettingKeys.TenantId, ManagedSettingKeys.Scopes,
        ManagedSettingKeys.AllowedGroupIds, ManagedSettingKeys.AllowedUserOids, ManagedSettingKeys.AllowedTargets,
        ManagedSettingKeys.ServerClientAddress, ManagedSettingKeys.ServerHostname, ManagedSettingKeys.ServerPort
    ];

    /// <summary>Every ordered pair of distinct pages, with the edits made all at once or one page at a time.</summary>
    private static TheoryData<string, string, bool> Pairs(string[] pages)
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var first in pages)
        {
            foreach (var second in pages.Where(page => page != first))
            {
                data.Add(first, second, true);
                data.Add(first, second, false);
            }
        }

        return data;
    }

    private sealed class Window(SettingsLayout layout, IReadOnlyList<Page> pages)
    {
        public IReadOnlyList<Page> Pages => pages;

        public void Run(string first, string second, bool editBothFirst)
        {
            var a = pages.Single(page => page.Name == first);
            var b = pages.Single(page => page.Name == second);

            Reload(saved: null);
            a.Edit();
            if (editBothFirst)
            {
                b.Edit();
            }

            Save(a);
            if (!editBothFirst)
            {
                b.Edit();
            }

            Save(b);
        }

        private void Save(Page page)
        {
            page.Save(layout);
            Reload(page);
        }

        private void Reload(Page? saved)
        {
            var layers = layout.Layers();
            foreach (var page in pages)
            {
                page.Load(layers, keepEdits: !ReferenceEquals(page, saved));
            }
        }
    }

    private abstract class Page
    {
        public abstract string Name { get; }

        public abstract void Load(SettingsLayers layers, bool keepEdits);

        public abstract void Edit();

        public abstract void Save(SettingsLayout layout);

        public abstract void AssertSaved(SettingsLayout layout);

        /// <summary>As the setup window saves: the allow-list, then the user file directly or the machine file through a transfer file.</summary>
        protected static void Write(SettingsLayout layout, SettingsTarget target, SettingsChangeSet changes)
        {
            if (target == SettingsTarget.User)
            {
                Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.User));
                new SettingsFileWriter(layout.UserPath).Apply(changes);
                return;
            }

            Assert.Empty(ManagedSettingKeys.Unmanaged(changes, ManagedSettingKeys.Machine));
            var transfer = MachineSettingsTransfer.WriteTempFile(changes, layout.Root);
            try
            {
                Assert.True(MachineSettingsTransfer.TryRead(transfer, ManagedSettingKeys.Machine, out var parsed, out var error), error);
                new SettingsFileWriter(layout.MachinePath).Apply(parsed);
            }
            finally
            {
                MachineSettingsTransfer.Delete(transfer);
            }
        }
    }

    private sealed class ServerPage : Page
    {
        private ServerSettingsModel? _model;
        private bool _edited;

        public override string Name => "Server";

        public override void Load(SettingsLayers layers, bool keepEdits)
        {
            var view = layers.View(SettingsTarget.Machine);
            _model = keepEdits && _model is not null ? _model.Reload(view) : ServerSettingsModel.Load(view);
        }

        public override void Edit()
        {
            _model!.ClientAddress = "10.0.0.99";
            _model.AllowedUserOids = UserOid;
            _model.Port = 9443;
            _edited = true;
        }

        public override void Save(SettingsLayout layout) =>
            Write(layout, SettingsTarget.Machine, _model!.Reload(layout.View(SettingsTarget.Machine)).ToChanges());

        public override void AssertSaved(SettingsLayout layout)
        {
            var machine = layout.View(SettingsTarget.Machine).Effective;
            Assert.True(_edited);
            Assert.Equal("10.0.0.99", machine[ManagedSettingKeys.ServerClientAddress]);
            Assert.Equal("9443", machine[ManagedSettingKeys.ServerPort]);
            Assert.Equal(UserOid, machine[$"{ManagedSettingKeys.AllowedUserOids}:0"]);
            Assert.Equal("vm-a.devconduit.internal", machine[ManagedSettingKeys.ServerHostname]);
        }
    }

    private sealed class RoutesPage(SettingsTarget target) : Page
    {
        private RoutingEditModel? _model;

        public override string Name => "Routes";

        public override void Load(SettingsLayers layers, bool keepEdits)
        {
            var view = layers.View(target);
            _model = keepEdits && _model is not null ? _model.Reload(view) : RoutingEditModel.Load(view);
        }

        public override void Edit()
        {
            _model!.Routes.Add(new RouteRow { Name = "added", Target = "http://added.internal" });
            var web = _model.Routes.Single(row => row.Name == "web");
            if (target == SettingsTarget.Machine)
            {
                web.Target = "http://web2.internal";
            }
            else
            {
                web.Enabled = false;
            }
        }

        public override void Save(SettingsLayout layout)
        {
            var model = _model!.Reload(layout.View(target));
            Assert.Empty(model.RowProblems());
            Write(layout, target, model.ToChanges());
        }

        public override void AssertSaved(SettingsLayout layout)
        {
            var effective = layout.View(target).Effective;
            Assert.Equal("http://added.internal", effective[$"{ManagedSettingKeys.Routes}:added:Target"] ?? effective[$"{ManagedSettingKeys.Routes}:added"]);
            if (target == SettingsTarget.Machine)
            {
                Assert.Equal("http://web2.internal", effective[$"{ManagedSettingKeys.Routes}:web:Target"] ?? effective[$"{ManagedSettingKeys.Routes}:web"]);
            }
            else
            {
                Assert.Equal("False", effective[$"{ManagedSettingKeys.Routes}:web:Enabled"]);
            }
        }
    }

    private sealed class LocalProxyPage : Page
    {
        private LocalProxySettingsModel? _model;

        public override string Name => "LocalProxy";

        public override void Load(SettingsLayers layers, bool keepEdits)
        {
            var view = layers.View(SettingsTarget.User);
            _model = keepEdits && _model is not null ? _model.Reload(view) : LocalProxySettingsModel.Load(view);
        }

        public override void Edit()
        {
            _model!.Port = 9123;
            _model.CaptureEnabled = true;
        }

        public override void Save(SettingsLayout layout)
        {
            var model = _model!.Reload(layout.View(SettingsTarget.User));
            Assert.Empty(model.Validate());
            Write(layout, SettingsTarget.User, model.ToChanges());
        }

        public override void AssertSaved(SettingsLayout layout)
        {
            var user = layout.View(SettingsTarget.User).Effective;
            Assert.Equal("9123", user[ManagedSettingKeys.LocalPort]);
            Assert.Equal("True", user[ManagedSettingKeys.LocalCaptureEnabled]);
        }
    }

    /// <summary>The Servers page: nothing to type, its save is importing a .conduit, which writes the machine file.</summary>
    private sealed class ServersPage : Page, IDisposable
    {
        private readonly X509Certificate2 _certificate = CertificateFactory.CreateServerCertificate(ImportHost, TestCertificates.Now);

        public override string Name => "Servers";

        public void Dispose() => _certificate.Dispose();

        public override void Load(SettingsLayers layers, bool keepEdits)
        {
        }

        public override void Edit()
        {
        }

        public override void Save(SettingsLayout layout)
        {
            var config = new ValidatedProxyConfig(
                ImportHost, "10.0.0.7", true, 8091, "vm-b", Tenant, Client,
                [new("billing", "http://billing.internal")],
                [_certificate], [], _certificate);
            var writer = new SettingsFileWriter(layout.MachinePath);
            var settings = ProxyConfigSettingsMerge.Compute(writer.Read(), config, []);
            Assert.Empty(ManagedSettingKeys.Unmanaged(settings.Changes, ManagedSettingKeys.Imported));
            writer.Apply(settings.Changes);
        }

        public override void AssertSaved(SettingsLayout layout)
        {
            var user = layout.View(SettingsTarget.User).Effective;
            Assert.Equal($"https://{ImportHost}:8091", user[$"{ManagedSettingKeys.Remotes}:vm-b:Endpoint"]);
            Assert.Equal("vm-b", user[$"{ManagedSettingKeys.Routes}:billing:Remote"]);
        }
    }
}
