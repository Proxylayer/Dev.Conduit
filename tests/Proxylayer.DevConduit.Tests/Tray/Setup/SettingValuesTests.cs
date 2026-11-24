using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Proxylayer.DevConduit.Configuration;
using Proxylayer.DevConduit.Tray.Setup;

namespace Proxylayer.DevConduit.Tests.Tray.Setup;

/// <summary>How an edited value becomes a change to a settings file.</summary>
public sealed class SettingValuesTests
{
    private const string Key = "Section:Key";

    private static IConfiguration Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static void AssertRemoved(SettingsChangeSet changes)
    {
        Assert.Empty(changes.Values);
        Assert.Equal([Key], changes.Removed);
    }

    private static JsonNode AssertSet(SettingsChangeSet changes)
    {
        Assert.Empty(changes.Removed);
        return Assert.Single(changes.Values, pair => pair.Key == Key).Value;
    }

    // ---- Text / Switch / Number ----------------------------------------------------------

    [Theory]
    [InlineData(null, "inherited")]
    [InlineData("", "inherited")]
    [InlineData("   ", "inherited")]
    [InlineData("inherited", "inherited")]
    [InlineData("  inherited  ", " inherited ")]
    [InlineData("", null)]
    public void Text_RemovesABlankValueOrOneEqualToTheInheritedValue(string? value, string? inherited)
    {
        var changes = new SettingsChangeSet();

        SettingValues.Text(changes, Key, value, inherited);

        AssertRemoved(changes);
    }

    [Theory]
    [InlineData("mine", "inherited", "mine")]
    [InlineData("  mine ", null, "mine")]
    [InlineData("Inherited", "inherited", "Inherited")]
    public void Text_SetsAnyOtherValueTrimmed(string value, string? inherited, string expected)
    {
        var changes = new SettingsChangeSet();

        SettingValues.Text(changes, Key, value, inherited);

        Assert.Equal(expected, AssertSet(changes).GetValue<string>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Switch_RemovesAValueEqualToTheInheritedOne(bool value, bool inherited)
    {
        var changes = new SettingsChangeSet();

        SettingValues.Switch(changes, Key, value, inherited);

        AssertRemoved(changes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Switch_SetsADifferingValue(bool value, bool inherited)
    {
        var changes = new SettingsChangeSet();

        SettingValues.Switch(changes, Key, value, inherited);

        Assert.Equal(value, AssertSet(changes).GetValue<bool>());
    }

    [Fact]
    public void Number_RemovesAnEqualValueAndSetsADifferingOne()
    {
        var same = new SettingsChangeSet();
        SettingValues.Number(same, Key, 8092, 8092);
        AssertRemoved(same);

        var different = new SettingsChangeSet();
        SettingValues.Number(different, Key, 9000, 8092);
        Assert.Equal(9000, AssertSet(different).GetValue<int>());
    }

    // ---- List / ListNode -----------------------------------------------------------------

    [Fact]
    public void List_RemovesAnEmptyList()
    {
        var changes = new SettingsChangeSet();

        SettingValues.List(changes, Key, [], Json("""{ "Section": { "Key": [ "a" ] } }"""));

        AssertRemoved(changes);
    }

    [Fact]
    public void List_RemovesAListEqualToTheInheritedOneIgnoringCaseAndBlanks()
    {
        var changes = new SettingsChangeSet();

        SettingValues.List(changes, Key, ["A", "b"], Json("""{ "Section": { "Key": [ "a", "", " B " ] } }"""));

        AssertRemoved(changes);
    }

    [Fact]
    public void List_PadsAShorterListToTheInheritedLengthSoTheMergeHoldsExactlyTheNewEntries()
    {
        var below = Json("""{ "Section": { "Key": [ "a", "b", "c" ] } }""");
        var changes = new SettingsChangeSet();

        SettingValues.List(changes, Key, ["x"], below);

        var node = AssertSet(changes);
        Assert.True(JsonNode.DeepEquals(new JsonArray("x", "", ""), node), node.ToJsonString());

        var merged = new ConfigurationBuilder()
            .AddConfiguration(below)
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(new JsonObject { ["Section"] = new JsonObject { ["Key"] = node.DeepClone() } }.ToJsonString())))
            .Build();
        Assert.Equal(["x"], SettingValues.ReadList(merged.GetSection(Key)));
    }

    [Fact]
    public void ListNode_IsAPlainArrayWhenNothingIsInherited()
    {
        var node = SettingValues.ListNode(["a"], Json("{}").GetSection(Key));

        Assert.True(JsonNode.DeepEquals(new JsonArray("a"), node));
    }

    [Fact]
    public void ListNode_IsAPlainArrayWhenItIsLongerThanTheInheritedList()
    {
        var node = SettingValues.ListNode(["a", "b", "c"], Json("""{ "Section": { "Key": [ "x" ] } }""").GetSection(Key));

        Assert.True(JsonNode.DeepEquals(new JsonArray("a", "b", "c"), node));
    }

    [Theory]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new string[0], "")]
    public void ListNode_IsAStringOverAnInheritedString(string[] values, string expected)
    {
        var node = SettingValues.ListNode(values, Json("""{ "Section": { "Key": "single" } }""").GetSection(Key));

        Assert.Equal(expected, node.GetValue<string>());
    }

    [Fact]
    public void ListNode_IsAnArrayForSeveralValuesOverAnInheritedString()
    {
        var node = SettingValues.ListNode(["a", "b"], Json("""{ "Section": { "Key": "single" } }""").GetSection(Key));

        Assert.True(JsonNode.DeepEquals(new JsonArray("a", "b"), node));
    }

    [Fact]
    public void ListNode_ForAnEmptyListOverAnInheritedListIsAllBlanks()
    {
        var node = SettingValues.ListNode([], Json("""{ "Section": { "Key": [ "a", "b" ] } }""").GetSection(Key));

        Assert.True(JsonNode.DeepEquals(new JsonArray("", ""), node));
    }

    // ---- ReadList / Split / SameList -----------------------------------------------------

    [Fact]
    public void ReadList_ReadsTheNonBlankEntriesTrimmed() =>
        Assert.Equal(["a", "b"], SettingValues.ReadList(Json("""{ "Section": { "Key": [ " a ", "", "  ", "b" ] } }""").GetSection(Key)));

    [Fact]
    public void ReadList_ReadsASingleStringAsOneEntry() =>
        Assert.Equal(["only"], SettingValues.ReadList(Json("""{ "Section": { "Key": " only " } }""").GetSection(Key)));

    [Fact]
    public void ReadList_IsEmptyForAMissingOrBlankSetting()
    {
        Assert.Empty(SettingValues.ReadList(Json("{}").GetSection(Key)));
        Assert.Empty(SettingValues.ReadList(Json("""{ "Section": { "Key": "  " } }""").GetSection(Key)));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData(" a , b ", new[] { "a", "b" })]
    [InlineData("a\r\nb\nc\r", new[] { "a", "b", "c" })]
    [InlineData(",, a ,,\r\n\r\n", new[] { "a" })]
    public void Split_ReadsCommaAndLineSeparatedEntries(string? text, string[] expected) =>
        Assert.Equal(expected, SettingValues.Split(text));

    [Fact]
    public void SameList_ComparesInOrderIgnoringCase()
    {
        Assert.True(SettingValues.SameList(["A", "b"], ["a", "B"]));
        Assert.False(SettingValues.SameList(["a", "b"], ["b", "a"]));
        Assert.False(SettingValues.SameList(["a"], ["a", "b"]));
    }

    // ---- SetProperty / RemoveProperty / Find ---------------------------------------------

    [Fact]
    public void SetProperty_ReplacesAPropertyWhoseNameDiffersOnlyInCase()
    {
        var target = new JsonObject { ["endpoint"] = "old", ["Other"] = 1 };

        SettingValues.SetProperty(target, "Endpoint", "new");

        Assert.Equal(["Other", "Endpoint"], target.Select(property => property.Key));
        Assert.Equal("new", target["Endpoint"]!.GetValue<string>());
    }

    [Fact]
    public void RemoveProperty_RemovesEveryCaseOfTheName()
    {
        var target = new JsonObject { ["Enabled"] = false, ["enabled"] = true, ["Other"] = 1 };

        SettingValues.RemoveProperty(target, "ENABLED");

        Assert.Equal(["Other"], target.Select(property => property.Key));
    }

    [Fact]
    public void Find_ReturnsTheObjectAtAPathIgnoringCase()
    {
        var document = SettingsJson.Parse("""{ "devconduit": { "remotes": { "uat": "x" } } }""");

        var found = SettingValues.Find(document, "DevConduit:Remotes");

        Assert.NotNull(found);
        Assert.Equal("x", found["uat"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("DevConduit:Missing")]
    [InlineData("DevConduit:Remotes:uat")]
    [InlineData("DevConduit:Remotes:uat:Endpoint")]
    public void Find_ReturnsNullForAMissingPathOrANonObject(string path)
    {
        var document = SettingsJson.Parse("""{ "DevConduit": { "Remotes": { "uat": "x" } } }""");

        Assert.Null(SettingValues.Find(document, path));
    }
}
