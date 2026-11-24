namespace Proxylayer.DevConduit.Models;

/// <summary>A reusable, named collection of headers.</summary>
public sealed record SavedHeaderSet(
    string Id,
    string Name,
    bool ApplyByDefault,
    IReadOnlyList<HeaderItem> Headers,
    DateTimeOffset Created,
    DateTimeOffset Updated);

/// <summary>The payload accepted when creating or replacing a header set.</summary>
public sealed record SavedHeaderSetInput(
    string Name,
    bool ApplyByDefault,
    IReadOnlyList<HeaderItem> Headers);
