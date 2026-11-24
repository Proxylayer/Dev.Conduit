using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>Which command lines go to the Windows service host rather than the console run.</summary>
public sealed class ServiceLaunchTests
{
    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("help")]
    [InlineData("INSTALL")]
    [InlineData("Uninstall")]
    [InlineData("sTaRt")]
    [InlineData("STOP")]
    [InlineData("Help")]
    public void IsServiceVerb_AcceptsEachLifecycleVerbInAnyCase(string verb)
    {
        Assert.True(ServiceLaunch.IsServiceVerb([verb]));
        Assert.True(ServiceLaunch.IsServiceVerb([verb, "-environment:Uat"]));
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("local")]
    [InlineData("server")]
    [InlineData("--urls")]
    [InlineData("")]
    [InlineData("installer")]
    [InlineData(" install")]
    [InlineData("-install")]
    public void IsServiceVerb_RejectsAnythingElseFirst(string first) =>
        Assert.False(ServiceLaunch.IsServiceVerb([first, "install"]));

    [Fact]
    public void IsServiceVerb_RejectsAnEmptyCommandLine() =>
        Assert.False(ServiceLaunch.IsServiceVerb([]));

    [Theory]
    [InlineData("--mode", "local", "install")]
    [InlineData("--mode", "server", "start")]
    [InlineData("local", "stop")]
    public void IsServiceVerb_RejectsAVerbThatIsNotFirst(params string[] args) =>
        Assert.False(ServiceLaunch.IsServiceVerb(args));

    [Fact]
    public void Verbs_AreExactlyTheFiveLifecycleVerbs() =>
        Assert.Equal(["help", "install", "start", "stop", "uninstall"], ServiceLaunch.Verbs.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("install")]
    [InlineData("Stop")]
    public void UsesServiceHost_IsTrueForAVerbFirstOnWindows(string verb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The service host exists only on Windows.");

        Assert.True(ServiceLaunch.UsesServiceHost([verb]));
    }

    [Theory]
    [InlineData]
    [InlineData("--mode", "local")]
    [InlineData("--mode=server")]
    [InlineData("--DevConduit:Mode=Local", "--urls", "http://localhost:5000")]
    public void UsesServiceHost_IsFalseForAConsoleCommandLineInATestProcess(params string[] args) =>
        Assert.False(ServiceLaunch.UsesServiceHost(args));

    [Fact]
    public void IsRunningAsService_IsFalseInATestProcess() =>
        Assert.False(ServiceLaunch.IsRunningAsService());

    [Theory]
    [InlineData]
    [InlineData("--mode", "local")]
    [InlineData("--mode=server")]
    [InlineData("--DevConduit:Mode=Local", "--urls", "http://localhost:5000")]
    [InlineData("")]
    [InlineData("trays")]
    [InlineData(" tray")]
    [InlineData("--tray")]
    [InlineData("service-admins")]
    [InlineData("serviceadmin")]
    [InlineData("--mode", "tray")]
    [InlineData("local", "service-admin")]
    public void Resolve_IsConsoleForAnyOtherCommandLine(params string[] args) =>
        Assert.Equal(LaunchKind.Console, ServiceLaunch.Resolve(args));

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("help")]
    [InlineData("INSTALL")]
    [InlineData("sTaRt")]
    public void Resolve_IsServiceHostForALifecycleVerbInAnyCase(string verb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The service host exists only on Windows.");

        Assert.Equal(LaunchKind.ServiceHost, ServiceLaunch.Resolve([verb]));
        Assert.Equal(LaunchKind.ServiceHost, ServiceLaunch.Resolve([verb, "-environment:Uat"]));
    }

    [Theory]
    [InlineData("tray")]
    [InlineData("TRAY")]
    [InlineData("Tray")]
    public void Resolve_IsTrayForTheTrayVerbInAnyCase(string verb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The tray exists only on Windows.");

        Assert.Equal(LaunchKind.Tray, ServiceLaunch.Resolve([verb]));
        Assert.Equal(LaunchKind.Tray, ServiceLaunch.Resolve([verb, "--mode", "local"]));
    }

    [Theory]
    [InlineData("service-admin")]
    [InlineData("SERVICE-ADMIN")]
    [InlineData("Service-Admin")]
    public void Resolve_IsServiceAdminForTheHelperVerbInAnyCase(string verb)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The elevated helper exists only on Windows.");

        Assert.Equal(LaunchKind.ServiceAdmin, ServiceLaunch.Resolve([verb]));
        Assert.Equal(LaunchKind.ServiceAdmin, ServiceLaunch.Resolve([verb, "start"]));
    }

    [Theory]
    [InlineData("tray", "install")]
    [InlineData("service-admin", "install")]
    public void Resolve_LetsTheTrayVerbsWinOverALaterLifecycleVerb(string verb, string later)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The tray exists only on Windows.");

        Assert.NotEqual(LaunchKind.ServiceHost, ServiceLaunch.Resolve([verb, later]));
    }

    [Theory]
    [InlineData(ServiceLaunch.TrayVerb)]
    [InlineData(ServiceLaunch.ServiceAdminVerb)]
    [InlineData("TRAY")]
    [InlineData("SERVICE-ADMIN")]
    public void TrayVerbs_AreNotTopshelfVerbs(string verb)
    {
        Assert.DoesNotContain(verb, ServiceLaunch.Verbs);
        Assert.False(ServiceLaunch.IsServiceVerb([verb]));
        Assert.False(ServiceLaunch.UsesServiceHost([verb]));
    }

    [Fact]
    public void TrayVerbs_AreTrayAndServiceAdmin()
    {
        Assert.Equal("tray", ServiceLaunch.TrayVerb);
        Assert.Equal("service-admin", ServiceLaunch.ServiceAdminVerb);
    }

    // ---- ChangesServiceRegistration ------------------------------------------------------

    [Fact]
    public void RegistrationVerbs_AreInstallAndUninstall() =>
        Assert.Equal(["install", "uninstall"], ServiceLaunch.RegistrationVerbs.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("INSTALL")]
    [InlineData("Uninstall")]
    [InlineData("iNsTaLl")]
    [InlineData("install", "-environment:Uat")]
    [InlineData("-servicename:Other", "install")]
    [InlineData("--mode", "local", "UNINSTALL")]
    [InlineData("start", "install")]
    [InlineData("stop", "-instance:x", "uninstall")]
    public void ChangesServiceRegistration_IsTrueForInstallOrUninstallInAnyCaseAndPosition(params string[] args) =>
        Assert.True(ServiceLaunch.ChangesServiceRegistration(args));

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("help")]
    [InlineData("START")]
    [InlineData("tray")]
    [InlineData("service-admin")]
    [InlineData("service-admin", "start")]
    [InlineData("--mode", "local")]
    [InlineData("--mode=server")]
    [InlineData("--DevConduit:Mode=Local", "--urls", "http://localhost:5000")]
    [InlineData("-displayname:Dev.Conduit", "-servicename:Dev.Conduit")]
    [InlineData("installer")]
    [InlineData("-install")]
    [InlineData(" install")]
    [InlineData("uninstalls")]
    [InlineData("")]
    public void ChangesServiceRegistration_IsFalseForAnythingElse(params string[] args) =>
        Assert.False(ServiceLaunch.ChangesServiceRegistration(args));

    [Fact]
    public void ChangesServiceRegistration_IsFalseForAnEmptyCommandLine() =>
        Assert.False(ServiceLaunch.ChangesServiceRegistration([]));

    // ---- Certificate verbs ---------------------------------------------------------------

    [Fact]
    public void CertificateVerbs_AreExportImportAndCerts()
    {
        Assert.Equal("export", ServiceLaunch.ExportVerb);
        Assert.Equal("import", ServiceLaunch.ImportVerb);
        Assert.Equal("certs", ServiceLaunch.CertificatesVerb);
    }

    [Theory]
    [InlineData(LaunchKind.Export, "export")]
    [InlineData(LaunchKind.Export, "EXPORT", "--out", @"C:\x\host.conduit")]
    [InlineData(LaunchKind.Export, "Export", "--address", "10.0.0.5")]
    [InlineData(LaunchKind.Import, "import", @"C:\x\host.conduit")]
    [InlineData(LaunchKind.Import, "IMPORT", @"C:\x\host.conduit", "--machine")]
    [InlineData(LaunchKind.Certificates, "certs", "list")]
    [InlineData(LaunchKind.Certificates, "CERTS", "revoke", "0A1B")]
    [InlineData(LaunchKind.Certificates, "certs")]
    public void Resolve_SendsTheCertificateVerbsToTheirCommandsInAnyCase(LaunchKind expected, params string[] args)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The certificate commands exist only on Windows.");

        Assert.Equal(expected, ServiceLaunch.Resolve(args));
    }

    [Theory]
    [InlineData("exports")]
    [InlineData("--export")]
    [InlineData(" import")]
    [InlineData("cert")]
    [InlineData("certificates")]
    [InlineData("--mode", "export")]
    [InlineData("local", "import")]
    [InlineData("--DevConduit:Mode=server", "certs")]
    public void Resolve_IsConsoleWhenACertificateVerbIsNotExactlyFirst(params string[] args) =>
        Assert.Equal(LaunchKind.Console, ServiceLaunch.Resolve(args));

    [Theory]
    [InlineData("export", "install")]
    [InlineData("import", "uninstall")]
    [InlineData("certs", "start")]
    public void Resolve_LetsACertificateVerbWinOverALaterLifecycleVerb(string verb, string later)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The certificate commands exist only on Windows.");

        Assert.NotEqual(LaunchKind.ServiceHost, ServiceLaunch.Resolve([verb, later]));
    }

    [Theory]
    [InlineData(ServiceLaunch.ExportVerb)]
    [InlineData(ServiceLaunch.ImportVerb)]
    [InlineData(ServiceLaunch.CertificatesVerb)]
    public void CertificateVerbs_AreNotTopshelfVerbs(string verb)
    {
        Assert.DoesNotContain(verb, ServiceLaunch.Verbs);
        Assert.False(ServiceLaunch.IsServiceVerb([verb]));
        Assert.False(ServiceLaunch.ChangesServiceRegistration([verb]));
    }
}
