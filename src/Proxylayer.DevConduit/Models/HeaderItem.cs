namespace Proxylayer.DevConduit.Models;

/// <summary>A single request header as composed in the UI.</summary>
public sealed record HeaderItem(string Name, string Value, bool Enabled = true, bool IsSecret = false);
