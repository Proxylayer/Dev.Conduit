namespace Proxylayer.DevConduit.Services.Local;

/// <summary>
/// The request body as it appears to the forward proxy: the bytes that arrived in the same
/// read as the header block, then the socket, stopping at the declared content length so a
/// keep-alive connection's next request is not swallowed. The socket is never disposed here;
/// it belongs to the connection.
/// </summary>
public sealed class ForwardProxyBodyStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private readonly long _length;

    private int _prefixOffset;
    private long _consumed;

    public ForwardProxyBodyStream(byte[] prefix, Stream inner, long length)
    {
        _prefix = prefix;
        _inner = inner;
        _length = length;
    }

    /// <summary>True once every declared byte has been handed out.</summary>
    public bool Drained => _consumed >= _length;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var slice = Reserve(buffer);
        if (slice.IsEmpty)
        {
            return 0;
        }

        if (TryTakePrefix(slice, out var fromPrefix))
        {
            return fromPrefix;
        }

        return Count(_inner.Read(slice));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var slice = Reserve(buffer.Span);
        if (slice.IsEmpty)
        {
            return 0;
        }

        if (TryTakePrefix(slice, out var fromPrefix))
        {
            return fromPrefix;
        }

        return Count(await _inner.ReadAsync(buffer[..slice.Length], cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Trims a read to what is left of the declared length.</summary>
    private Span<byte> Reserve(Span<byte> buffer)
    {
        var remaining = _length - _consumed;
        return remaining <= 0 ? [] : buffer[..(int)Math.Min(buffer.Length, remaining)];
    }

    private bool TryTakePrefix(Span<byte> buffer, out int read)
    {
        read = 0;
        var available = _prefix.Length - _prefixOffset;
        if (available <= 0)
        {
            return false;
        }

        read = Math.Min(available, buffer.Length);
        _prefix.AsSpan(_prefixOffset, read).CopyTo(buffer);
        _prefixOffset += read;
        _consumed += read;
        return true;
    }

    private int Count(int read)
    {
        _consumed += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The socket stream belongs to the connection, not to this wrapper.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
