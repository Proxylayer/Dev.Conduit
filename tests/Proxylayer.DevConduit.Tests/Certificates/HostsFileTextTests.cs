using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>
/// Editing the text of a hosts file: the single managed line per host is added, replaced or
/// removed, and every other line is left as it was. Pure; no file is read or written.
/// </summary>
public sealed class HostsFileTextTests
{
    private const string Host = "devconduit.example.internal";
    private const string Marker = HostsFileText.Marker;

    private static string Managed(string address, string hostname = Host) => $"{address}\t{hostname}\t{Marker}";

    private const string StockHosts =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "# localhost name resolution is handled within DNS itself.\r\n" +
        "#\t127.0.0.1       localhost\r\n" +
        "#\t::1             localhost\r\n";

    [Fact]
    public void Marker_IsTheAppsComment() =>
        Assert.Equal("# Dev.Conduit", HostsFileText.Marker);

    [Fact]
    public void ManagedLine_IsAddressTabHostTabMarker() =>
        Assert.Equal($"10.0.0.5\t{Host}\t# Dev.Conduit", HostsFileText.ManagedLine("10.0.0.5", Host));

    // ---- SetEntry: adding ----------------------------------------------------------------

    [Fact]
    public void SetEntry_AppendsTheLineKeepingTheFinalNewline()
    {
        var edit = HostsFileText.SetEntry(StockHosts, "10.0.0.5", Host);

        Assert.True(edit.Changed);
        Assert.Equal(StockHosts + Managed("10.0.0.5") + "\r\n", edit.Text);
        Assert.Empty(edit.ShadowingLines);
    }

    [Fact]
    public void SetEntry_OnAnEmptyFile_WritesOneCrLfTerminatedLine()
    {
        var edit = HostsFileText.SetEntry(string.Empty, "10.0.0.5", Host);

        Assert.Equal(Managed("10.0.0.5") + "\r\n", edit.Text);
        Assert.True(edit.Changed);
    }

    [Fact]
    public void SetEntry_KeepsLfLineEndingsInAnLfFile()
    {
        var edit = HostsFileText.SetEntry("127.0.0.1\tlocalhost\n", "10.0.0.5", Host);

        Assert.Equal("127.0.0.1\tlocalhost\n" + Managed("10.0.0.5") + "\n", edit.Text);
    }

    [Fact]
    public void SetEntry_OnAFileWithoutAFinalNewline_AddsTheLineWithoutOne()
    {
        var edit = HostsFileText.SetEntry("127.0.0.1\tlocalhost", "10.0.0.5", Host);

        Assert.Equal("127.0.0.1\tlocalhost\r\n" + Managed("10.0.0.5"), edit.Text);
    }

    [Fact]
    public void SetEntry_OnAnLfFileWithoutAFinalNewline_SeparatesWithLf()
    {
        var edit = HostsFileText.SetEntry("a\nb", "10.0.0.5", Host);

        Assert.Equal("a\nb\n" + Managed("10.0.0.5"), edit.Text);
    }

    [Fact]
    public void SetEntry_UsesTheDominantLineEnding()
    {
        Assert.EndsWith("c\r\n" + Managed("10.0.0.5") + "\n", HostsFileText.SetEntry("a\nb\nc\r\n", "10.0.0.5", Host).Text);
        Assert.EndsWith("c\n" + Managed("10.0.0.5") + "\r\n", HostsFileText.SetEntry("a\r\nb\r\nc\n", "10.0.0.5", Host).Text);
    }

    [Fact]
    public void SetEntry_ReplacingALine_KeepsThatLinesEnding()
    {
        var before = "a\r\n" + Managed("10.0.0.1") + "\nb\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.2", Host);

        Assert.Equal("a\r\n" + Managed("10.0.0.2") + "\nb\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_ReplacingAnUnterminatedLastLine_KeepsItUnterminated()
    {
        var edit = HostsFileText.SetEntry("a\r\n" + Managed("10.0.0.1"), "10.0.0.2", Host);

        Assert.Equal("a\r\n" + Managed("10.0.0.2"), edit.Text);
    }

    [Theory]
    [InlineData("10.0.0.5", "h\u00f6st.example")]
    [InlineData("10.0.0.\u0665", Host)]
    [InlineData("10.0.0.5 extra", Host)]
    [InlineData("10.0.0.5#", Host)]
    [InlineData("", Host)]
    public void SetEntry_RefusesAnythingButPlainAscii(string address, string hostname) =>
        Assert.Throws<ArgumentException>(() => HostsFileText.SetEntry(string.Empty, address, hostname));

    [Fact]
    public void SetEntry_LeavesNonAsciiCharactersOfOtherLinesAlone()
    {
        const string before = "# caf\u00e9 \u00c3\u00a9\r\n127.0.0.1 localhost\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.5", Host);

        Assert.Equal(before + Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_StoresTheHostnameNormalized()
    {
        var edit = HostsFileText.SetEntry(string.Empty, "10.0.0.5", " DevConduit.Example.Internal. ");

        Assert.Equal(Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_AcceptsAnIPv6Address()
    {
        var edit = HostsFileText.SetEntry(string.Empty, "fd00::5", Host);

        Assert.Equal(Managed("fd00::5") + "\r\n", edit.Text);
        Assert.Equal("fd00::5", HostsFileText.ManagedAddress(edit.Text, Host));
    }

    // ---- SetEntry: replacing -------------------------------------------------------------

    [Fact]
    public void SetEntry_ReplacesTheManagedLineInPlace()
    {
        var before = "a\r\n" + Managed("10.0.0.1") + "\r\nb\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.2", Host);

        Assert.True(edit.Changed);
        Assert.Equal("a\r\n" + Managed("10.0.0.2") + "\r\nb\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_RecognisesTheManagedLineWhateverItsSpacingAndCase()
    {
        var before = $"a\r\n10.0.0.1    {Host.ToUpperInvariant()}   {Marker}   \r\nb\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.2", Host);

        Assert.Equal("a\r\n" + Managed("10.0.0.2") + "\r\nb\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_WhenAlreadyRight_IsUnchangedAndIdentical()
    {
        var once = HostsFileText.SetEntry(StockHosts, "10.0.0.5", Host);

        var twice = HostsFileText.SetEntry(once.Text, "10.0.0.5", Host);

        Assert.False(twice.Changed);
        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void SetEntry_DropsDuplicateManagedLinesForTheHost()
    {
        var before = Managed("10.0.0.1") + "\r\nkeep\r\n" + Managed("10.0.0.9") + "\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.2", Host);

        Assert.Equal(Managed("10.0.0.2") + "\r\nkeep\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_LeavesAnotherHostsManagedLineAlone()
    {
        var other = Managed("10.0.0.7", "other.devconduit.internal");

        var edit = HostsFileText.SetEntry(other + "\r\n", "10.0.0.5", Host);

        Assert.Equal(other + "\r\n" + Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_DoesNotTakeOverALineThatOnlyMapsTheHostWithoutTheMarker()
    {
        var before = $"10.9.9.9 {Host}\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.5", Host);

        Assert.StartsWith(before, edit.Text);
        Assert.EndsWith(Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    [Fact]
    public void SetEntry_LeavesEveryOtherLineByteForByte()
    {
        const string before = "# comment  with  spaces\r\n\r\n   \r\n127.0.0.1 localhost # trailing comment\r\n\t\t10.1.1.1\tbox\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.5", Host);

        Assert.Equal(before + Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    // ---- Shadowing -----------------------------------------------------------------------

    [Fact]
    public void SetEntry_ReportsAnEarlierUnmanagedLineForTheHost()
    {
        var shadow = $"10.9.9.9\t{Host}";

        var edit = HostsFileText.SetEntry("a\r\n" + shadow + "\r\n", "10.0.0.5", Host);

        Assert.Equal([shadow], edit.ShadowingLines);
    }

    [Fact]
    public void SetEntry_ReportsAHostAmongSeveralNamesOnALine()
    {
        var shadow = $"10.9.9.9 first.example {Host.ToUpperInvariant()}";

        var edit = HostsFileText.SetEntry(shadow + "\r\n", "10.0.0.5", Host);

        Assert.Equal([shadow], edit.ShadowingLines);
    }

    [Fact]
    public void SetEntry_IgnoresACommentedOutMapping()
    {
        var edit = HostsFileText.SetEntry($"# 10.9.9.9 {Host}\r\n", "10.0.0.5", Host);

        Assert.Empty(edit.ShadowingLines);
    }

    [Fact]
    public void SetEntry_DoesNotReportALineAfterTheManagedOne()
    {
        var before = Managed("10.0.0.1") + $"\r\n10.9.9.9 {Host}\r\n";

        var edit = HostsFileText.SetEntry(before, "10.0.0.2", Host);

        Assert.Empty(edit.ShadowingLines);
    }

    // ---- RemoveEntry ---------------------------------------------------------------------

    [Fact]
    public void RemoveEntry_RemovesOnlyTheManagedLineForTheHost()
    {
        var other = Managed("10.0.0.7", "other.devconduit.internal");
        var before = $"a\r\n{Managed("10.0.0.5")}\r\n10.9.9.9 {Host}\r\n{other}\r\n";

        var edit = HostsFileText.RemoveEntry(before, Host.ToUpperInvariant());

        Assert.True(edit.Changed);
        Assert.Equal($"a\r\n10.9.9.9 {Host}\r\n{other}\r\n", edit.Text);
    }

    [Fact]
    public void RemoveEntry_RemovesEveryDuplicate()
    {
        var before = Managed("10.0.0.1") + "\nkeep\n" + Managed("10.0.0.2") + "\n";

        Assert.Equal("keep\n", HostsFileText.RemoveEntry(before, Host).Text);
    }

    [Fact]
    public void RemoveEntry_WithoutAManagedLine_ReturnsTheSameTextUnchanged()
    {
        var edit = HostsFileText.RemoveEntry(StockHosts, Host);

        Assert.False(edit.Changed);
        Assert.Same(StockHosts, edit.Text);
    }

    [Theory]
    [InlineData(StockHosts)]
    [InlineData("")]
    [InlineData("127.0.0.1 localhost\n")]
    [InlineData("127.0.0.1 localhost\r\n\r\n")]
    public void SetThenRemove_RestoresAFileEndingInANewlineExactly(string original)
    {
        var added = HostsFileText.SetEntry(original, "10.0.0.5", Host).Text;

        Assert.Equal(original, HostsFileText.RemoveEntry(added, Host).Text);
    }

    [Fact]
    public void SetThenRemove_RestoresAFileWithoutAFinalNewlineExactly()
    {
        const string original = "127.0.0.1 localhost";

        var added = HostsFileText.SetEntry(original, "10.0.0.5", Host).Text;

        Assert.Equal(original, HostsFileText.RemoveEntry(added, Host).Text);
    }

    [Theory]
    [InlineData("a\r\nb\nc")]
    [InlineData("a\nb\r\nc\r\n")]
    [InlineData("a\r\r\nb\n")]
    [InlineData("\r\n\n\r\n")]
    public void SetThenRemove_RestoresMixedLineEndingsExactly(string original)
    {
        var added = HostsFileText.SetEntry(original, "10.0.0.5", Host).Text;

        Assert.Equal(original, HostsFileText.RemoveEntry(added, Host).Text);
    }

    [Fact]
    public void SetEntry_KeepsMixedLineEndingsAsTheyWere()
    {
        const string original = "a\r\nb\nc\r\n";

        var edit = HostsFileText.SetEntry(original, "10.0.0.5", Host);

        Assert.StartsWith(original, edit.Text);
        Assert.Equal(original + Managed("10.0.0.5") + "\r\n", edit.Text);
    }

    [Fact]
    public void RemoveEntry_OfAnUnterminatedLastManagedLine_LeavesTheFileUnterminated()
    {
        var edit = HostsFileText.RemoveEntry("a\r\nb\r\n" + Managed("10.0.0.5"), Host);

        Assert.Equal("a\r\nb", edit.Text);
    }

    // ---- ManagedAddress / IsManagedFor / MapsHostname ------------------------------------

    [Fact]
    public void ManagedAddress_IsTheAddressOfTheManagedLine()
    {
        var text = $"10.9.9.9 {Host}\r\n{Managed("10.0.0.5")}\r\n";

        Assert.Equal("10.0.0.5", HostsFileText.ManagedAddress(text, Host));
        Assert.Equal("10.0.0.5", HostsFileText.ManagedAddress(text, Host.ToUpperInvariant() + "."));
    }

    [Fact]
    public void ManagedAddress_IsNullWithoutAManagedLine()
    {
        Assert.Null(HostsFileText.ManagedAddress(StockHosts, Host));
        Assert.Null(HostsFileText.ManagedAddress($"10.9.9.9 {Host}\r\n", Host));
        Assert.Null(HostsFileText.ManagedAddress(Managed("10.0.0.7", "other.example"), Host));
    }

    [Theory]
    [InlineData("10.0.0.5\tdevconduit.example.internal\t# Dev.Conduit", true)]
    [InlineData("10.0.0.5 DEVCONDUIT.example.internal # Dev.Conduit  ", true)]
    [InlineData("10.0.0.5 devconduit.example.internal", false)]
    [InlineData("10.0.0.5 devconduit.example.internal # Dev.Conduit extra", false)]
    [InlineData("devconduit.example.internal # Dev.Conduit", false)]
    [InlineData("10.0.0.5 other.example # Dev.Conduit", false)]
    [InlineData("10.0.0.5 devconduit.example.internal # dev.conduit", false)]
    public void IsManagedFor_NeedsTheExactMarkerAndTheHostAfterTheAddress(string line, bool expected) =>
        Assert.Equal(expected, HostsFileText.IsManagedFor(line, Host));

    [Theory]
    [InlineData("10.0.0.5 devconduit.example.internal", true)]
    [InlineData("10.0.0.5\tfirst devconduit.example.internal # note", true)]
    [InlineData("# 10.0.0.5 devconduit.example.internal", false)]
    [InlineData("devconduit.example.internal", false)]
    [InlineData("10.0.0.5 devconduit.example.internal.other", false)]
    [InlineData("", false)]
    public void MapsHostname_IsAnActiveLineNamingTheHost(string line, bool expected) =>
        Assert.Equal(expected, HostsFileText.MapsHostname(line, Host));

    [Fact]
    public void Methods_RefuseANullText()
    {
        Assert.Throws<ArgumentNullException>(() => HostsFileText.SetEntry(null!, "10.0.0.5", Host));
        Assert.Throws<ArgumentNullException>(() => HostsFileText.RemoveEntry(null!, Host));
        Assert.Throws<ArgumentNullException>(() => HostsFileText.ManagedAddress(null!, Host));
    }
}
