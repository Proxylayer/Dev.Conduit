using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// Which settings files a launch reads, where they sit among the configuration sources, and
/// which files are refused. Precedence is proven against a real WebApplication builder with an
/// empty temp content root; the settings paths are always injected temp paths.
/// </summary>
public sealed class SettingsFilesTests
{
    private const string ProbeKey = "Probe:Value";

    private static string Probe(string value) => $$"""{ "Probe": { "Value": "{{value}}" } }""";

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static WebApplicationBuilder CreateBuilder(SettingsLayout layout, params string[] args) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = layout.ContentRoot,
            EnvironmentName = SettingsLayout.EnvironmentName
        });

    private static IConfiguration Layer(SettingsLayout layout, bool runningAsService, params string[] args)
    {
        var builder = CreateBuilder(layout, args);

        Assert.True(
            layout.Files.TryAddTo(builder.Configuration, builder.Environment.EnvironmentName, runningAsService, out var error),
            error);
        Assert.Equal(string.Empty, error);

        return builder.Configuration;
    }

    /// <summary>
    /// Gives a temp file the owner and access list the elevated helper writes, so the machine
    /// file check accepts it. Only an elevated process may make Administrators the owner.
    /// </summary>
    private static void SecureAsMachineFile(string path)
    {
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void RequireElevatedForMachineContent() =>
        Assert.SkipUnless(
            IsElevated(),
            "A machine settings file with content is accepted only when owned by Administrators or SYSTEM, "
            + "which a non-elevated test process cannot arrange.");

    // ---- Construction and PathsFor -------------------------------------------------------

    [Fact]
    public void PathsFor_AServiceReadsTheMachineFileOnly()
    {
        using var layout = new SettingsLayout();

        Assert.Equal([layout.MachinePath], layout.Files.PathsFor(runningAsService: true));
    }

    [Fact]
    public void PathsFor_AConsoleOrTrayLaunchReadsTheMachineFileThenTheUserFile()
    {
        using var layout = new SettingsLayout();

        Assert.Equal([layout.MachinePath, layout.UserPath], layout.Files.PathsFor(runningAsService: false));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData(@"relative\settings.json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RefusesAPathThatIsNotFullyQualified(string path)
    {
        using var root = new TempDirectory();
        var full = Path.Combine(root.Path, "settings.json");

        Assert.ThrowsAny<ArgumentException>(() => new SettingsFiles(path, full));
        Assert.ThrowsAny<ArgumentException>(() => new SettingsFiles(full, path));
    }

    [Fact]
    public void ForThisComputer_NamesTheProgramDataAndLocalAppDataFiles()
    {
        // Paths only: nothing is read or written.
        var files = SettingsFiles.ForThisComputer();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Proxylayer", "Dev.Conduit", "settings.json"),
            files.MachinePath);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Proxylayer", "Dev.Conduit", "settings.json"),
            files.UserPath);
    }

    // ---- TryCheck ------------------------------------------------------------------------

    [Fact]
    public void TryCheck_AcceptsAnAbsentFileWithNoContent()
    {
        using var root = new TempDirectory();

        Assert.True(SettingsFiles.TryCheck(Path.Combine(root.Path, "missing.json"), out var hasContent, out var error));
        Assert.False(hasContent);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t\r\n")]
    public void TryCheck_AcceptsABlankFileWithNoContent(string text)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, text);

        Assert.True(SettingsFiles.TryCheck(path, out var hasContent, out var error));
        Assert.False(hasContent);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "DevConduit": { "Local": { "Port": 9000 } } }""")]
    [InlineData("""
        {
          // a comment
          "DevConduit": { "Local": { "Port": 9000, }, },
        }
        """)]
    public void TryCheck_AcceptsAnObjectWithContent(string text)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, text);

        Assert.True(SettingsFiles.TryCheck(path, out var hasContent, out var error));
        Assert.True(hasContent);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[ { \"a\": 1 } ]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void TryCheck_RefusesAFileThatIsNotAnObjectNamingIt(string text)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, text);

        Assert.False(SettingsFiles.TryCheck(path, out var hasContent, out var error));
        Assert.False(hasContent);
        Assert.Contains(path, error);
        Assert.Contains("single JSON object", error);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ \"a\": }")]
    [InlineData("not json")]
    [InlineData("{ \"a\": 1 } { \"b\": 2 }")]
    public void TryCheck_RefusesAnUnparseableFileNamingIt(string text)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, text);

        Assert.False(SettingsFiles.TryCheck(path, out var hasContent, out var error));
        Assert.False(hasContent);
        Assert.StartsWith($"The settings file {path} is not valid:", error);
        Assert.EndsWith("Correct the file or delete it, then start again.", error);
    }

    // ---- InsertionIndex ------------------------------------------------------------------

    [Fact]
    public void InsertionIndex_IsAfterTheEnvironmentFileAndBeforeEnvironmentVariablesAndTheCommandLine()
    {
        IList<IConfigurationSource> sources =
        [
            new MemoryConfigurationSource(),
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "appsettings.Uat.json" },
            new EnvironmentVariablesConfigurationSource(),
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(3, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_IsAfterAppSettingsJsonWhenThereIsNoEnvironmentFile()
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new EnvironmentVariablesConfigurationSource(),
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(1, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_MatchesTheFileNamesIgnoringCase()
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "APPSETTINGS.JSON" },
            new JsonConfigurationSource { Path = "AppSettings.uat.Json" },
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(2, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_IgnoresAnotherEnvironmentsFile()
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "appsettings.Development.json" },
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(1, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_WithoutAppSettingsIsAheadOfTheUnprefixedEnvironmentVariables()
    {
        IList<IConfigurationSource> sources =
        [
            new MemoryConfigurationSource(),
            new EnvironmentVariablesConfigurationSource { Prefix = "DOTNET_" },
            new EnvironmentVariablesConfigurationSource(),
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(2, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_WithoutAppSettingsOrEnvironmentVariablesIsAheadOfTheCommandLine()
    {
        IList<IConfigurationSource> sources = [new MemoryConfigurationSource(), new CommandLineConfigurationSource()];

        Assert.Equal(1, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_IsLastWhenThereIsNothingToGoBefore()
    {
        IList<IConfigurationSource> sources = [new MemoryConfigurationSource(), new MemoryConfigurationSource()];

        Assert.Equal(2, SettingsFiles.InsertionIndex(sources, "Uat"));
        Assert.Equal(0, SettingsFiles.InsertionIndex([], "Uat"));
    }

    [Fact]
    public void InsertionIndex_IsAfterTheApplicationSettingsFiles()
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "appsettings.Uat.json" },
            new JsonConfigurationSource { Path = "Dev.Conduit.settings.json" },
            new JsonConfigurationSource { Path = "Dev.Conduit.settings.Uat.json" },
            new JsonConfigurationSource { Path = "secrets.json" },
            new EnvironmentVariablesConfigurationSource(),
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(4, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_IsAfterTheApplicationSettingsFileWhenThereIsNoEnvironmentOne()
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "Dev.Conduit.settings.json" },
            new JsonConfigurationSource { Path = "Dev.Conduit.settings.Development.json" },
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(2, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Theory]
    [InlineData("secrets.json")]
    [InlineData("settings.json")]
    [InlineData(".settings.json")]
    [InlineData(@"C:\elsewhere\app.settings.json")]
    public void InsertionIndex_IgnoresAJsonFileThatIsNotAContentRootSettingsFile(string path)
    {
        IList<IConfigurationSource> sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = path },
            new CommandLineConfigurationSource()
        ];

        Assert.Equal(1, SettingsFiles.InsertionIndex(sources, "Uat"));
    }

    [Fact]
    public void InsertionIndex_InARealBuilderIsAfterEveryContentRootSettingsFile()
    {
        using var layout = new SettingsLayout();
        var builder = CreateBuilder(layout, $"--{ProbeKey}=command line");
        var sources = builder.Configuration.Sources;
        var applicationName = builder.Environment.ApplicationName;

        var index = SettingsFiles.InsertionIndex(sources, SettingsLayout.EnvironmentName);

        var before = sources.Take(index).OfType<JsonConfigurationSource>().Select(source => source.Path).ToList();
        Assert.Contains("appsettings.json", before);
        Assert.Contains("appsettings.Uat.json", before);
        Assert.Equal($"{applicationName}.settings.{SettingsLayout.EnvironmentName}.json", before[^1]);
        Assert.Contains($"{applicationName}.settings.json", before);
        Assert.DoesNotContain(sources.Skip(index), source => source is JsonConfigurationSource { Path: { } path }
            && (path.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) || path.Contains(".settings.", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(sources.Skip(index), source => source is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        Assert.Contains(sources.Skip(index), source => source is CommandLineConfigurationSource);
    }

    [Fact]
    public void TryAddTo_UserFileBeatsTheApplicationSettingsFiles()
    {
        using var layout = new SettingsLayout();
        var builder = CreateBuilder(layout);
        var applicationName = builder.Environment.ApplicationName;
        File.WriteAllText(Path.Combine(layout.ContentRoot, $"{applicationName}.settings.json"), Probe("application"));
        File.WriteAllText(Path.Combine(layout.ContentRoot, $"{applicationName}.settings.{SettingsLayout.EnvironmentName}.json"), Probe("application environment"));
        layout.User(Probe("user"));
        builder = CreateBuilder(layout);
        Assert.Equal("application environment", builder.Configuration[ProbeKey]);

        Assert.True(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: false, out var error), error);

        Assert.Equal("user", builder.Configuration[ProbeKey]);
    }

    // ---- TryAddTo: precedence ------------------------------------------------------------

    [Fact]
    public void TryAddTo_UserFileBeatsTheEnvironmentFile()
    {
        using var layout = new SettingsLayout();
        layout.AppSettings(Probe("appsettings")).EnvironmentSettings(Probe("environment")).User(Probe("user"));

        Assert.Equal("user", Layer(layout, runningAsService: false)[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_CommandLineBeatsTheUserFile()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).User(Probe("user"));

        Assert.Equal("command line", Layer(layout, runningAsService: false, $"--{ProbeKey}=command line")[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_EnvironmentVariablesBeatTheUserFile()
    {
        using var layout = new SettingsLayout();
        var section = $"SettingsFilesProbe{Guid.NewGuid():N}";
        layout.User($$"""{ "{{section}}": { "Value": "user" } }""");

        Environment.SetEnvironmentVariable($"{section}__Value", "environment variable");
        try
        {
            Assert.Equal("environment variable", Layer(layout, runningAsService: false)[$"{section}:Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{section}__Value", null);
        }
    }

    [Fact]
    public void TryAddTo_KeysTheUserFileDoesNotSetComeFromBelow()
    {
        using var layout = new SettingsLayout();
        layout
            .AppSettings("""{ "Probe": { "Value": "appsettings", "Other": "kept" } }""")
            .User(Probe("user"));

        var configuration = Layer(layout, runningAsService: false);

        Assert.Equal("user", configuration[ProbeKey]);
        Assert.Equal("kept", configuration["Probe:Other"]);
    }

    [Fact]
    public void TryAddTo_AServiceSkipsTheUserFile()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).User(Probe("user"));

        Assert.Equal("environment", Layer(layout, runningAsService: true)[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_AServiceDoesNotEvenReadAMalformedUserFile()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).User("{ not json");

        Assert.Equal("environment", Layer(layout, runningAsService: true)[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_PlacesTheUserFileAtTheInsertionIndex()
    {
        using var layout = new SettingsLayout();
        layout.User(Probe("user"));
        var builder = CreateBuilder(layout);
        var expected = SettingsFiles.InsertionIndex(builder.Configuration.Sources, SettingsLayout.EnvironmentName);

        Assert.True(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: false, out var error), error);

        var source = Assert.IsType<JsonConfigurationSource>(builder.Configuration.Sources[expected]);
        Assert.Equal(layout.UserPath, Path.Combine(((Microsoft.Extensions.FileProviders.PhysicalFileProvider)source.FileProvider!).Root, source.Path!));
        Assert.False(source.ReloadOnChange);
    }

    [Fact]
    public void TryAddTo_AddsNothingForAbsentOrBlankFiles()
    {
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).Machine("   ").User("");
        var builder = CreateBuilder(layout);
        var before = builder.Configuration.Sources.Count;

        Assert.True(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: false, out var error), error);

        Assert.Equal(before, builder.Configuration.Sources.Count);
        Assert.Equal("environment", builder.Configuration[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_UserFileBeatsTheMachineFile()
    {
        RequireElevatedForMachineContent();
        using var layout = new SettingsLayout();
        layout.EnvironmentSettings(Probe("environment")).Machine(Probe("machine")).User(Probe("user"));
        SecureAsMachineFile(layout.MachinePath);

        Assert.Equal("user", Layer(layout, runningAsService: false)[ProbeKey]);
    }

    [Fact]
    public void TryAddTo_MachineFileBeatsTheEnvironmentFile()
    {
        RequireElevatedForMachineContent();
        using var layout = new SettingsLayout();
        layout.AppSettings(Probe("appsettings")).EnvironmentSettings(Probe("environment")).Machine(Probe("machine"));
        SecureAsMachineFile(layout.MachinePath);

        Assert.Equal("machine", Layer(layout, runningAsService: false)[ProbeKey]);
        Assert.Equal("machine", Layer(layout, runningAsService: true)[ProbeKey]);
    }

    // ---- TryAddTo: refusals --------------------------------------------------------------

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[ 1, 2 ]")]
    public void TryAddTo_RefusesAMalformedUserFileNamingIt(string text)
    {
        using var layout = new SettingsLayout();
        layout.User(text);
        var builder = CreateBuilder(layout);

        Assert.False(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: false, out var error));
        Assert.Contains(layout.UserPath, error);
    }

    [Fact]
    public void TryAddTo_RefusesAMalformedMachineFileNamingItBeforeCheckingItsOwner()
    {
        using var layout = new SettingsLayout();
        layout.Machine("{ broken");
        var builder = CreateBuilder(layout);

        Assert.False(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: true, out var error));
        Assert.StartsWith($"The settings file {layout.MachinePath} is not valid:", error);
    }

    [Fact]
    public void TryAddTo_RefusesAMachineFileAnOrdinaryUserCouldHaveWritten()
    {
        using var layout = new SettingsLayout();
        layout.Machine(Probe("machine"));
        var builder = CreateBuilder(layout);

        Assert.False(layout.Files.TryAddTo(builder.Configuration, SettingsLayout.EnvironmentName, runningAsService: false, out var error));
        Assert.StartsWith($"The machine settings file {layout.MachinePath} ", error);
        Assert.Contains(IsElevated() ? "only Administrators and SYSTEM may write it" : "not by Administrators or SYSTEM", error);
        Assert.NotEqual("machine", builder.Configuration[ProbeKey]);
    }

    // ---- MachineSettingsAccess -----------------------------------------------------------

    [Fact]
    public void MachineAccessCheck_RefusesAFileOwnedByTheCurrentNonAdministrativeUser()
    {
        Assert.SkipWhen(IsElevated(), "An elevated process creates files owned by Administrators.");
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, "{}");

        var problem = MachineSettingsAccess.Check(path);

        Assert.NotNull(problem);
        Assert.StartsWith($"The machine settings file {path} is owned by ", problem);
        Assert.Contains("not by Administrators or SYSTEM", problem);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Contains(identity.Name, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MachineAccessCheck_ReportsAFileThatCannotBeRead()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "missing.json");

        var problem = MachineSettingsAccess.Check(path);

        Assert.NotNull(problem);
        Assert.StartsWith($"The machine settings file {path} could not be checked:", problem);
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void MachineAccessCheck_AcceptsTheHelpersAccessListUnderAnAdministrativeOwner(WellKnownSidType owner)
    {
        // An in-memory access list: setting its owner needs no privilege, unlike the file's.
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier(owner, domainSid: null));

        Assert.Null(MachineSettingsAccess.Check(@"C:\machine\settings.json", security));
    }

    [Fact]
    public void MachineAccessCheck_AcceptsTrustedInstallerAsOwner()
    {
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"));

        Assert.Null(MachineSettingsAccess.Check(@"C:\machine\settings.json", security));
    }

    [Fact]
    public void MachineAccessCheck_RefusesAnOwnerThatIsNotAdministrative()
    {
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null));

        var problem = MachineSettingsAccess.Check(@"C:\machine\settings.json", security);

        Assert.NotNull(problem);
        Assert.StartsWith(@"The machine settings file C:\machine\settings.json is owned by ", problem);
    }

    [Fact]
    public void MachineAccessCheck_RefusesAnAccessListWithNoOwner()
    {
        var problem = MachineSettingsAccess.Check(@"C:\machine\settings.json", new FileSecurity());

        Assert.NotNull(problem);
        Assert.Contains("is owned by an unknown account", problem);
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Modify)]
    public void MachineAccessCheck_RefusesAnAdministrativelyOwnedFileOthersCanWrite(FileSystemRights rights)
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null);
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
        security.AddAccessRule(new FileSystemAccessRule(users, rights, AccessControlType.Allow));

        var problem = MachineSettingsAccess.Check(@"C:\machine\settings.json", security);

        Assert.NotNull(problem);
        Assert.StartsWith(@"The machine settings file C:\machine\settings.json can be changed by ", problem);
        Assert.Contains(users.Translate(typeof(NTAccount)).Value, problem);
    }

    [Fact]
    public void MachineAccessCheck_IgnoresADenyRule()
    {
        var security = MachineSettingsAccess.BuildSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.Write,
            AccessControlType.Deny));

        Assert.Null(MachineSettingsAccess.Check(@"C:\machine\settings.json", security));
    }

    [Fact]
    public void BuildSecurity_IsProtectedWithAdministratorsAndSystemFullControlAndUsersRead()
    {
        var security = MachineSettingsAccess.BuildSecurity();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToDictionary(rule => (SecurityIdentifier)rule.IdentityReference, rule => rule.FileSystemRights);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(3, rules.Count);
        Assert.Equal(FileSystemRights.FullControl, rules[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]);
        Assert.Equal(FileSystemRights.FullControl, rules[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)]);
        Assert.Equal(FileSystemRights.Read | FileSystemRights.Synchronize, rules[new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)]);
    }
}
