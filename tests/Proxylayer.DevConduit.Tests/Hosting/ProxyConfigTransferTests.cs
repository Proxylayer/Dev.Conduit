using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Hosting;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>
/// The transfer file that carries a <c>.conduit</c> to the elevated helper: which paths are
/// accepted, what is read, and what is never deleted. Every file is under a throwaway directory.
/// </summary>
public sealed class ProxyConfigTransferTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    private static string TransferName(string hex = Hex) => $"Dev.Conduit-import-{hex}.conduit";

    private static string WriteFile(TempDirectory root, string name, byte[] content)
    {
        var path = Path.Combine(root.Path, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---- WriteTempFile / Delete ----------------------------------------------------------

    [Fact]
    public void WriteTempFile_CopiesTheSourceToAProperlyNamedFile()
    {
        using var root = new TempDirectory();
        var source = WriteFile(root, "host.conduit", "{ \"format\": \"dev-conduit/1\" }"u8.ToArray());

        var path = ProxyConfigTransfer.WriteTempFile(source, root.Path);

        Assert.Equal(root.Path, Path.GetDirectoryName(path));
        Assert.Matches("^Dev\\.Conduit-import-[0-9a-f]{32}\\.conduit$", Path.GetFileName(path));
        Assert.True(ProxyConfigTransfer.IsTransferPath(path));
        Assert.True(ProxyConfigTransfer.TryRead(path, out var content, out var error), error);
        Assert.Equal(File.ReadAllBytes(source), content);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void WriteTempFile_GivesEveryCopyANewName()
    {
        using var root = new TempDirectory();
        var source = WriteFile(root, "host.conduit", [1]);

        Assert.NotEqual(ProxyConfigTransfer.WriteTempFile(source, root.Path), ProxyConfigTransfer.WriteTempFile(source, root.Path));
    }

    [Fact]
    public void WriteTempFile_RefusesASourceLargerThanAClientConfigurationCanBe()
    {
        using var root = new TempDirectory();
        var source = WriteFile(root, "big.conduit", new byte[ProxyConfigDocument.MaxBytes + 1]);

        Assert.Throws<InvalidDataException>(() => ProxyConfigTransfer.WriteTempFile(source, root.Path));
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void WriteTempFile_RefusesAMissingSource()
    {
        using var root = new TempDirectory();

        Assert.Throws<FileNotFoundException>(() => ProxyConfigTransfer.WriteTempFile(Path.Combine(root.Path, "missing.conduit"), root.Path));
    }

    [Fact]
    public void Delete_RemovesATransferFile()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), [1]);

        ProxyConfigTransfer.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("host.conduit")]
    [InlineData("Dev.Conduit-import-0123456789ABCDEF0123456789ABCDEF.conduit")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit.bak")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    public void Delete_LeavesAnyOtherFileAlone(string name)
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, name, [1]);

        ProxyConfigTransfer.Delete(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Delete_IgnoresAMissingFileAndARelativePath()
    {
        using var root = new TempDirectory();

        ProxyConfigTransfer.Delete(Path.Combine(root.Path, TransferName()));
        ProxyConfigTransfer.Delete(TransferName());
        ProxyConfigTransfer.Delete(string.Empty);
    }

    // ---- IsTransferPath ------------------------------------------------------------------

    [Theory]
    [InlineData(Hex)]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    public void IsTransferPath_AcceptsAFullPathToAProperlyNamedFile(string hex) =>
        Assert.True(ProxyConfigTransfer.IsTransferPath(Path.Combine(@"C:\Temp", TransferName(hex))));

    [Fact]
    public void IsTransferPath_AcceptsTheLegacyProxyconfigExtension() =>
        Assert.True(ProxyConfigTransfer.IsTransferPath(Path.Combine(@"C:\Temp", $"Dev.Conduit-import-{Hex}.proxyconfig")));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("")]
    public void IsTransferPath_RefusesAnythingButThirtyTwoLowerCaseHexDigits(string hex) =>
        Assert.False(ProxyConfigTransfer.IsTransferPath(Path.Combine(@"C:\Temp", TransferName(hex))));

    [Theory]
    [InlineData("host.conduit")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.json")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit.conduit")]
    [InlineData("dev.conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.CONDUIT")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.PROXYCONFIG")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.proxyconfig.conduit")]
    [InlineData("xDev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData("Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json")]
    public void IsTransferPath_RefusesAnyOtherFileName(string name) =>
        Assert.False(ProxyConfigTransfer.IsTransferPath(Path.Combine(@"C:\Temp", name)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData(@"temp\Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData(@"..\Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData(@"C:Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    [InlineData(@"\Dev.Conduit-import-0123456789abcdef0123456789abcdef.conduit")]
    public void IsTransferPath_RefusesABlankOrRelativePath(string? path) =>
        Assert.False(ProxyConfigTransfer.IsTransferPath(path));

    // ---- TryRead -------------------------------------------------------------------------

    [Fact]
    public void TryRead_RefusesAFileNotNamedAsATransfer()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, "host.conduit", [1]);

        Assert.False(ProxyConfigTransfer.TryRead(path, out var content, out var error));
        Assert.Empty(content);
        Assert.Contains("is not a .conduit transfer file", error);
    }

    [Fact]
    public void TryRead_RefusesADirectoryNamedAsATransfer()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, TransferName());
        Directory.CreateDirectory(path);

        Assert.False(ProxyConfigTransfer.TryRead(path, out _, out var error));
        Assert.Contains("is not an ordinary file", error);
    }

    [Fact]
    public void TryRead_RefusesAMissingFile()
    {
        using var root = new TempDirectory();

        Assert.False(ProxyConfigTransfer.TryRead(Path.Combine(root.Path, TransferName()), out _, out var error));
        Assert.Contains("is not an ordinary file", error);
    }

    [Fact]
    public void TryRead_AcceptsAFileOfExactlyTheLimit()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), new byte[ProxyConfigDocument.MaxBytes]);

        Assert.True(ProxyConfigTransfer.TryRead(path, out var content, out _));
        Assert.Equal(ProxyConfigDocument.MaxBytes, content.Length);
    }

    [Fact]
    public void TryRead_RefusesAFileOverTheLimit()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), new byte[ProxyConfigDocument.MaxBytes + 1]);

        Assert.False(ProxyConfigTransfer.TryRead(path, out var content, out var error));
        Assert.Empty(content);
        Assert.Contains("could not be read", error);
    }

    [Fact]
    public void TryRead_LeavesTheFileInPlace()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, TransferName(), [1, 2, 3]);

        Assert.True(ProxyConfigTransfer.TryRead(path, out var content, out _));

        Assert.Equal([1, 2, 3], content);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReadLimited_ReadsAnEmptyFile()
    {
        using var root = new TempDirectory();
        var path = WriteFile(root, "empty", []);

        Assert.Empty(ProxyConfigTransfer.ReadLimited(path));
    }
}
