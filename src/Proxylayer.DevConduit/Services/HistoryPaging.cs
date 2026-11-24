using Proxylayer.DevConduit.Models;

namespace Proxylayer.DevConduit.Services;

/// <summary>Filtering and paging shared by the console history and routed-traffic stores.</summary>
public static class HistoryPaging
{
    public const int MaxTake = 500;

    public static HistoryPage Page(IReadOnlyList<HistorySummary> index, int skip, int take, string? query)
    {
        IEnumerable<HistorySummary> matches = index;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            matches = matches.Where(entry =>
                entry.Url.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Method.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = matches.ToList();
        var offset = Math.Max(skip, 0);
        var page = filtered.Skip(offset).Take(Math.Clamp(take, 1, MaxTake)).ToArray();
        return new HistoryPage(filtered.Count, offset, page.Length, page);
    }
}
