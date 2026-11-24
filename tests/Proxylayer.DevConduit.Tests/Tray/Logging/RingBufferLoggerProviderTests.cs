using Microsoft.Extensions.Logging;
using Proxylayer.DevConduit.Tray.Logging;

namespace Proxylayer.DevConduit.Tests.Tray.Logging;

/// <summary>The logger provider the tray hosts local mode with, writing into the ring buffer.</summary>
public sealed class RingBufferLoggerProviderTests
{
    [Fact]
    public void Log_WritesTheCategoryLevelAndFormattedMessage()
    {
        var buffer = new LogRingBuffer();
        using var provider = new RingBufferLoggerProvider(buffer);
        var logger = provider.CreateLogger("Proxylayer.DevConduit.Hosting");
        var before = DateTimeOffset.Now;

        logger.LogWarning("Listening on {Port} for {User}", 8092, "local");

        var line = Assert.Single(buffer.Snapshot());
        Assert.Equal("Proxylayer.DevConduit.Hosting", line.Category);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("Listening on 8092 for local", line.Message);
        Assert.InRange(line.Time, before.AddSeconds(-1), DateTimeOffset.Now.AddSeconds(1));
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void Log_WritesEveryLevel(LogLevel level)
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLoggerProvider(buffer).CreateLogger("C");

        Assert.True(logger.IsEnabled(level));
        logger.Log(level, "message");

        Assert.Equal(level, Assert.Single(buffer.Snapshot()).Level);
    }

    [Fact]
    public void Log_WritesNothingAtLevelNone()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLoggerProvider(buffer).CreateLogger("C");

        Assert.False(logger.IsEnabled(LogLevel.None));
        logger.Log(LogLevel.None, "ignored");

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Log_AppendsTheExceptionOnTheNextLine()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLoggerProvider(buffer).CreateLogger("C");
        var exception = new InvalidOperationException("boom");

        logger.LogError(exception, "It failed");

        var line = Assert.Single(buffer.Snapshot());
        Assert.StartsWith("It failed" + Environment.NewLine, line.Message);
        Assert.Contains("System.InvalidOperationException: boom", line.Message);
    }

    [Fact]
    public void Log_UsesTheExceptionAloneWhenThereIsNoMessage()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLoggerProvider(buffer).CreateLogger("C");

        logger.Log(LogLevel.Error, new EventId(1), "state", new InvalidOperationException("boom"), (_, _) => string.Empty);

        Assert.StartsWith("System.InvalidOperationException: boom", Assert.Single(buffer.Snapshot()).Message);
    }

    [Fact]
    public void BeginScope_ReturnsNothingAndChangesNothing()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLoggerProvider(buffer).CreateLogger("C");

        Assert.Null(logger.BeginScope("scope"));
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void LoggersForDifferentCategoriesShareTheBuffer()
    {
        var buffer = new LogRingBuffer();
        var provider = new RingBufferLoggerProvider(buffer);

        provider.CreateLogger("First").LogInformation("one");
        provider.CreateLogger("Second").LogInformation("two");

        Assert.Equal(["First", "Second"], buffer.Snapshot().Select(line => line.Category));
    }

    [Fact]
    public void Dispose_LeavesTheBufferForTheNextHostedApplication()
    {
        var buffer = new LogRingBuffer();
        var provider = new RingBufferLoggerProvider(buffer);
        provider.CreateLogger("C").LogInformation("kept");

        provider.Dispose();

        Assert.Equal("kept", Assert.Single(buffer.Snapshot()).Message);
    }

    [Fact]
    public void ThroughALoggerFactory_WritesTheCategoryOfTheTypedLogger()
    {
        var buffer = new LogRingBuffer();
        using (var factory = LoggerFactory.Create(builder => builder
                   .SetMinimumLevel(LogLevel.Debug)
                   .AddProvider(new RingBufferLoggerProvider(buffer))))
        {
            factory.CreateLogger<RingBufferLoggerProviderTests>().LogDebug("Debug {Value}", 42);
        }

        var line = Assert.Single(buffer.Snapshot());
        Assert.Equal(typeof(RingBufferLoggerProviderTests).FullName, line.Category);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal("Debug 42", line.Message);
        Assert.Single(buffer.Snapshot());
    }
}
