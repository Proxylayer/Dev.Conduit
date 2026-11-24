namespace Proxylayer.DevConduit.Models;

/// <summary>One page of history summaries plus the total matching the filter.</summary>
public sealed record HistoryPage(int Total, int Skip, int Take, IReadOnlyList<HistorySummary> Items);
