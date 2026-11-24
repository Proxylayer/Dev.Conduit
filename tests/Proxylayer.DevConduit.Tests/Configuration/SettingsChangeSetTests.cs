using System.Text.Json;
using System.Text.Json.Nodes;
using Proxylayer.DevConduit.Configuration;

namespace Proxylayer.DevConduit.Tests.Configuration;

/// <summary>
/// Setting and removing settings by configuration path, the JSON form handed to the elevated
/// helper, and how settings documents are parsed.
/// </summary>
public sealed class SettingsChangeSetTests
{
    private static JsonObject Document(string json) => SettingsJson.Parse(json);

    private static void AssertJson(string expected, JsonNode actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"Expected {expected} but got {actual.ToJsonString()}");

    private static string[] PropertyNames(JsonObject node) => [.. node.Select(property => property.Key)];

    // ---- SettingsJson --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Parse_TreatsBlankTextAsAnEmptyDocument(string text) =>
        Assert.Empty(SettingsJson.Parse(text));

    [Fact]
    public void Parse_ToleratesCommentsAndTrailingCommas()
    {
        var document = SettingsJson.Parse("""
            {
              // line comment
              /* block comment */
              "DevConduit": { "Local": { "Port": 9000, }, },
            }
            """);

        Assert.Equal(9000, document["DevConduit"]!["Local"]!["Port"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_MatchesPropertyNamesIgnoringCase()
    {
        var document = SettingsJson.Parse("""{ "DevConduit": { "Local": { "Port": 1 } } }""");

        Assert.NotNull(document["devconduit"]);
        Assert.Equal(1, document["DEVCONDUIT"]!["local"]!["PORT"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    public void Parse_RefusesJsonThatIsNotAnObject(string text) =>
        Assert.Throws<InvalidDataException>(() => SettingsJson.Parse(text));

    [Fact]
    public void Parse_RefusesTextThatIsNotJson() =>
        Assert.ThrowsAny<JsonException>(() => SettingsJson.Parse("{ nope"));

    [Fact]
    public void WriteOptions_IndentAndLeaveUrlsUnescaped()
    {
        var json = new JsonObject { ["Url"] = "https://host/path?a=1&b=<2>+'3'" }.ToJsonString(SettingsJson.WriteOptions);

        Assert.Contains("https://host/path?a=1&b=<2>+'3'", json);
        Assert.Contains(Environment.NewLine, json);
    }

    // ---- Set -----------------------------------------------------------------------------

    [Fact]
    public void Set_CreatesTheObjectsAlongAPath()
    {
        var document = Document("{}");

        new SettingsChangeSet().Set("DevConduit:Local:Port", 9000).ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Local": { "Port": 9000 } } }""", document);
    }

    [Fact]
    public void Set_WritesStringsBooleansAndNumbers()
    {
        var document = Document("{}");

        new SettingsChangeSet()
            .Set("A:Text", "value")
            .Set("A:Switch", false)
            .Set("A:Number", 42)
            .ApplyTo(document);

        AssertJson("""{ "A": { "Text": "value", "Switch": false, "Number": 42 } }""", document);
    }

    [Fact]
    public void Set_ReplacesAValueAndLeavesEveryOtherKeyAlone()
    {
        var document = Document("""
            {
              "AzureAd": { "TenantId": "t" },
              "DevConduit": { "Local": { "Port": 8092, "CaptureEnabled": true }, "DataRoot": "D:\\data" }
            }
            """);

        new SettingsChangeSet().Set("DevConduit:Local:Port", 9000).ApplyTo(document);

        AssertJson("""
            {
              "AzureAd": { "TenantId": "t" },
              "DevConduit": { "Local": { "Port": 9000, "CaptureEnabled": true }, "DataRoot": "D:\\data" }
            }
            """, document);
    }

    [Fact]
    public void Set_ReplacesAWholeSection()
    {
        var document = Document("""
            { "DevConduit": { "Remotes": { "uat": "https://uat", "prod": { "Endpoint": "https://prod" } }, "DataRoot": "x" } }
            """);

        new SettingsChangeSet()
            .Set("DevConduit:Remotes", new JsonObject { ["dev"] = "https://dev" })
            .ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Remotes": { "dev": "https://dev" }, "DataRoot": "x" } }""", document);
    }

    [Fact]
    public void Set_KeepsTheExistingCaseOfEveryPropertyAlongThePath()
    {
        var document = Document("""{ "devconduit": { "LOCAL": { "port": 1, "Other": 2 } } }""");

        new SettingsChangeSet().Set("DevConduit:Local:Port", 9000).ApplyTo(document);

        Assert.Equal(["devconduit"], PropertyNames(document));
        var local = document["devconduit"]!["LOCAL"]!.AsObject();
        Assert.Equal(["port", "Other"], PropertyNames(local));
        Assert.Equal(9000, local["port"]!.GetValue<int>());
    }

    [Fact]
    public void Set_ReplacesAValueThatStandsWhereAnObjectIsNeeded()
    {
        var document = Document("""{ "DevConduit": { "Local": "not an object" } }""");

        new SettingsChangeSet().Set("DevConduit:Local:Port", 9000).ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Local": { "Port": 9000 } } }""", document);
    }

    [Fact]
    public void Set_CopiesTheValue()
    {
        var value = new JsonObject { ["uat"] = "https://uat" };
        var changes = new SettingsChangeSet().Set("DevConduit:Remotes", value);

        value["prod"] = "https://prod";

        var document = Document("{}");
        changes.ApplyTo(document);
        AssertJson("""{ "DevConduit": { "Remotes": { "uat": "https://uat" } } }""", document);
    }

    [Fact]
    public void Set_ThenRemoveTheSameKeyInAnotherCaseKeepsOnlyTheRemoval()
    {
        var changes = new SettingsChangeSet().Set("DevConduit:Local:Port", 1).Remove("devconduit:local:port");

        Assert.Empty(changes.Values);
        Assert.Equal(["devconduit:local:port"], changes.Removed);
    }

    [Fact]
    public void Remove_ThenSetTheSameKeyKeepsOnlyTheValue()
    {
        var changes = new SettingsChangeSet().Remove("DevConduit:Local:Port").Set("DEVCONDUIT:LOCAL:PORT", 1);

        Assert.Empty(changes.Removed);
        Assert.Equal(1, Assert.Single(changes.Values).Value.GetValue<int>());
    }

    [Fact]
    public void Set_TrimsTheKey()
    {
        var changes = new SettingsChangeSet().Set("  DevConduit:Local:Port  ", 1);

        Assert.Equal("DevConduit:Local:Port", Assert.Single(changes.Keys));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DevConduit::Port")]
    [InlineData(":Port")]
    [InlineData("DevConduit:")]
    [InlineData("DevConduit: Local")]
    [InlineData("DevConduit :Local")]
    public void SetAndRemove_RefuseAKeyThatIsNotAPath(string key)
    {
        Assert.Throws<ArgumentException>(() => new SettingsChangeSet().Set(key, "x"));
        Assert.Throws<ArgumentException>(() => new SettingsChangeSet().Remove(key));
    }

    [Fact]
    public void Set_RefusesANullNode() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsChangeSet().Set("A", (JsonNode)null!));

    [Fact]
    public void IsEmpty_IsTrueOnlyBeforeAnyChange()
    {
        Assert.True(new SettingsChangeSet().IsEmpty);
        Assert.False(new SettingsChangeSet().Set("A", 1).IsEmpty);
        Assert.False(new SettingsChangeSet().Remove("A").IsEmpty);
    }

    [Fact]
    public void Keys_ListsBothSetAndRemovedPaths()
    {
        var changes = new SettingsChangeSet().Set("A:B", 1).Remove("C:D");

        Assert.Equal(["A:B", "C:D"], changes.Keys.Order(StringComparer.Ordinal));
    }

    // ---- Remove --------------------------------------------------------------------------

    [Fact]
    public void Remove_DeletesAValueAndLeavesItsSiblings()
    {
        var document = Document("""{ "DevConduit": { "Local": { "Port": 1, "CaptureEnabled": true } } }""");

        new SettingsChangeSet().Remove("DevConduit:Local:Port").ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Local": { "CaptureEnabled": true } } }""", document);
    }

    [Fact]
    public void Remove_DeletesAWholeSection()
    {
        var document = Document("""
            { "DevConduit": { "Remotes": { "uat": "https://uat" }, "Routes": { "a": "http://a" } } }
            """);

        new SettingsChangeSet().Remove("DevConduit:Remotes").ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Routes": { "a": "http://a" } } }""", document);
    }

    [Fact]
    public void Remove_PrunesEveryParentItLeavesEmpty()
    {
        var document = Document("""{ "AzureAd": { "TenantId": "t" }, "DevConduit": { "Local": { "Port": 1 } } }""");

        new SettingsChangeSet().Remove("DevConduit:Local:Port").ApplyTo(document);

        AssertJson("""{ "AzureAd": { "TenantId": "t" } }""", document);
    }

    [Fact]
    public void Remove_StopsPruningAtTheFirstParentThatStillHoldsSomething()
    {
        var document = Document("""{ "DevConduit": { "DataRoot": "x", "Local": { "Port": 1 } } }""");

        new SettingsChangeSet().Remove("DevConduit:Local:Port").ApplyTo(document);

        AssertJson("""{ "DevConduit": { "DataRoot": "x" } }""", document);
    }

    [Fact]
    public void Remove_LeavesAnEmptyRootWhenItRemovesTheLastKey()
    {
        var document = Document("""{ "DevConduit": { "Local": { "Port": 1 } } }""");

        new SettingsChangeSet().Remove("DevConduit:Local:Port").ApplyTo(document);

        Assert.Empty(document);
    }

    [Fact]
    public void Remove_DoesNotPruneAnEmptyObjectItDidNotEmpty()
    {
        var document = Document("""{ "DevConduit": { "Local": {}, "DataRoot": "x" }, "Empty": {} }""");

        new SettingsChangeSet().Remove("DevConduit:DataRoot").ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Local": {} }, "Empty": {} }""", document);
    }

    [Fact]
    public void Remove_MatchesPropertyNamesIgnoringCase()
    {
        var document = Document("""{ "devconduit": { "local": { "port": 1 } }, "Other": 1 }""");

        new SettingsChangeSet().Remove("DevConduit:Local:Port").ApplyTo(document);

        AssertJson("""{ "Other": 1 }""", document);
    }

    [Theory]
    [InlineData("Missing:Path")]
    [InlineData("DevConduit:Missing")]
    [InlineData("DevConduit:DataRoot:Below")]
    public void Remove_IgnoresAPathThatIsNotThere(string key)
    {
        const string Original = """{ "DevConduit": { "DataRoot": "x" } }""";
        var document = Document(Original);

        new SettingsChangeSet().Remove(key).ApplyTo(document);

        AssertJson(Original, document);
    }

    [Fact]
    public void ApplyTo_RemovesBeforeItSets()
    {
        var document = Document("""{ "DevConduit": { "Local": { "Port": 1 } } }""");

        new SettingsChangeSet()
            .Set("DevConduit:Local:ForwardProxyPort", 2)
            .Remove("DevConduit:Local:Port")
            .ApplyTo(document);

        AssertJson("""{ "DevConduit": { "Local": { "ForwardProxyPort": 2 } } }""", document);
    }

    [Fact]
    public void ApplyTo_RefusesANullDocument() =>
        Assert.Throws<ArgumentNullException>(() => new SettingsChangeSet().ApplyTo(null!));

    // ---- ToJson / TryParse ---------------------------------------------------------------

    [Fact]
    public void ToJson_WritesTheSetAndRemoveForm()
    {
        var json = new SettingsChangeSet().Set("A:B", "x").Remove("C").ToJson();

        AssertJson("""{ "set": { "A:B": "x" }, "remove": [ "C" ] }""", JsonNode.Parse(json)!);
    }

    [Fact]
    public void ToJsonAndTryParse_RoundTripEveryKindOfValue()
    {
        var original = new SettingsChangeSet()
            .Set("DevConduit:Local:Port", 9000)
            .Set("DevConduit:Local:CaptureEnabled", true)
            .Set("AzureAd:TenantId", "contoso.onmicrosoft.com")
            .Set("DevConduit:Remotes", new JsonObject { ["uat"] = new JsonObject { ["Endpoint"] = "https://uat", ["Enabled"] = false } })
            .Set("DevConduit:AllowedUserOids", new JsonArray("a", "", ""))
            .Remove("DevConduit:Routes")
            .Remove("DevConduit:DefaultRemote");

        Assert.True(SettingsChangeSet.TryParse(original.ToJson(), out var parsed, out var error), error);

        Assert.Equal(original.Values.Keys.Order(), parsed.Values.Keys.Order());
        foreach (var (key, value) in original.Values)
        {
            Assert.True(JsonNode.DeepEquals(value, parsed.Values[key]), key);
        }

        Assert.Equal(original.Removed.Order(), parsed.Removed.Order());
        Assert.Equal(original.ToJson(), parsed.ToJson());
    }

    [Fact]
    public void ToJsonAndTryParse_RoundTripAnEmptyChangeSet()
    {
        Assert.True(SettingsChangeSet.TryParse(new SettingsChangeSet().ToJson(), out var parsed, out var error), error);
        Assert.True(parsed.IsEmpty);
    }

    [Fact]
    public void TryParse_ToleratesCommentsAndTrailingCommas()
    {
        const string Json = """
            {
              // the values
              "set": { "A:B": 1, },
              "remove": [ "C", ],
            }
            """;

        Assert.True(SettingsChangeSet.TryParse(Json, out var parsed, out var error), error);
        Assert.Equal(1, parsed.Values["A:B"].GetValue<int>());
        Assert.Equal(["C"], parsed.Removed);
    }

    [Fact]
    public void TryParse_AcceptsEitherPartAlone()
    {
        Assert.True(SettingsChangeSet.TryParse("""{ "set": { "A": 1 } }""", out var setOnly, out _));
        Assert.Single(setOnly.Values);
        Assert.True(SettingsChangeSet.TryParse("""{ "remove": [ "A" ] }""", out var removeOnly, out _));
        Assert.Single(removeOnly.Removed);
        Assert.True(SettingsChangeSet.TryParse("{}", out var empty, out _));
        Assert.True(empty.IsEmpty);
    }

    [Theory]
    [InlineData("not json", "The change set is not valid JSON:")]
    [InlineData("[]", "The change set must be a JSON object.")]
    [InlineData("\"set\"", "The change set must be a JSON object.")]
    [InlineData("""{ "other": {} }""", "The change set has an unexpected property 'other'.")]
    [InlineData("""{ "Set": {} }""", "The change set has an unexpected property 'Set'.")]
    [InlineData("""{ "set": [] }""", "The change set has an unexpected property 'set'.")]
    [InlineData("""{ "remove": {} }""", "The change set has an unexpected property 'remove'.")]
    [InlineData("""{ "set": { "A": null } }""", "The change set gives 'A' no value.")]
    [InlineData("""{ "remove": [ 1 ] }""", "Every entry under 'remove' must be a string.")]
    [InlineData("""{ "remove": [ null ] }""", "Every entry under 'remove' must be a string.")]
    [InlineData("""{ "remove": [ { } ] }""", "Every entry under 'remove' must be a string.")]
    [InlineData("""{ "set": { "A::B": 1 } }""", "'A::B' is not a settings key")]
    [InlineData("""{ "remove": [ "" ] }""", "A settings key cannot be blank.")]
    public void TryParse_RefusesAnythingElseSayingWhy(string json, string expected)
    {
        Assert.False(SettingsChangeSet.TryParse(json, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.StartsWith(expected, error);
    }
}
