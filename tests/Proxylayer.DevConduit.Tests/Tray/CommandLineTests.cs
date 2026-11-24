using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Tests.Tray;

/// <summary>
/// Quoting arguments into one Windows command line, checked against the system's own parser,
/// <c>CommandLineToArgvW</c>, and against known expected strings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandLineTests
{
    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// Splits a command line as Windows does. A program name goes first because the parser
    /// treats the first token by different rules, and is dropped from the result.
    /// </summary>
    private static string[] Parse(string arguments)
    {
        var argv = CommandLineToArgv("program.exe " + arguments, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);

        try
        {
            return [.. Enumerable.Range(1, count - 1).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    public static TheoryData<string> SingleArguments =>
    [
        "plain",
        "",
        " ",
        "two words",
        "tab\there",
        "line\nfeed",
        "vertical\vtab",
        "\"",
        "\"\"",
        "say \"hi\"",
        "a\"b",
        @"a\b",
        @"a\\b",
        @"trailing\",
        @"trailing\\",
        @"with space\",
        @"with space\\",
        @"C:\Program Files\Proxylayer\Dev.Conduit\",
        @"\\server\share\folder\",
        @"\""",
        @"\\""",
        @"a\""b",
        @"a\\""b",
        @"""\",
        "-environment:Uat",
        @"C:\Users\x\AppData\Local\Temp\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json",
        "ünïcödé ✓"
    ];

    [Theory]
    [MemberData(nameof(SingleArguments))]
    public void Quote_RoundTripsThroughTheWindowsParser(string argument) =>
        Assert.Equal([argument], Parse(CommandLine.Quote(argument)));

    [Fact]
    public void Join_RoundTripsSeveralArgumentsIncludingEmptyOnes()
    {
        string[] arguments =
        [
            "save",
            @"C:\Temp Folder\Dev.Conduit-settings-0123456789abcdef0123456789abcdef.json",
            "",
            "Uat",
            @"ends with\",
            "has \"quotes\" inside",
            ""
        ];

        Assert.Equal(arguments, Parse(CommandLine.Join(arguments)));
    }

    [Fact]
    public void Join_OfNothingIsEmpty() =>
        Assert.Equal(string.Empty, CommandLine.Join([]));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\path\file.json", @"C:\path\file.json")]
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"with space\", "\"with space\\\\\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void Quote_ProducesTheExpectedText(string argument, string expected) =>
        Assert.Equal(expected, CommandLine.Quote(argument));

    [Fact]
    public void Join_SeparatesWithSingleSpaces() =>
        Assert.Equal("save \"C:\\a b\\x.json\" Uat", CommandLine.Join(["save", @"C:\a b\x.json", "Uat"]));

    [Fact]
    public void Quote_RefusesNull() =>
        Assert.Throws<ArgumentNullException>(() => CommandLine.Quote(null!));
}
