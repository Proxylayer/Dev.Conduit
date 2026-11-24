using Proxylayer.DevConduit.Services;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Services;

/// <summary>
/// The guard that stops a caller-supplied identifier from escaping the data root.
/// Anything that reaches the filesystem goes through here first.
/// </summary>
public sealed class UserStoragePathsTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("local")]
    [InlineData("00000000-1111-2222-3333-444444444444")]
    [InlineData("Mixed-Case_Name.9")]
    [InlineData("..a")]
    [InlineData("a..")]
    [InlineData("...")]
    public void IsSafeSegment_AcceptsTheAllowedCharacters(string value) =>
        Assert.True(UserStoragePaths.IsSafeSegment(value));

    [Fact]
    public void IsSafeSegment_AcceptsExactlyTheMaximumLength() =>
        Assert.True(UserStoragePaths.IsSafeSegment(new string('a', 128)));

    [Fact]
    public void IsSafeSegment_RejectsOneCharacterBeyondTheMaximum() =>
        Assert.False(UserStoragePaths.IsSafeSegment(new string('a', 129)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsSafeSegment_RejectsNothing(string? value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void IsSafeSegment_RejectsTheDirectoryShorthands(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\..\\windows")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("\\\\server\\share")]
    public void IsSafeSegment_RejectsPathSeparatorsAndTraversal(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("C:")]
    [InlineData("C:\\")]
    [InlineData("C:temp")]
    [InlineData("\\\\?\\C:\\temp")]
    public void IsSafeSegment_RejectsDriveRelativeForms(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("\u0430bc")]           // Cyrillic small a
    [InlineData("\uFF41bc")]           // Fullwidth latin small a
    [InlineData("a\u00A0b")]           // Non-breaking space
    [InlineData("\u202Eabc")]          // Right-to-left override
    [InlineData("ab\uFF0Fcd")]         // Fullwidth solidus
    [InlineData("\u2024\u2024")]       // One-dot leaders posing as dots
    public void IsSafeSegment_RejectsUnicodeLookalikes(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("a b")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("%2e%2e")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    public void IsSafeSegment_RejectsEverythingOutsideTheAllowlist(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    public void IsSafeSegment_RejectsTrailingLineBreaks(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM5")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT5")]
    [InlineData("LPT9")]
    public void IsSafeSegment_RejectsTheWindowsReservedDeviceNames(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("con")]
    [InlineData("Nul")]
    [InlineData("cOm1")]
    [InlineData("lpt9")]
    public void IsSafeSegment_RejectsTheReservedNamesWhateverTheCase(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("nul.json")]
    [InlineData("COM1.")]
    [InlineData("LPT1.tar.gz")]
    public void IsSafeSegment_RejectsTheReservedNamesWithAnExtension(string value) =>
        Assert.False(UserStoragePaths.IsSafeSegment(value));

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("CON1")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT")]
    [InlineData("NULL")]
    [InlineData("PRNT")]
    [InlineData("my-CON")]
    [InlineData("a.CON")]
    public void IsSafeSegment_StillAcceptsNamesThatMerelyStartLikeADevice(string value) =>
        Assert.True(UserStoragePaths.IsSafeSegment(value));

    [Fact]
    public void UserDirectory_RefusesAReservedDeviceName()
    {
        using var temp = new TempDirectory();
        var paths = new UserStoragePaths(TestOptions.Create(options => options.DataRoot = temp.Path));

        Assert.Throws<ArgumentException>(() => paths.UserDirectory("CON"));
        Assert.Throws<ArgumentException>(() => paths.HeaderSetsFile("con.txt"));
        Assert.Throws<ArgumentException>(() => paths.HistoryDirectory("LPT3"));
        Assert.Throws<ArgumentException>(() => paths.RoutedDirectory("nul"));
    }

    [Fact]
    public void UserDirectory_PlacesTheUserUnderTheDataRoot()
    {
        using var temp = new TempDirectory();
        var paths = new UserStoragePaths(TestOptions.Create(options => options.DataRoot = temp.Path));

        var directory = paths.UserDirectory("user-1");

        Assert.Equal(Path.Combine(temp.Path, "users", "user-1"), directory);
        Assert.Equal(Path.Combine(directory, "headers.json"), paths.HeaderSetsFile("user-1"));
        Assert.Equal(Path.Combine(directory, "history"), paths.HistoryDirectory("user-1"));
        Assert.Equal(Path.Combine(directory, "routed"), paths.RoutedDirectory("user-1"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData("")]
    public void UserDirectory_RefusesAnIdentifierThatCouldEscapeTheRoot(string userId)
    {
        using var temp = new TempDirectory();
        var paths = new UserStoragePaths(TestOptions.Create(options => options.DataRoot = temp.Path));

        Assert.Throws<ArgumentException>(() => paths.UserDirectory(userId));
        Assert.Throws<ArgumentException>(() => paths.HeaderSetsFile(userId));
        Assert.Throws<ArgumentException>(() => paths.HistoryDirectory(userId));
        Assert.Throws<ArgumentException>(() => paths.RoutedDirectory(userId));
    }

    [Fact]
    public void DataRoot_FallsBackToTheLocalApplicationDataFolder()
    {
        var paths = new UserStoragePaths(TestOptions.Create());

        Assert.EndsWith(Path.Combine("Proxylayer", "Dev.Conduit"), paths.DataRoot);
        Assert.True(Path.IsPathFullyQualified(paths.DataRoot));
    }
}
