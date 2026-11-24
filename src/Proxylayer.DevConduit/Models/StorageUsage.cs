namespace Proxylayer.DevConduit.Models;

/// <summary>What one on-disk store is costing the current user.</summary>
public sealed record StoreUsage(string Store, string Path, long Bytes, int Entries, int? MaxEntries);

/// <summary>Per-store byte usage for the signed-in user, so growth stays visible.</summary>
public sealed record StorageUsage(string DataRoot, long TotalBytes, IReadOnlyList<StoreUsage> Stores);
