namespace Proxylayer.DevConduit.Tray.Logging;

/// <summary>
/// Writes log entries into a <see cref="LogRingBuffer"/>. Registered only when the tray hosts
/// local mode, in place of the console the tray does not have. The buffer outlives each
/// hosted application, so disposing the provider leaves it alone.
/// </summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;

    public RingBufferLoggerProvider(LogRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public ILogger CreateLogger(string categoryName) => new RingBufferLogger(_buffer, categoryName);

    public void Dispose()
    {
    }

    private sealed class RingBufferLogger(LogRingBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = string.IsNullOrEmpty(message) ? exception.ToString() : message + Environment.NewLine + exception;
            }

            buffer.Add(DateTimeOffset.Now, logLevel, category, message);
        }
    }
}
