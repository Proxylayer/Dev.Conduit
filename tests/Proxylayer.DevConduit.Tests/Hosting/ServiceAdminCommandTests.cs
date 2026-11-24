using System.Runtime.Versioning;
using System.Security.Principal;
using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// The elevated helper's argument checks and exit codes. Nothing is saved, no service is
/// controlled or installed: every Run here returns before acting, and any Run that could reach
/// an action is skipped when the test process happens to be elevated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceAdminCommandTests
{
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RequireNotElevated() =>
        Assert.SkipWhen(IsElevated(), "The test process is elevated, so the helper would act instead of refusing.");

    // ---- Run -----------------------------------------------------------------------------

    [Fact]
    public void Run_WithNoArgumentsIsInvalidArguments() =>
        Assert.Equal(2, ServiceAdminCommand.Run([]));

    [Theory]
    [InlineData(ServiceAdminCommand.StartAction)]
    [InlineData(ServiceAdminCommand.StopAction)]
    [InlineData(ServiceAdminCommand.RestartAction)]
    [InlineData(ServiceAdminCommand.UninstallAction)]
    [InlineData(ServiceAdminCommand.InstallAction, "Uat")]
    [InlineData(ServiceAdminCommand.SaveAction, @"C:\nowhere\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData(ServiceAdminCommand.ForgetAction, "devconduit.example.internal")]
    [InlineData("unknown")]
    public void Run_UnelevatedIsNotElevatedForEveryAction(params string[] args)
    {
        RequireNotElevated();

        Assert.Equal((int)ServiceAdminExitCode.NotElevated, ServiceAdminCommand.Run(args));
        Assert.Equal(3, ServiceAdminCommand.Run(args));
    }

    [Fact]
    public void Actions_AreTheSixHelperVerbs()
    {
        Assert.Equal("start", ServiceAdminCommand.StartAction);
        Assert.Equal("stop", ServiceAdminCommand.StopAction);
        Assert.Equal("restart", ServiceAdminCommand.RestartAction);
        Assert.Equal("install", ServiceAdminCommand.InstallAction);
        Assert.Equal("uninstall", ServiceAdminCommand.UninstallAction);
        Assert.Equal("save", ServiceAdminCommand.SaveAction);
    }

    // ---- IsAcceptedEnvironmentName -------------------------------------------------------

    [Theory]
    [InlineData("Uat")]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("uat.eu-west_2")]
    [InlineData("A")]
    [InlineData("0")]
    [InlineData("...")]
    [InlineData("-")]
    public void IsAcceptedEnvironmentName_AcceptsLettersDigitsDotDashAndUnderscore(string name) =>
        Assert.True(ServiceAdminCommand.IsAcceptedEnvironmentName(name));

    [Fact]
    public void IsAcceptedEnvironmentName_AcceptsSixtyFourCharacters() =>
        Assert.True(ServiceAdminCommand.IsAcceptedEnvironmentName(new string('a', 64)));

    [Fact]
    public void IsAcceptedEnvironmentName_RefusesSixtyFiveCharacters() =>
        Assert.False(ServiceAdminCommand.IsAcceptedEnvironmentName(new string('a', 65)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Uat")]
    [InlineData("Uat ")]
    [InlineData("U at")]
    [InlineData("Uat;Prod")]
    [InlineData("Uat&calc")]
    [InlineData("Uat\"")]
    [InlineData("Uat/x")]
    [InlineData(@"Uat\x")]
    [InlineData("Uat:x")]
    [InlineData("Uat=x")]
    [InlineData("Üat")]
    [InlineData("Uat\t")]
    [InlineData("\nUat")]
    public void IsAcceptedEnvironmentName_RefusesAnythingElse(string? name) =>
        Assert.False(ServiceAdminCommand.IsAcceptedEnvironmentName(name));

    [Theory]
    [InlineData("Uat\n")]
    [InlineData("Production\n")]
    public void IsAcceptedEnvironmentName_RefusesATrailingLineFeed(string name) =>
        Assert.False(ServiceAdminCommand.IsAcceptedEnvironmentName(name));

    // ---- Exit codes ----------------------------------------------------------------------

    [Fact]
    public void ExitCodes_HaveTheirDocumentedValues()
    {
        Assert.Equal(0, (int)ServiceAdminExitCode.Success);
        Assert.Equal(1, (int)ServiceAdminExitCode.UnexpectedError);
        Assert.Equal(2, (int)ServiceAdminExitCode.InvalidArguments);
        Assert.Equal(3, (int)ServiceAdminExitCode.NotElevated);
        Assert.Equal(4, (int)ServiceAdminExitCode.ServiceNotInstalled);
        Assert.Equal(5, (int)ServiceAdminExitCode.ServiceAlreadyInstalled);
        Assert.Equal(6, (int)ServiceAdminExitCode.ServiceTimedOut);
        Assert.Equal(7, (int)ServiceAdminExitCode.ServiceControlFailed);
        Assert.Equal(8, (int)ServiceAdminExitCode.SettingsRejected);
        Assert.Equal(9, (int)ServiceAdminExitCode.SettingsWriteFailed);
        Assert.Equal(10, (int)ServiceAdminExitCode.InstallFailed);
        Assert.Equal(11, (int)ServiceAdminExitCode.UninstallFailed);
        Assert.Equal(12, (int)ServiceAdminExitCode.ManagedByInstaller);
        Assert.Equal(13, (int)ServiceAdminExitCode.ExportRefused);
        Assert.Equal(14, (int)ServiceAdminExitCode.ExportFailed);
        Assert.Equal(15, (int)ServiceAdminExitCode.ImportRejected);
        Assert.Equal(16, (int)ServiceAdminExitCode.ImportFailed);
        Assert.Equal(17, (int)ServiceAdminExitCode.TrustFailed);
        Assert.Equal(18, (int)ServiceAdminExitCode.NotFound);
        Assert.Equal(19, (int)ServiceAdminExitCode.ForgetFailed);
        Assert.Equal(20, (int)ServiceAdminExitCode.IdentityRenewalFailed);
        Assert.Equal(21, (int)ServiceAdminExitCode.UntrustFailed);
        Assert.Equal(22, (int)ServiceAdminExitCode.UpdateRefused);
        Assert.Equal(23, (int)ServiceAdminExitCode.UpdateFailed);
        Assert.Equal(24, Enum.GetValues<ServiceAdminExitCode>().Length);
    }

    [Fact]
    public void Describe_GivesEveryCodeItsOwnSentence()
    {
        var descriptions = Enum.GetValues<ServiceAdminExitCode>()
            .Select(code => (Code: code, Text: ServiceAdminExitCodes.Describe(code)))
            .ToList();

        Assert.All(descriptions, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Text), pair.Code.ToString());
            Assert.EndsWith(".", pair.Text);
            Assert.DoesNotContain("unknown exit code", pair.Text);
        });
        Assert.Equal(descriptions.Count, descriptions.Select(pair => pair.Text).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Describe_ByNumberMatchesDescribeByCode()
    {
        foreach (var code in Enum.GetValues<ServiceAdminExitCode>())
        {
            Assert.Equal(ServiceAdminExitCodes.Describe(code), ServiceAdminExitCodes.Describe((int)code));
        }
    }

    [Theory]
    [InlineData(ServiceAdminExitCode.Success, "Done.")]
    [InlineData(ServiceAdminExitCode.UnexpectedError, "The administrative helper failed unexpectedly.")]
    [InlineData(ServiceAdminExitCode.NotElevated, "The administrative helper did not run with administrator rights.")]
    [InlineData(ServiceAdminExitCode.InvalidArguments, "The administrative helper was started with arguments it does not accept.")]
    public void Describe_SaysWhatHappened(ServiceAdminExitCode code, string expected) =>
        Assert.Equal(expected, ServiceAdminExitCodes.Describe(code));

    [Fact]
    public void Describe_NamesTheServiceWhereItMatters()
    {
        Assert.Contains(DevConduitServiceHost.ServiceName, ServiceAdminExitCodes.Describe(ServiceAdminExitCode.ServiceNotInstalled));
        Assert.Contains(DevConduitServiceHost.ServiceName, ServiceAdminExitCodes.Describe(ServiceAdminExitCode.ServiceAlreadyInstalled));
        Assert.Contains(ServiceEventLog.SourceName, ServiceAdminExitCodes.Describe(ServiceAdminExitCode.ServiceControlFailed));
    }

    [Fact]
    public void Describe_ManagedByInstallerPointsAtTheMsi()
    {
        var text = ServiceAdminExitCodes.Describe(12);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Equal(ServiceAdminExitCodes.Describe(ServiceAdminExitCode.ManagedByInstaller), text);
        Assert.Contains("MSI", text, StringComparison.Ordinal);
        Assert.Contains("msiexec", text, StringComparison.Ordinal);
        Assert.Contains(DevConduitServiceHost.ServiceName, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(1223)]
    public void Describe_ReportsAnUnknownCodeByNumber(int exitCode) =>
        Assert.Equal($"The administrative helper ended with an unknown exit code ({exitCode}).", ServiceAdminExitCodes.Describe(exitCode));

    // ---- Certificate actions -------------------------------------------------------------

    [Fact]
    public void CertificateActions_AreTheFiveCertificateVerbs()
    {
        Assert.Equal("export", ServiceAdminCommand.ExportAction);
        Assert.Equal("import", ServiceAdminCommand.ImportAction);
        Assert.Equal("trust", ServiceAdminCommand.TrustAction);
        Assert.Equal("revoke", ServiceAdminCommand.RevokeAction);
        Assert.Equal("forget", ServiceAdminCommand.ForgetAction);
    }

    // Unelevated, the helper refuses before it looks at any argument, so the per-action checks
    // (path shape, transfer name, host name, serial) are reachable only elevated and are
    // exercised here through the public pieces they call instead.
    [Theory]
    [InlineData(ServiceAdminCommand.ExportAction, @"C:\out\host.conduit")]
    [InlineData(ServiceAdminCommand.ExportAction, @"C:\out\host.conduit", "10.0.0.5")]
    [InlineData(ServiceAdminCommand.ExportAction, "relative.conduit")]
    [InlineData(ServiceAdminCommand.ImportAction, @"C:\nowhere\Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData(ServiceAdminCommand.ImportAction, @"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(ServiceAdminCommand.TrustAction, "devconduit.example.internal")]
    [InlineData(ServiceAdminCommand.TrustAction, "not a host")]
    [InlineData(ServiceAdminCommand.RevokeAction, "0A1B2C")]
    [InlineData(ServiceAdminCommand.RevokeAction, "not-hex")]
    [InlineData(ServiceAdminCommand.ForgetAction, "DEVCONDUIT.example.internal")]
    [InlineData(ServiceAdminCommand.ExportAction)]
    [InlineData(ServiceAdminCommand.ImportAction)]
    [InlineData(ServiceAdminCommand.TrustAction, "a.example", "b.example")]
    public void Run_UnelevatedIsNotElevatedForEveryCertificateAction(params string[] args)
    {
        RequireNotElevated();

        Assert.Equal((int)ServiceAdminExitCode.NotElevated, ServiceAdminCommand.Run(args));
    }

    // ---- IsAcceptedHostname --------------------------------------------------------------

    [Theory]
    [InlineData("devconduit.example.internal")]
    [InlineData("localhost")]
    [InlineData("a")]
    [InlineData("a-1.b-2")]
    public void IsAcceptedHostname_AcceptsALowerCaseDnsName(string hostname) =>
        Assert.True(ServiceAdminCommand.IsAcceptedHostname(hostname));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("DEVCONDUIT.example.internal")]
    [InlineData("Localhost")]
    [InlineData(" devconduit.example.internal")]
    [InlineData("devconduit.example.internal ")]
    [InlineData("devconduit.example.internal.")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    [InlineData(@"..\client")]
    [InlineData("a/b")]
    [InlineData("*.devconduit.internal")]
    [InlineData("under_score")]
    [InlineData("host\n")]
    [InlineData("host;calc")]
    public void IsAcceptedHostname_RefusesAnythingElse(string? hostname) =>
        Assert.False(ServiceAdminCommand.IsAcceptedHostname(hostname));

    // ---- HostnameArgument (trust and forget alike) ----------------------------------------

    [Theory]
    [InlineData("devconduit.example.internal", "devconduit.example.internal")]
    [InlineData("DEVCONDUIT.example.internal", "devconduit.example.internal")]
    [InlineData("Localhost", "localhost")]
    public void HostnameArgument_LowerCasesThenAccepts(string argument, string expected) =>
        Assert.Equal(expected, ServiceAdminCommand.HostnameArgument(argument));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" devconduit.example.internal")]
    [InlineData("devconduit.example.internal.")]
    [InlineData("10.0.0.5")]
    [InlineData(@"..\client")]
    [InlineData("host;calc")]
    [InlineData("höst.example")]
    public void HostnameArgument_RefusesWhatIsNotAHostOnceLowerCased(string? argument) =>
        Assert.Null(ServiceAdminCommand.HostnameArgument(argument));

    [Fact]
    public void Run_UnelevatedIsNotElevatedForRevokeByEnrollment()
    {
        RequireNotElevated();

        Assert.Equal(
            (int)ServiceAdminExitCode.NotElevated,
            ServiceAdminCommand.Run([ServiceAdminCommand.RevokeAction, CertsCommand.EnrollmentOption, "0123456789abcdef0123456789abcdef"]));
    }

    // ---- IsAcceptedSerial (revoke) -------------------------------------------------------

    [Theory]
    [InlineData("0A")]
    [InlineData("0a1b2c3d4e5f")]
    [InlineData(" 0A1B ")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void IsAcceptedSerial_AcceptsTwoToSixtyFourHexDigits(string serial) =>
        Assert.True(CertsCommand.IsAcceptedSerial(serial));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("0A:1B")]
    [InlineData("0A 1B")]
    [InlineData("0G")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    public void IsAcceptedSerial_RefusesAnythingElse(string? serial) =>
        Assert.False(CertsCommand.IsAcceptedSerial(serial));

    // ---- Describe, codes 13 to 20 --------------------------------------------------------

    [Theory]
    [InlineData(ServiceAdminExitCode.ExportRefused, "DevConduit:Server:Hostname")]
    [InlineData(ServiceAdminExitCode.ExportRefused, "Kestrel:Endpoints")]
    [InlineData(ServiceAdminExitCode.ExportFailed, ".conduit could not be written")]
    [InlineData(ServiceAdminExitCode.ImportRejected, "Dev.Conduit.exe import <file>")]
    [InlineData(ServiceAdminExitCode.ImportFailed, "--machine")]
    [InlineData(ServiceAdminExitCode.TrustFailed, @"LocalMachine\Root")]
    [InlineData(ServiceAdminExitCode.NotFound, "serial number")]
    [InlineData(ServiceAdminExitCode.NotFound, "imported server")]
    [InlineData(ServiceAdminExitCode.ForgetFailed, @"%ProgramData%\Proxylayer\Dev.Conduit\client")]
    [InlineData(ServiceAdminExitCode.IdentityRenewalFailed, "client certificate could not be renewed")]
    [InlineData(ServiceAdminExitCode.IdentityRenewalFailed, "current one is kept")]
    [InlineData(ServiceAdminExitCode.UntrustFailed, "certs untrust")]
    public void Describe_SaysWhatEachCertificateCodeMeans(ServiceAdminExitCode code, string expected) =>
        Assert.Contains(expected, ServiceAdminExitCodes.Describe(code), StringComparison.Ordinal);

    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void Describe_KnowsEveryCertificateCodeByNumber(int exitCode)
    {
        var text = ServiceAdminExitCodes.Describe(exitCode);

        Assert.DoesNotContain("unknown exit code", text);
        Assert.NotEqual(ServiceAdminExitCodes.Describe(ServiceAdminExitCode.UnexpectedError), text);
    }
}
