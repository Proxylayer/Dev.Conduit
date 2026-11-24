namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Retains the first N bytes handed to it and counts the rest. Once the cap is reached
/// it keeps counting but stops retaining, so capture can never grow without bound and
/// never has to interrupt the stream it is observing.
/// </summary>
public sealed class CappedBuffer
{
    private readonly int _limit;
    private readonly MemoryStream _retained;

    public CappedBuffer(int limit)
    {
        _limit = Math.Max(limit, 0);
        _retained = new MemoryStream(Math.Min(_limit, 64 * 1024));
    }

    /// <summary>Total bytes observed, including the ones past the cap.</summary>
    public long TotalBytes { get; private set; }

    /// <summary>True once more bytes were observed than could be retained.</summary>
    public bool Truncated { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        TotalBytes += data.Length;

        var room = _limit - (int)_retained.Length;
        if (room <= 0)
        {
            Truncated = true;
            return;
        }

        if (data.Length > room)
        {
            _retained.Write(data[..room]);
            Truncated = true;
            return;
        }

        _retained.Write(data);
    }

    public byte[] Retained() => _retained.ToArray();
}
