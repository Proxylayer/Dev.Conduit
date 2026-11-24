using System.Text;

namespace Proxylayer.DevConduit.Tray.Logging;

/// <summary>One formatted log line and its place in the buffer's sequence.</summary>
public sealed record LogLine(long Sequence, DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{Time.LocalDateTime:HH:mm:ss.fff} {LevelName(Level)} {Category}: {Message}";

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "    "
    };
}

/// <summary>
/// The most recent log lines, oldest dropped first, for the tray's log window. Safe to write
/// from any thread. Every line gets a sequence number that keeps increasing across
/// <see cref="Clear"/>, so a reader can ask what happened after a point it remembers.
/// </summary>
public sealed class LogRingBuffer
{
    public const int DefaultCapacity = 2_000;

    private readonly object _gate = new();
    private readonly Queue<LogLine> _lines = new();
    private long _sequence;
    private long _lastErrorSequence;

    public LogRingBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Raised after a line is added or the buffer is cleared, on the writing thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The sequence number of the newest line written so far, 0 before any.</summary>
    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public void Add(DateTimeOffset time, LogLevel level, string category, string message)
    {
        lock (_gate)
        {
            _sequence++;
            _lines.Enqueue(new LogLine(_sequence, time, level, category, message));
            while (_lines.Count > Capacity)
            {
                _lines.Dequeue();
            }

            if (level >= LogLevel.Error)
            {
                _lastErrorSequence = _sequence;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when an error or worse was written after <paramref name="sequence"/>.</summary>
    public bool HasErrorAfter(long sequence)
    {
        lock (_gate)
        {
            return _lastErrorSequence > sequence;
        }
    }

    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }

    /// <summary>Every line, one per row, for the clipboard.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var line in Snapshot())
        {
            text.AppendLine(line.ToString());
        }

        return text.ToString();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
