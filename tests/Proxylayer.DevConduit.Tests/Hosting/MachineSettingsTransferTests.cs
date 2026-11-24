using System.Text;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// The transfer file that carries a machine settings change to the elevated helper: which
/// paths are accepted, and which contents are refused. Every file is under a throwaway
/// directory; the machine settings file is never written.
/// </summary>
public sealed class MachineSettingsTransferTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    private static string TransferName(string hex = Hex) => $"Dev.Conduit-settings-{hex}.json";

    private static string WriteFile(TempDirectory root, string name, string content)
    {
        var path = Path.Combine(root.Path, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // ---- WriteTempFile / Delete ----------------------------------------------------------

    [Fact]
    public void WriteTempFile_WritesAFileNamedAsATransferThatReadsBack()
    {
        using var root = new TempDirectory();
        var changes = new SettingsChangeSet()
            .Set(ManagedSettingKeys.TenantId, "contoso.onmicrosoft.com")
            .Set(ManagedSettingKeys.ServerPort, 8443)
            .Remove(ManagedSettingKeys.Routes);

        var path = MachineSettingsTransfer.WriteTempFile(changes, root.Path);

        Assert.Equal(root.Path, Path.GetDirectoryName(path));
        Assert.Matches("^Dev\\.Conduit-settings-[0-9a-f]{32}\\.json$", Path.GetFileName(path));
        Assert.True(MachineSettingsTransfer.IsTransferPath(path));
        Assert.True(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var read, out var error), error);
        Assert.Equal(changes.ToJson(), read.ToJson());
    }

    [Fact]
    public void WriteTempFile_GivesEveryFileANewName()
    {
        using var root = new TempDirectory();

        var first = MachineSettingsTransfer.WriteTempFile(new SettingsChangeSet(), root.Path);
        var second = MachineSettingsTransfer.WriteTempFile(new SettingsChangeSet(), root.Path);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Delete_RemovesATransferFile()
    {
        using var root = new TempDirectory();
        var path = MachineSettingsTransfer.WriteTempFile(new SettingsChangeSet(), root.Path);

        MachineSettingsTransfer.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("Dev.Conduit-settings-0123456789ABCDEF0123456789ABCDEF.json")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json.bak")]
    public void Delete_LeavesAnyOtherFileAlone(string name)
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, name, "{}");

        MachineSettingsTransfer.Delete(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Delete_IgnoresAMissingFileAndARelativePath()
    {
        using var root = new TempDirectory();

        MachineSettingsTransfer.Delete(Path.Combine(root.Path, TransferName()));
        MachineSettingsTransfer.Delete(TransferName());
    }

    // ---- IsTransferPath ------------------------------------------------------------------

    [Theory]
    [InlineData(Hex)]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    public void IsTransferPath_AcceptsAFullPathToAProperlyNamedFile(string hex)
    {
        using var root = new TempDirectory();

        Assert.True(MachineSettingsTransfer.IsTransferPath(Path.Combine(root.Path, TransferName(hex))));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("")]
    public void IsTransferPath_RefusesAnythingButThirtyTwoLowerCaseHexDigits(string hex)
    {
        using var root = new TempDirectory();

        Assert.False(MachineSettingsTransfer.IsTransferPath(Path.Combine(root.Path, TransferName(hex))));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.txt")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json.json")]
    [InlineData("dev.conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData("DevxConduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData("xDev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.jsonx")]
    public void IsTransferPath_RefusesAnyOtherFileName(string name)
    {
        using var root = new TempDirectory();

        Assert.False(MachineSettingsTransfer.IsTransferPath(Path.Combine(root.Path, name)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData(@"temp\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData(@"..\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData(@"C:Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    [InlineData(@"\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    public void IsTransferPath_RefusesABlankOrRelativePath(string? path) =>
        Assert.False(MachineSettingsTransfer.IsTransferPath(path));

    // ---- TryRead -------------------------------------------------------------------------

    [Fact]
    public void TryRead_RefusesAFileNotNamedAsATransfer()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, "settings.json", new SettingsChangeSet().Set(ManagedSettingKeys.TenantId, "t").ToJson());

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.Equal($"'{path}' is not a settings transfer file.", error);
    }

    [Fact]
    public void TryRead_RefusesARelativePath()
    {
        Assert.False(MachineSettingsTransfer.TryRead(TransferName(), ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.Contains("is not a settings transfer file", error);
    }

    [Fact]
    public void TryRead_RefusesADirectoryNamedAsATransfer()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, TransferName());
        Directory.CreateDirectory(path);

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.Equal($"'{path}' is not an ordinary file.", error);
    }

    [Fact]
    public void TryRead_RefusesAMissingFile()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, TransferName());

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out _, out var error));
        Assert.Equal($"'{path}' is not an ordinary file.", error);
    }

    [Fact]
    public void TryRead_AcceptsAFileOfExactlyTheLimit()
    {
        using var root = new TempDirectory();
        var json = new SettingsChangeSet().Set(ManagedSettingKeys.TenantId, "t").ToJson();
        var path = WriteFile(root, TransferName(), json.PadRight((int)MachineSettingsTransfer.MaxBytes));
        Assert.Equal(MachineSettingsTransfer.MaxBytes, new FileInfo(path).Length);

        Assert.True(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out _, out var error), error);
    }

    [Fact]
    public void TryRead_RefusesAFileOverTheLimit()
    {
        using var root = new TempDirectory();
        var json = new SettingsChangeSet().Set(ManagedSettingKeys.TenantId, "t").ToJson();
        var path = WriteFile(root, TransferName(), json.PadRight((int)MachineSettingsTransfer.MaxBytes + 1));

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.Equal($"'{path}' is larger than a settings change can be.", error);
    }

    [Fact]
    public void MaxBytes_Is256Kilobytes() =>
        Assert.Equal(256 * 1024, MachineSettingsTransfer.MaxBytes);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "DevConduit": { "DataRoot": "x" } }""")]
    [InlineData("""{ "set": { "AzureAd:TenantId": "t" }, "extra": 1 }""")]
    [InlineData("""{ "set": [ "AzureAd:TenantId" ] }""")]
    [InlineData("""{ "remove": [ 1 ] }""")]
    public void TryRead_RefusesABodyThatIsNotAChangeSet(string body)
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), body);

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(ManagedSettingKeys.LocalPort)]
    [InlineData("DevConduit:Local:SharedSecret")]
    [InlineData("Kestrel:Endpoints:Https:Certificate")]
    [InlineData("DevConduit:DataRoot")]
    [InlineData("AzureAd:ClientSecret")]
    [InlineData("DevConduit:Mode")]
    [InlineData("Kestrel:Endpoints:Http:Url")]
    [InlineData("DevConduit:Remotes:uat")]
    public void TryRead_RefusesKeysOutsideTheAllowList(string key)
    {
        using var root = new TempDirectory();
        var body = new SettingsChangeSet().Set(ManagedSettingKeys.TenantId, "t").Set(key, "x").ToJson();
        var path = WriteFile(root, TransferName(), body);

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out var changes, out var error));
        Assert.Null(changes);
        Assert.Equal($"The change touches keys the setup window does not manage: {key}.", error);
    }

    [Fact]
    public void TryRead_RefusesARemovalOutsideTheAllowList()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), new SettingsChangeSet().Remove("Logging").ToJson());

        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out _, out var error));
        Assert.Contains("Logging", error);
    }

    [Fact]
    public void TryRead_ChecksAgainstTheAllowListItIsGiven()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), new SettingsChangeSet().Set(ManagedSettingKeys.LocalPort, 9000).ToJson());

        Assert.True(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.User, out _, out _));
        Assert.False(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out _, out _));
    }

    [Fact]
    public void TryRead_LeavesTheFileInPlace()
    {
        using var root = new TempDirectory();
        var path = MachineSettingsTransfer.WriteTempFile(new SettingsChangeSet().Set(ManagedSettingKeys.TenantId, "t"), root.Path);

        Assert.True(MachineSettingsTransfer.TryRead(path, ManagedSettingKeys.Machine, out _, out _));
        Assert.True(File.Exists(path));
    }
}
