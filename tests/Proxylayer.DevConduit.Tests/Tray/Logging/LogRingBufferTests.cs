using Microsoft.Extensions.Logging;
using Proxylayer.DevConduit.Tray.Logging;

namespace Proxylayer.DevConduit.Tests.Tray.Logging;

/// <summary>The tray's in-memory log: capacity, order, clearing, errors and concurrent writers.</summary>
public sealed class LogRingBufferTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 20, 9, 30, 15, 123, TimeSpan.Zero);

    private static void AddMany(LogRingBuffer buffer, int count, LogLevel level = LogLevel.Information)
    {
        for (var index = 1; index <= count; index++)
        {
            buffer.Add(Time, level, "Category", $"line {index}");
        }
    }

    [Fact]
    public void DefaultCapacity_IsTwoThousandLines()
    {
        Assert.Equal(2_000, LogRingBuffer.DefaultCapacity);
        Assert.Equal(2_000, new LogRingBuffer().Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RefusesACapacityBelowOne(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(capacity));

    [Fact]
    public void Snapshot_IsEmptyAtFirst()
    {
        var buffer = new LogRingBuffer();

        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0, buffer.LastSequence);
        Assert.Equal(string.Empty, buffer.ToText());
    }

    [Fact]
    public void Snapshot_IsOldestFirstWithIncreasingSequenceNumbers()
    {
        var buffer = new LogRingBuffer();
        buffer.Add(Time, LogLevel.Information, "A", "first");
        buffer.Add(Time.AddSeconds(1), LogLevel.Warning, "B", "second");
        buffer.Add(Time.AddSeconds(2), LogLevel.Error, "C", "third");

        var lines = buffer.Snapshot();

        Assert.Equal(["first", "second", "third"], lines.Select(line => line.Message));
        Assert.Equal([1L, 2L, 3L], lines.Select(line => line.Sequence));
        Assert.Equal(["A", "B", "C"], lines.Select(line => line.Category));
        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error], lines.Select(line => line.Level));
        Assert.Equal(Time.AddSeconds(1), lines[1].Time);
        Assert.Equal(3, buffer.LastSequence);
    }

    [Fact]
    public void Add_DropsTheOldestLinesBeyondTwoThousand()
    {
        var buffer = new LogRingBuffer();

        AddMany(buffer, 2_500);

        var lines = buffer.Snapshot();
        Assert.Equal(2_000, lines.Count);
        Assert.Equal("line 501", lines[0].Message);
        Assert.Equal(501, lines[0].Sequence);
        Assert.Equal("line 2500", lines[^1].Message);
        Assert.Equal(2_500, buffer.LastSequence);
    }

    [Fact]
    public void Add_KeepsExactlyTheCapacityWhenFull()
    {
        var buffer = new LogRingBuffer();

        AddMany(buffer, 2_000);
        Assert.Equal(2_000, buffer.Snapshot().Count);
        Assert.Equal("line 1", buffer.Snapshot()[0].Message);

        buffer.Add(Time, LogLevel.Information, "Category", "one more");
        Assert.Equal(2_000, buffer.Snapshot().Count);
        Assert.Equal("line 2", buffer.Snapshot()[0].Message);
        Assert.Equal("one more", buffer.Snapshot()[^1].Message);
    }

    [Fact]
    public void Add_HonoursASmallCapacity()
    {
        var buffer = new LogRingBuffer(capacity: 3);

        AddMany(buffer, 5);

        Assert.Equal(["line 3", "line 4", "line 5"], buffer.Snapshot().Select(line => line.Message));
    }

    [Fact]
    public void Snapshot_IsACopyThatLaterWritesDoNotChange()
    {
        var buffer = new LogRingBuffer(capacity: 2);
        AddMany(buffer, 2);

        var snapshot = buffer.Snapshot();
        AddMany(buffer, 2);
        buffer.Clear();

        Assert.Equal(["line 1", "line 2"], snapshot.Select(line => line.Message));
    }

    [Fact]
    public void Clear_EmptiesTheBufferButKeepsTheSequenceGoing()
    {
        var buffer = new LogRingBuffer();
        AddMany(buffer, 5);

        buffer.Clear();

        Assert.Empty(buffer.Snapshot());
        Assert.Equal(5, buffer.LastSequence);

        buffer.Add(Time, LogLevel.Information, "Category", "after");
        Assert.Equal(6, Assert.Single(buffer.Snapshot()).Sequence);
    }

    [Fact]
    public void Changed_IsRaisedForEveryAddAndClear()
    {
        var buffer = new LogRingBuffer();
        var raised = 0;
        buffer.Changed += (sender, _) =>
        {
            Assert.Same(buffer, sender);
            raised++;
        };

        AddMany(buffer, 3);
        buffer.Clear();

        Assert.Equal(4, raised);
    }

    [Fact]
    public void HasErrorAfter_TracksErrorsAndWorseOnly()
    {
        var buffer = new LogRingBuffer();
        buffer.Add(Time, LogLevel.Warning, "C", "warning");
        Assert.False(buffer.HasErrorAfter(0));

        buffer.Add(Time, LogLevel.Error, "C", "error");
        Assert.True(buffer.HasErrorAfter(0));
        Assert.True(buffer.HasErrorAfter(1));
        Assert.False(buffer.HasErrorAfter(2));

        buffer.Add(Time, LogLevel.Information, "C", "info");
        Assert.False(buffer.HasErrorAfter(2));

        buffer.Add(Time, LogLevel.Critical, "C", "critical");
        Assert.True(buffer.HasErrorAfter(3));
    }

    [Fact]
    public void ToText_WritesOneLinePerEntryWithTimeLevelCategoryAndMessage()
    {
        var buffer = new LogRingBuffer();
        buffer.Add(Time, LogLevel.Warning, "Dev.Conduit.Test", "careful");
        buffer.Add(Time, LogLevel.Error, "Other", "broken");

        var lines = buffer.ToText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal($"{Time.LocalDateTime:HH:mm:ss.fff} warn Dev.Conduit.Test: careful", lines[0]);
        Assert.EndsWith(" fail Other: broken", lines[1]);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trce")]
    [InlineData(LogLevel.Debug, "dbug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "fail")]
    [InlineData(LogLevel.Critical, "crit")]
    [InlineData(LogLevel.None, "    ")]
    public void LogLine_NamesEachLevelInFourCharacters(LogLevel level, string name) =>
        Assert.Equal($"{Time.LocalDateTime:HH:mm:ss.fff} {name} C: m", new LogLine(1, Time, level, "C", "m").ToString());

    [Fact]
    public async Task Add_FromManyThreadsLosesNoSequenceNumberAndKeepsTheNewestInOrder()
    {
        const int Writers = 8;
        const int PerWriter = 5_000;
        var buffer = new LogRingBuffer();
        var raised = 0;
        buffer.Changed += (_, _) => Interlocked.Increment(ref raised);

        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            // Snapshots taken mid-write must always be whole and in order.
            while (!stop.IsCancellationRequested)
            {
                var snapshot = buffer.Snapshot();
                Assert.True(snapshot.Count <= buffer.Capacity);
                for (var index = 1; index < snapshot.Count; index++)
                {
                    Assert.Equal(snapshot[index - 1].Sequence + 1, snapshot[index].Sequence);
                }
            }
        }, TestContext.Current.CancellationToken);

        Parallel.For(0, Writers, new ParallelOptions { MaxDegreeOfParallelism = Writers }, writer =>
        {
            for (var index = 0; index < PerWriter; index++)
            {
                buffer.Add(Time, index % 100 == 0 ? LogLevel.Error : LogLevel.Information, $"writer {writer}", $"{writer}:{index}");
            }
        });

        stop.Cancel();
        await reader;

        const int Total = Writers * PerWriter;
        Assert.Equal(Total, buffer.LastSequence);
        Assert.Equal(Total, raised);

        var lines = buffer.Snapshot();
        Assert.Equal(LogRingBuffer.DefaultCapacity, lines.Count);
        Assert.Equal(
            Enumerable.Range(Total - LogRingBuffer.DefaultCapacity + 1, LogRingBuffer.DefaultCapacity).Select(value => (long)value),
            lines.Select(line => line.Sequence));

        // Each writer's own lines stay in the order it wrote them.
        foreach (var group in lines.GroupBy(line => line.Category))
        {
            var indexes = group.Select(line => int.Parse(line.Message.Split(':')[1])).ToList();
            Assert.Equal(indexes.Order(), indexes);
        }
    }

    [Fact]
    public void ClearAndAdd_FromManyThreadsNeverCorruptTheBuffer()
    {
        var buffer = new LogRingBuffer(capacity: 50);

        Parallel.For(0, 4_000, index =>
        {
            if (index % 97 == 0)
            {
                buffer.Clear();
            }
            else
            {
                buffer.Add(Time, LogLevel.Information, "C", index.ToString());
            }
        });

        Assert.True(buffer.Snapshot().Count <= 50);
        var lines = buffer.Snapshot();
        for (var index = 1; index < lines.Count; index++)
        {
            Assert.True(lines[index - 1].Sequence < lines[index].Sequence);
        }

        Assert.Equal(4_000 - Enumerable.Range(0, 4_000).Count(index => index % 97 == 0), buffer.LastSequence);
    }
}
