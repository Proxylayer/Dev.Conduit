using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// The hosts file on disk, always a throwaway copy: writes replace it in one step, keep its
/// access list, retry while another process holds it, and leave no temporary file behind.
/// The real hosts file is never opened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostsFileTests : IDisposable
{
    private const string Host = "devconduit.example.internal";
    private const string Stock = "# hosts\r\n127.0.0.1 localhost\r\n";

    private readonly TempDirectory _root = new();
    private readonly string _path;
    private readonly HostsFile _hosts;

    public HostsFileTests()
    {
        var etc = Path.Combine(_root.Path, "drivers", "etc");
        Directory.CreateDirectory(etc);
        _path = Path.Combine(etc, "hosts");
        _hosts = new HostsFile(_path);
    }

    public void Dispose() => _root.Dispose();

    private void WriteOriginal(byte[] content) => File.WriteAllBytes(_path, content);

    private string[] Leftovers() =>
        [.. Directory.GetFiles(Path.GetDirectoryName(_path)!).Select(Path.GetFileName).Where(name => name != "hosts")!];

    [Fact]
    public void ForThisComputer_IsTheSystemHostsFile() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts"),
            HostsFile.ForThisComputer().Path);

    [Fact]
    public void SetEntry_WritesTheManagedLine()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));

        var edit = _hosts.SetEntry("10.0.0.5", Host);

        Assert.True(edit.Changed);
        Assert.Equal(Stock + HostsFileText.ManagedLine("10.0.0.5", Host) + "\r\n", File.ReadAllText(_path));
        Assert.Equal("10.0.0.5", _hosts.ManagedAddress(Host));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void SetEntry_CreatesAMissingFile()
    {
        var edit = _hosts.SetEntry("10.0.0.5", Host);

        Assert.True(edit.Changed);
        Assert.Equal(HostsFileText.ManagedLine("10.0.0.5", Host) + "\r\n", File.ReadAllText(_path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void SetEntry_WhenAlreadyRight_DoesNotWrite()
    {
        _hosts.SetEntry("10.0.0.5", Host);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, stamp);

        var edit = _hosts.SetEntry("10.0.0.5", Host);

        Assert.False(edit.Changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void SetEntry_WritesUtf8WithoutAByteOrderMark()
    {
        _hosts.SetEntry("10.0.0.5", Host);

        var bytes = File.ReadAllBytes(_path);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void SetThenRemove_RestoresTheFileByteForByte()
    {
        var original = Encoding.ASCII.GetBytes(Stock + "10.1.1.1\tbox # mine\r\n\r\n");
        WriteOriginal(original);

        _hosts.SetEntry("10.0.0.5", Host);
        var removal = _hosts.RemoveEntry(Host);

        Assert.True(removal.Changed);
        Assert.Equal(original, File.ReadAllBytes(_path));
        Assert.Null(_hosts.ManagedAddress(Host));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void SetThenRemove_RestoresAFileWithAByteOrderMark()
    {
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.ASCII.GetBytes(Stock)).ToArray();
        WriteOriginal(original);

        _hosts.SetEntry("10.0.0.5", Host);
        _hosts.RemoveEntry(Host);

        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void SetEntry_KeepsAByteOrderMarkAndAddsAsciiAfterIt()
    {
        WriteOriginal([.. Encoding.UTF8.GetPreamble(), .. Encoding.ASCII.GetBytes(Stock)]);

        _hosts.SetEntry("10.0.0.5", Host);

        Assert.Equal(
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.ASCII.GetBytes(Stock + HostsFileText.ManagedLine("10.0.0.5", Host) + "\r\n")],
            File.ReadAllBytes(_path));
        Assert.Equal("10.0.0.5", _hosts.ManagedAddress(Host));
    }

    [Theory]
    [InlineData(new byte[] { 0x23, 0x20, 0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A, 0x31, 0x30, 0x2E, 0x31, 0x2E, 0x31, 0x2E, 0x31, 0x20, 0xE9, 0x0D, 0x0A })]
    [InlineData(new byte[] { 0x23, 0x20, 0xC3, 0xA9, 0x20, 0x80, 0x9F, 0xFF, 0x0A, 0x23, 0x20, 0x78 })]
    public void SetThenRemove_RestoresAnsiAndUtf8BytesExactly(byte[] original)
    {
        WriteOriginal(original);

        _hosts.SetEntry("10.0.0.5", Host);
        Assert.Equal("10.0.0.5", _hosts.ManagedAddress(Host));
        Assert.Equal(original[..^2], File.ReadAllBytes(_path)[..(original.Length - 2)]);
        _hosts.RemoveEntry(Host);

        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void SetThenRemove_RestoresAFileWithoutAFinalNewline()
    {
        var original = Encoding.ASCII.GetBytes("# hosts\n127.0.0.1 localhost");
        WriteOriginal(original);

        _hosts.SetEntry("10.0.0.5", Host);
        Assert.Equal("# hosts\n127.0.0.1 localhost\n" + HostsFileText.ManagedLine("10.0.0.5", Host), File.ReadAllText(_path));
        _hosts.RemoveEntry(Host);

        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void SetEntry_RefusesANonAsciiHostnameAndWritesNothing()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));

        Assert.Throws<ArgumentException>(() => _hosts.SetEntry("10.0.0.5", "h\u00f6st.example"));

        Assert.Equal(Stock, File.ReadAllText(_path));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void RemoveEntry_OfAMissingFile_ChangesNothing()
    {
        var edit = _hosts.RemoveEntry(Host);

        Assert.False(edit.Changed);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void RemoveEntry_WithoutAManagedLine_DoesNotWrite()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, stamp);

        Assert.False(_hosts.RemoveEntry(Host).Changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void ManagedAddress_OfAMissingFile_IsNull() =>
        Assert.Null(_hosts.ManagedAddress(Host));

    [Fact]
    public void Write_KeepsTheFilesOwnAccessList()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(_path).SetAccessControl(security);

        _hosts.SetEntry("10.0.0.5", Host);

        var after = new FileInfo(_path).GetAccessControl(AccessControlSections.Access);
        Assert.True(after.AreAccessRulesProtected);
        var sids = after.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value }.Order(StringComparer.Ordinal),
            sids);
    }

    [Fact]
    public void Write_RetriesWhileAnotherProcessHoldsTheFile()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));

        // A scanner-like open: readers and writers allowed, deletion and replacement not.
        var holder = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var release = new Timer(_ => holder.Dispose(), null, 500, Timeout.Infinite);

        var edit = _hosts.SetEntry("10.0.0.5", Host);

        Assert.True(edit.Changed);
        Assert.Equal("10.0.0.5", _hosts.ManagedAddress(Host));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Write_GivesUpAfterItsRetriesWhileTheFileStaysHeld()
    {
        WriteOriginal(Encoding.ASCII.GetBytes(Stock));
        using var holder = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.ThrowsAny<Exception>(() => _hosts.SetEntry("10.0.0.5", Host));

        holder.Dispose();
        Assert.Equal(Stock, File.ReadAllText(_path));
        Assert.Empty(Leftovers());
    }
}
