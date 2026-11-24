namespace Proxylayer.DevConduit.Tests.Support;

/// <summary>
/// A read-only stream that hands out a fixed payload in fixed-size slices and can then
/// either report end of stream or block until cancelled. Nothing here touches a socket.
/// </summary>
internal sealed class ScriptedStream : Stream
{
    private readonly byte[] _payload;
    private readonly int _sliceSize;
    private readonly bool _blockAtEnd;
    private int _position;

    public ScriptedStream(byte[] payload, int sliceSize = int.MaxValue, bool blockAtEnd = false)
    {
        _payload = payload;
        _sliceSize = sliceSize;
        _blockAtEnd = blockAtEnd;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _payload.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = _payload.Length - _position;
        if (remaining <= 0)
        {
            if (_blockAtEnd)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        var count = Math.Min(Math.Min(remaining, _sliceSize), buffer.Length);
        _payload.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
