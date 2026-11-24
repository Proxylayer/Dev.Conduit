using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// Where the Data Protection key ring lives and how it is protected, from a host built with
/// a throwaway data root. Keys are generated and used for real, but only on local disk.
/// </summary>
public sealed class DataProtectionSetupTests
{
    private sealed class ProtectedHost : IAsyncDisposable
    {
        private readonly TempDirectory _root;

        private ProtectedHost(TempDirectory root, DataProtectionSetup setup, WebApplication app)
        {
            _root = root;
            Setup = setup;
            App = app;
        }

        public DataProtectionSetup Setup { get; }

        public WebApplication App { get; }

        public string KeyDirectory => System.IO.Path.Combine(_root.Path, "data", DataProtectionSetup.KeyDirectoryName);

        /// <summary>Builds a host whose content root and data root are both under one temp directory.</summary>
        public static ProtectedHost Build()
        {
            var root = new TempDirectory();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.Path });
            builder.Logging.ClearProviders();

            var setup = DataProtectionSetup.Register(
                builder,
                new DevConduitOptions { DataRoot = System.IO.Path.Combine(root.Path, "data") });

            return new ProtectedHost(root, setup, builder.Build());
        }

        /// <summary>Round-trips a payload, which creates the first key, and returns the key files.</summary>
        public string[] ProtectAndReadKeyFiles()
        {
            var protector = App.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("tests");
            var protectedPayload = protector.Protect("payload");

            Assert.NotEqual("payload", protectedPayload);
            Assert.Equal("payload", protector.Unprotect(protectedPayload));

            return Directory.GetFiles(KeyDirectory, "key-*.xml");
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            _root.Dispose();
        }
    }

    [Fact]
    public async Task Register_FixesTheApplicationDiscriminator()
    {
        await using var host = ProtectedHost.Build();

        var options = host.App.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

        Assert.Equal("Dev.Conduit", options.ApplicationDiscriminator);
        Assert.Equal(DataProtectionSetup.ApplicationName, options.ApplicationDiscriminator);
    }

    [Fact]
    public async Task Register_ReportsTheKeyDirectoryUnderTheDataRoot()
    {
        await using var host = ProtectedHost.Build();

        Assert.Equal(host.KeyDirectory, host.Setup.KeyDirectory);
    }

    [Fact]
    public async Task Protect_PersistsTheKeyUnderTheDataRootsKeysDirectory()
    {
        await using var host = ProtectedHost.Build();

        var keyFile = Assert.Single(host.ProtectAndReadKeyFiles());

        Assert.Equal("key", XDocument.Load(keyFile).Root?.Name.LocalName);
    }

    [Fact]
    public async Task Protect_WritesADpapiProtectedKeyOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI key protection is Windows-only.");
        await using var host = ProtectedHost.Build();

        var key = XDocument.Load(Assert.Single(host.ProtectAndReadKeyFiles()));

        var encryptedSecret = Assert.Single(key.Descendants(), element => element.Name.LocalName == "encryptedSecret");
        Assert.StartsWith(
            "Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlDecryptor",
            (string?)encryptedSecret.Attribute("decryptorType"));

        // The master key travels only inside the DPAPI blob, never as readable XML.
        Assert.DoesNotContain(key.Descendants(), element => element.Name.LocalName == "masterKey");
    }

    [Fact]
    public async Task Register_DescribesMachineScopeDpapiAndARestrictedDirectoryOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI key protection is Windows-only.");
        await using var host = ProtectedHost.Build();

        Assert.Null(host.Setup.AccessWarning);
        Assert.StartsWith("machine-scope DPAPI, directory limited to SYSTEM, Administrators and ", host.Setup.Protection);
        Assert.True(Directory.Exists(host.KeyDirectory));
    }
}
