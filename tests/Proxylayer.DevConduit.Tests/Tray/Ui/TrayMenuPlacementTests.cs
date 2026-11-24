using System.Drawing;
using System.Reflection;
using Proxylayer.DevConduit.Tray;

namespace Proxylayer.DevConduit.Tests.Tray.Ui;

/// <summary>
/// Where the tray menu goes, in physical pixels. The placement is internal to the app, so it is
/// reached by reflection; it is pure geometry and creates no window.
/// </summary>
public sealed class TrayMenuPlacementTests
{
    private static readonly MethodInfo LocateMethod =
        typeof(ConsoleAddresses).Assembly.GetType("Proxylayer.DevConduit.Tray.Ui.TrayMenuPlacement", throwOnError: true)!
            .GetMethod("Locate", BindingFlags.Public | BindingFlags.Static)!;

    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);
    private static readonly Size Menu = new(200, 300);

    private static Point Locate(Point anchor, Size menu, Rectangle monitor) =>
        (Point)LocateMethod.Invoke(null, [anchor, menu, monitor])!;

    [Fact]
    public void TaskbarAtTheBottom_PutsTheMenuAboveAndLeftWithItsCornerOnTheAnchor()
    {
        var anchor = new Point(1880, 1050);

        var location = Locate(anchor, Menu, Monitor);

        Assert.Equal(new Point(1680, 750), location);
        Assert.Equal(anchor, location + Menu);
    }

    [Fact]
    public void TaskbarAtTheTop_FlipsTheMenuBelow() =>
        Assert.Equal(new Point(1680, 30), Locate(new Point(1880, 30), Menu, Monitor));

    [Fact]
    public void TaskbarAtTheLeft_FlipsTheMenuRight() =>
        Assert.Equal(new Point(30, 750), Locate(new Point(30, 1050), Menu, Monitor));

    [Fact]
    public void TopLeftCorner_FlipsBothWays() =>
        Assert.Equal(new Point(10, 10), Locate(new Point(10, 10), Menu, Monitor));

    [Fact]
    public void ExactlyEnoughRoom_DoesNotFlip() =>
        Assert.Equal(new Point(0, 0), Locate(new Point(200, 300), Menu, Monitor));

    [Fact]
    public void OnePixelShortOfRoom_Flips() =>
        Assert.Equal(new Point(199, 299), Locate(new Point(199, 299), Menu, Monitor));

    [Fact]
    public void ClampsToTheMonitorBoundsNotTheWorkArea()
    {
        // An anchor inside a 40 pixel bottom taskbar: the menu ends on the anchor, over the
        // taskbar, rather than being pulled up to the work area's bottom at 1040.
        var location = Locate(new Point(1880, 1075), Menu, Monitor);

        Assert.Equal(new Point(1680, 775), location);
        Assert.True(location.Y + Menu.Height > 1040);
    }

    [Fact]
    public void AnAnchorPastTheRightEdge_IsClampedInside() =>
        Assert.Equal(new Point(1720, 750), Locate(new Point(1990, 1050), Menu, Monitor));

    [Fact]
    public void ATallMenuFlippedBelow_IsPulledUpToTheMonitorBottom() =>
        Assert.Equal(new Point(1680, 280), Locate(new Point(1880, 290), new Size(200, 800), Monitor));

    [Fact]
    public void AMenuLargerThanTheMonitor_StartsAtItsTopLeft() =>
        Assert.Equal(new Point(0, 0), Locate(new Point(1880, 1050), new Size(2500, 1500), Monitor));

    [Fact]
    public void AMonitorLeftOfThePrimary_UsesItsOwnBounds()
    {
        var left = new Rectangle(-1920, 0, 1920, 1080);

        Assert.Equal(new Point(-220, 750), Locate(new Point(-20, 1050), Menu, left));
        Assert.Equal(new Point(-1900, 750), Locate(new Point(-1900, 1050), Menu, left));
        Assert.Equal(new Point(-1920, 0), Locate(new Point(-20, 1050), new Size(2500, 1500), left));
    }

    [Fact]
    public void AMonitorAboveThePrimary_UsesItsOwnBounds()
    {
        var above = new Rectangle(0, -1080, 1920, 1080);

        Assert.Equal(new Point(1680, -330), Locate(new Point(1880, -30), Menu, above));
        Assert.Equal(new Point(1680, -1070), Locate(new Point(1880, -1070), Menu, above));
    }
}
