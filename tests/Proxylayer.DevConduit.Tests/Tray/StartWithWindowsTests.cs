using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Proxylayer.DevConduit.Tests.Support;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Tests.Tray;

/// <summary>
/// The "start with Windows" entry, written to a scratch key the test creates under
/// HKCU\Software\Proxylayer.DevConduit.Tests and deletes afterwards. The real Run key is never opened.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartWithWindowsTests : IDisposable
{
    private const string ScratchRoot = @"Software\Proxylayer.DevConduit.Tests";
    private const string Executable = @"C:\Program Files\Proxylayer\Dev.Conduit\Dev.Conduit.exe";

    private readonly string _scratch = $@"{ScratchRoot}\{Guid.NewGuid():N}";

    private string RunKey => $@"{_scratch}\Run";

    private StartWithWindows Entry(string executable = Executable) => new(Registry.CurrentUser, RunKey, executable);

    private object? StoredValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(StartWithWindows.ValueName);
    }

    private void Store(object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(StartWithWindows.ValueName, value, kind);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_scratch, throwOnMissingSubKey: false);

        using var root = Registry.CurrentUser.OpenSubKey(ScratchRoot);
        if (root is { SubKeyCount: 0, ValueCount: 0 })
        {
            Registry.CurrentUser.DeleteSubKey(ScratchRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Constants_NameTheCurrentUsersRunKeyValue()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartWithWindows.RunKeyPath);
        Assert.Equal("Dev.Conduit", StartWithWindows.ValueName);
    }

    [Fact]
    public void Command_QuotesTheExecutableAndAddsTheTrayVerb() =>
        Assert.Equal($"\"{Executable}\" tray", Entry().Command);

    [Fact]
    public void Constructor_RefusesMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new StartWithWindows(null!, RunKey, Executable));
        Assert.ThrowsAny<ArgumentException>(() => new StartWithWindows(Registry.CurrentUser, " ", Executable));
        Assert.ThrowsAny<ArgumentException>(() => new StartWithWindows(Registry.CurrentUser, RunKey, ""));
    }

    [Fact]
    public void IsEnabled_IsFalseWhenTheKeyDoesNotExist()
    {
        var entry = Entry();

        Assert.False(entry.IsEnabled);
        Assert.False(entry.PointsElsewhere);
    }

    [Fact]
    public void Enable_CreatesTheKeyAndWritesTheCommand()
    {
        var entry = Entry();

        entry.Enable();

        Assert.Equal(entry.Command, StoredValue());
        Assert.True(entry.IsEnabled);
        Assert.False(entry.PointsElsewhere);
    }

    [Fact]
    public void Enable_ReplacesAnEntryForAnotherExecutable()
    {
        Store("\"D:\\old\\Dev.Conduit.exe\" tray");
        var entry = Entry();
        Assert.True(entry.PointsElsewhere);

        entry.Enable();

        Assert.Equal(entry.Command, StoredValue());
        Assert.False(entry.PointsElsewhere);
    }

    [Fact]
    public void PointsElsewhere_IsTrueForAnotherExecutable()
    {
        Store("\"D:\\old\\Dev.Conduit.exe\" tray");

        var entry = Entry();

        Assert.True(entry.IsEnabled);
        Assert.True(entry.PointsElsewhere);
    }

    [Fact]
    public void PointsElsewhere_IgnoresCaseAndSurroundingWhitespace()
    {
        Store($"  \"{Executable.ToUpperInvariant()}\" TRAY  ");

        var entry = Entry();

        Assert.True(entry.IsEnabled);
        Assert.False(entry.PointsElsewhere);
    }

    [Fact]
    public void IsEnabled_IsFalseForAnEmptyOrNonStringValue()
    {
        Store(string.Empty);
        Assert.False(Entry().IsEnabled);

        Store(1, RegistryValueKind.DWord);
        Assert.False(Entry().IsEnabled);
        Assert.False(Entry().PointsElsewhere);
    }

    [Fact]
    public void Disable_RemovesTheValueAndLeavesOtherValues()
    {
        var entry = Entry();
        entry.Enable();
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!)
        {
            key.SetValue("SomethingElse", "keep");
        }

        entry.Disable();

        Assert.Null(StoredValue());
        Assert.False(entry.IsEnabled);
        using var after = Registry.CurrentUser.OpenSubKey(RunKey)!;
        Assert.Equal("keep", after.GetValue("SomethingElse"));
    }

    // ---- The installer's entry for every user --------------------------------------------

    private static XElement InstallerRunValue()
    {
        var runValues = InstallerSource.Document()
            .Descendants(InstallerSource.Wix + "RegistryValue")
            .Where(value => string.Equals((string?)value.Attribute("Key"), StartWithWindows.RunKeyPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Assert.Single(runValues);
    }

    [Fact]
    public void ForAllUsers_StartsTheSameCommandAsTheCurrentUsersEntry() =>
        // Only the command is exposed; the hive and key path are private, so the HKLM entry
        // itself is not opened here.
        Assert.Equal(StartWithWindows.ForCurrentUser().Command, StartWithWindows.ForAllUsers().Command);

    [Fact]
    public void InstallerRunEntry_IsTheMachinesRunKeyValueTheTrayLooksFor()
    {
        var value = InstallerRunValue();

        Assert.Equal("HKLM", (string?)value.Attribute("Root"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(StartWithWindows.ValueName, (string?)value.Attribute("Name"));
        Assert.Equal("string", (string?)value.Attribute("Type"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerRunEntry_ForThisExe_DoesNotPointElsewhere()
    {
        var template = (string?)InstallerRunValue().Attribute("Value");
        Assert.NotNull(template);
        Assert.Contains("[#", template, StringComparison.Ordinal);

        // The value as Windows Installer writes it, with the file reference resolved to the exe.
        Store(Regex.Replace(template, @"\[#[^\]]+\]", Executable.Replace("$", "$$", StringComparison.Ordinal)));

        Assert.True(Entry().IsEnabled);
        Assert.False(Entry().PointsElsewhere);
        Assert.True(Entry(@"D:\other\Dev.Conduit.exe").PointsElsewhere);
    }

    [Fact]
    public void Disable_DoesNothingWhenThereIsNoEntryOrKey()
    {
        var entry = Entry();

        entry.Disable();
        Assert.Null(Registry.CurrentUser.OpenSubKey(RunKey));

        using (Registry.CurrentUser.CreateSubKey(RunKey))
        {
        }

        entry.Disable();
        Assert.False(entry.IsEnabled);
    }
}
