using Proxylayer.DevConduit.Hosting;

namespace Proxylayer.DevConduit.Tests.Hosting;

/// <summary>Splitting a verb's own options from the configuration arguments that follow it.</summary>
public sealed class CommandArgumentsTests
{
    private static readonly string[] Options = ["--out", "--address"];

    [Fact]
    public void TakesASpaceSeparatedOption()
    {
        Assert.True(CommandArguments.TryTake(["--out", @"C:\x.conduit", "--DevConduit:DataRoot", "D"], Options, out var options, out var rest, out var error));

        Assert.Null(error);
        Assert.Equal(@"C:\x.conduit", options["--out"]);
        Assert.Equal(["--DevConduit:DataRoot", "D"], rest);
    }

    [Fact]
    public void TakesAnEqualsSeparatedOption()
    {
        Assert.True(CommandArguments.TryTake(["--address=10.0.0.5"], Options, out var options, out var rest, out _));

        Assert.Equal("10.0.0.5", options["--address"]);
        Assert.Empty(rest);
    }

    [Fact]
    public void KeepsEverythingAfterTheFirstEqualsSign()
    {
        Assert.True(CommandArguments.TryTake(["--out=C:\\a=b.conduit"], Options, out var options, out _, out _));

        Assert.Equal(@"C:\a=b.conduit", options["--out"]);
    }

    [Fact]
    public void MatchesOptionNamesIgnoringCase()
    {
        Assert.True(CommandArguments.TryTake(["--OUT", "x", "--Address=y"], Options, out var options, out _, out _));

        Assert.Equal("x", options["--out"]);
        Assert.Equal("y", options["--ADDRESS"]);
    }

    [Fact]
    public void TakesAnEmptyValueAfterEquals()
    {
        Assert.True(CommandArguments.TryTake(["--address="], Options, out var options, out _, out _));

        Assert.Equal(string.Empty, options["--address"]);
    }

    [Fact]
    public void TakesTheNextArgumentAsTheValueEvenWhenItLooksLikeAnOption()
    {
        Assert.True(CommandArguments.TryTake(["--out", "--address"], Options, out var options, out var rest, out _));

        Assert.Equal("--address", options["--out"]);
        Assert.False(options.ContainsKey("--address"));
        Assert.Empty(rest);
    }

    [Fact]
    public void LeavesAnArgumentThatOnlyStartsWithAnOptionName()
    {
        Assert.True(CommandArguments.TryTake(["--output", "x", "--addresses=y"], Options, out var options, out var rest, out _));

        Assert.Empty(options);
        Assert.Equal(["--output", "x", "--addresses=y"], rest);
    }

    [Fact]
    public void KeepsTheOrderOfTheRest()
    {
        Assert.True(CommandArguments.TryTake(["a", "--out", "o", "b", "--address", "x", "c"], Options, out _, out var rest, out _));

        Assert.Equal(["a", "b", "c"], rest);
    }

    [Fact]
    public void WithNothingToTake_PassesEverythingOn()
    {
        Assert.True(CommandArguments.TryTake([], Options, out var options, out var rest, out var error));

        Assert.Empty(options);
        Assert.Empty(rest);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("--ADDRESS")]
    public void RefusesAnOptionWithoutAValue(string last)
    {
        Assert.False(CommandArguments.TryTake(["--DevConduit:X", "1", last], Options, out _, out var rest, out var error));

        Assert.Equal($"{(last == "--out" ? "--out" : "--address")} needs a value.", error);
        Assert.Empty(rest);
    }

    [Theory]
    [InlineData("--out", "a", "--out", "b")]
    [InlineData("--out=a", "--OUT=b")]
    [InlineData("--address", "x", "--address=y")]
    public void RefusesAnOptionGivenTwice(params string[] args)
    {
        Assert.False(CommandArguments.TryTake(args, Options, out _, out var rest, out var error));

        Assert.EndsWith("is given twice.", error);
        Assert.Empty(rest);
    }
}
