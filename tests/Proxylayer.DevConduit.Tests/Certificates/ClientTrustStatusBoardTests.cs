using Proxylayer.DevConduit.Certificates;

namespace Proxylayer.DevConduit.Tests.Certificates;

/// <summary>The latest status per imported server, and the event raised when one changes.</summary>
public sealed class ClientTrustStatusBoardTests
{
    private static ClientTrustStatus Status(string hostname, bool rootUpdate = false, string? error = null) =>
        new(hostname, 1, rootUpdate, null, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), error);

    [Fact]
    public void ANewBoard_IsEmpty()
    {
        var board = new ClientTrustStatusBoard();

        Assert.Empty(board.Current);
        Assert.False(board.RootStoreUpdateNeeded);
    }

    [Fact]
    public void Set_AddsOneStatusPerHostIgnoringCase()
    {
        var board = new ClientTrustStatusBoard();

        board.Set(Status("a.example"));
        board.Set(Status("A.EXAMPLE", error: "later"));

        var only = Assert.Single(board.Current);
        Assert.Equal("later", only.LastError);
    }

    [Fact]
    public void Current_IsOrderedByHostname()
    {
        var board = new ClientTrustStatusBoard();

        board.Set(Status("c.example"));
        board.Set(Status("A.example"));
        board.Set(Status("b.example"));

        Assert.Equal(["A.example", "b.example", "c.example"], board.Current.Select(status => status.Hostname));
    }

    [Fact]
    public void RootStoreUpdateNeeded_IsTrueWhenAnyServerNeedsIt()
    {
        var board = new ClientTrustStatusBoard();
        board.Set(Status("a.example"));
        Assert.False(board.RootStoreUpdateNeeded);

        board.Set(Status("b.example", rootUpdate: true));
        Assert.True(board.RootStoreUpdateNeeded);

        board.Set(Status("b.example", rootUpdate: false));
        Assert.False(board.RootStoreUpdateNeeded);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void UpdateNeeded_IsTrueForWindowsTrustOrAMachineIdentityRenewal(bool rootUpdate, bool renewalDue, bool expected)
    {
        var status = Status("a.example", rootUpdate) with { MachineIdentityRenewalDue = renewalDue };

        Assert.Equal(expected, status.UpdateNeeded);
    }

    [Fact]
    public void UpdateNeeded_IsTrueWhenAnyServerNeedsEither()
    {
        var board = new ClientTrustStatusBoard();
        board.Set(Status("a.example"));
        Assert.False(board.UpdateNeeded);

        board.Set(Status("b.example") with { MachineIdentityRenewalDue = true });
        Assert.True(board.UpdateNeeded);
        Assert.False(board.RootStoreUpdateNeeded);

        board.Set(Status("b.example"));
        Assert.False(board.UpdateNeeded);
    }

    [Fact]
    public void MachineIdentityRenewalDue_DefaultsToFalse() =>
        Assert.False(Status("a.example").MachineIdentityRenewalDue);

    [Fact]
    public void Set_RaisesChangedEveryTimeWithTheBoardAsSender()
    {
        var board = new ClientTrustStatusBoard();
        var senders = new List<object?>();
        board.Changed += (sender, _) => senders.Add(sender);

        board.Set(Status("a.example"));
        board.Set(Status("a.example"));

        Assert.Equal(2, senders.Count);
        Assert.All(senders, sender => Assert.Same(board, sender));
    }

    [Fact]
    public void Changed_SeesTheNewStatusAlready()
    {
        var board = new ClientTrustStatusBoard();
        ClientTrustStatus? seen = null;
        board.Changed += (_, _) => seen = board.Current.Single();

        board.Set(Status("a.example", error: "boom"));

        Assert.Equal("boom", seen?.LastError);
    }

    [Fact]
    public void Set_WithoutSubscribers_DoesNotThrow()
    {
        var board = new ClientTrustStatusBoard();

        board.Set(Status("a.example"));

        Assert.Single(board.Current);
    }

    [Fact]
    public void Status_IsAValueRecord() =>
        Assert.Equal(Status("a.example"), Status("a.example"));
}
