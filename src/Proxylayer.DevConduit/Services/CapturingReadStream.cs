namespace Proxylayer.DevConduit.Services;

/// <summary>
/// Passes an inbound body through untouched while copying what it sees into a
/// <see cref="CappedBuffer"/>. The wrapped stream is never disposed here: it belongs
/// to the request, not to this wrapper.
/// </summary>
public sealed class CapturingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly CappedBuffer _capture;

    public CapturingReadStream(Stream inner, CappedBuffer capture)
    {
        _inner = inner;
        _capture = capture;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            _capture.Append(buffer[..read]);
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _capture.Append(buffer.Span[..read]);
        }

        return read;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The inner stream is owned by the request pipeline.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
