using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>Applying a change set to one settings file on disk, under a throwaway directory.</summary>
public sealed class SettingsFileWriterTests
{
    private static void AssertJson(string expected, string actualJson) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actualJson)),
            $"Expected {expected} but the file holds {actualJson}");

    private static string[] FilesIn(string directory) =>
        [.. Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("settings.json")]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RefusesAPathThatIsNotFullyQualified(string path) =>
        Assert.ThrowsAny<ArgumentException>(() => new SettingsFileWriter(path));

    [Fact]
    public void Read_GivesAnEmptyDocumentForAnAbsentFile()
    {
        using var root = new TempDirectory();

        Assert.Empty(new SettingsFileWriter(Path.Combine(root.Path, "settings.json")).Read());
    }

    [Fact]
    public void Read_GivesAnEmptyDocumentForABlankFile()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, "  \r\n");

        Assert.Empty(new SettingsFileWriter(path).Read());
    }

    [Fact]
    public void Apply_CreatesAMissingFileAndItsFolder()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "nested", "folder", "settings.json");

        var written = new SettingsFileWriter(path).Apply(new SettingsChangeSet().Set("DevConduit:Local:Port", 9000));

        Assert.True(File.Exists(path));
        AssertJson("""{ "DevConduit": { "Local": { "Port": 9000 } } }""", File.ReadAllText(path));
        AssertJson("""{ "DevConduit": { "Local": { "Port": 9000 } } }""", written.ToJsonString());
    }

    [Fact]
    public void Apply_MergesIntoAnExistingFileKeepingOtherKeysAndTheirCase()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, """
            {
              "azuread": { "tenantId": "t" },
              "devConduit": { "local": { "port": 8092, "CaptureEnabled": true }, "Unmanaged": [ 1, 2 ] }
            }
            """);

        new SettingsFileWriter(path).Apply(new SettingsChangeSet()
            .Set("DevConduit:Local:Port", 9000)
            .Remove("DevConduit:Local:CaptureEnabled")
            .Set("DevConduit:DefaultRemote", "uat"));

        var text = File.ReadAllText(path);
        AssertJson("""
            {
              "azuread": { "tenantId": "t" },
              "devConduit": { "local": { "port": 9000 }, "Unmanaged": [ 1, 2 ], "DefaultRemote": "uat" }
            }
            """, text);
        Assert.Contains("\"azuread\"", text);
        Assert.Contains("\"tenantId\"", text);
        Assert.Contains("\"devConduit\"", text);
        Assert.Contains("\"port\"", text);
    }

    [Fact]
    public void Apply_ReadsAFileWithCommentsAndTrailingCommasAndDropsTheComments()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, """
            {
              // keep me
              "DevConduit": { "DataRoot": "D:\\data", },
            }
            """);

        new SettingsFileWriter(path).Apply(new SettingsChangeSet().Set("DevConduit:Local:Port", 1));

        var text = File.ReadAllText(path);
        AssertJson("""{ "DevConduit": { "DataRoot": "D:\\data", "Local": { "Port": 1 } } }""", text);
        Assert.DoesNotContain("keep me", text);
    }

    [Fact]
    public void Apply_WritesIndentedUtf8WithoutAByteOrderMarkAndWithUnescapedUrls()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");

        new SettingsFileWriter(path).Apply(new SettingsChangeSet().Set("Kestrel:Endpoints:Https:Url", "https://0.0.0.0:8091/?a=1&b=2"));

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        var text = File.ReadAllText(path);
        Assert.Contains("https://0.0.0.0:8091/?a=1&b=2", text);
        Assert.Contains(Environment.NewLine + "  \"Kestrel\"", text);
        Assert.EndsWith("}" + Environment.NewLine, text);
    }

    [Fact]
    public void Apply_RemovingTheLastKeyLeavesAnEmptyObject()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, """{ "DevConduit": { "Local": { "Port": 1 } } }""");

        new SettingsFileWriter(path).Apply(new SettingsChangeSet().Remove("DevConduit:Local:Port"));

        AssertJson("{}", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{ \"DevConduit\": ")]
    [InlineData("not json at all")]
    public void Apply_RefusesToOverwriteMalformedJsonAndNamesTheFile(string original)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, original);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SettingsFileWriter(path).Apply(new SettingsChangeSet().Set("DevConduit:Local:Port", 1)));

        Assert.Contains(path, exception.Message);
        Assert.Contains("left as it is", exception.Message);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(["settings.json"], FilesIn(root.Path));
    }

    [Theory]
    [InlineData("[ 1, 2 ]")]
    [InlineData("\"text\"")]
    public void Apply_RefusesToOverwriteJsonThatIsNotAnObjectAndNamesTheFile(string original)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, original);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new SettingsFileWriter(path).Apply(new SettingsChangeSet().Set("DevConduit:Local:Port", 1)));

        Assert.Contains(path, exception.Message);
        Assert.Contains("not a JSON object", exception.Message);
        Assert.Contains("left as it is", exception.Message);

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(["settings.json"], FilesIn(root.Path));
    }

    [Fact]
    public void Apply_LeavesNoTemporaryFileBehind()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        var writer = new SettingsFileWriter(path);

        writer.Apply(new SettingsChangeSet().Set("A", 1));
        writer.Apply(new SettingsChangeSet().Set("B", 2));
        writer.Apply(new SettingsChangeSet().Remove("A"));

        Assert.Equal(["settings.json"], FilesIn(root.Path));
        AssertJson("""{ "B": 2 }""", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_WritesTheFileWithTheGivenAccessList()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "settings.json");
        using var identity = WindowsIdentity.GetCurrent();
        var calls = 0;

        FileSecurity Security()
        {
            calls++;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            return security;
        }

        new SettingsFileWriter(path, Security).Apply(new SettingsChangeSet().Set("A", 1));

        Assert.Equal(1, calls);
        var written = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(written.AreAccessRulesProtected);
        var rule = Assert.Single(written.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(["settings.json"], FilesIn(root.Path));
    }

    [Fact]
    public void Apply_RefusesANullChangeSet()
    {
        using var root = new TempDirectory();

        Assert.Throws<ArgumentNullException>(() => new SettingsFileWriter(Path.Combine(root.Path, "s.json")).Apply(null!));
    }
}
