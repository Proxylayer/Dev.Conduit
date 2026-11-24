namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
