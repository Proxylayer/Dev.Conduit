using Proxylayer.DevConduit.Certificates;
using Proxylayer.DevConduit.Tests.Support;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary><c>imports.json</c>: written and read back, and tolerant of a missing or damaged file. Temp files only.</summary>
public sealed class ProxyConfigImportRecordsTests
{
    private static readonly DateTimeOffset Now = TestCertificates.Now;

    [Fact]
    public void Read_OfAMissingFile_IsEmpty()
    {
        using var root = new TempDirectory();

        Assert.Empty(ProxyConfigImportRecords.Read(Path.Combine(root.Path, "imports.json")));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Read_OfAFileThatIsNotAList_IsEmpty(string text)
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "imports.json");
        File.WriteAllText(path, text);

        Assert.Empty(ProxyConfigImportRecords.Read(path));
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "nested", "imports.json");
        ProxyConfigImportRecord[] records =
        [
            new("a.example", "a", ["api", "web"], Now, "10.0.0.5"),
            new("b.example", "b", [], Now.AddDays(1))
        ];

        ProxyConfigImportRecords.Write(path, records);
        var read = ProxyConfigImportRecords.Read(path);

        Assert.Equal(2, read.Count);
        Assert.Equal("a.example", read[0].Hostname);
        Assert.Equal("a", read[0].Remote);
        Assert.Equal(["api", "web"], read[0].Routes);
        Assert.Equal(Now, read[0].ImportedAt);
        Assert.Equal("10.0.0.5", read[0].Address);
        Assert.Null(read[1].Address);
        Assert.Empty(read[1].Routes);
    }

    [Fact]
    public void Write_UsesCamelCaseAndOmitsAMissingAddress()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "imports.json");

        ProxyConfigImportRecords.Write(path, [new("b.example", "b", [], Now)]);
        var text = File.ReadAllText(path);

        Assert.Contains("\"hostname\": \"b.example\"", text);
        Assert.DoesNotContain("address", text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void Read_OfARecordWrittenBeforeTheAddressWasKept_HasNoAddress()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "imports.json");
        File.WriteAllText(path, """[ { "hostname": "a.example", "remote": "a", "routes": [ "api" ], "importedAt": "2026-06-01T12:00:00+00:00" } ]""");

        var record = Assert.Single(ProxyConfigImportRecords.Read(path));

        Assert.Null(record.Address);
        Assert.Equal(["api"], record.Routes);
    }
}
