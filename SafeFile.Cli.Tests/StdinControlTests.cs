using System.Collections.Concurrent;
using SafeFile.Cli.Infrastructure;

namespace SafeFile.Cli.Tests;

public sealed class StdinControlTests
{
    [Fact]
    public void Watch_CancelsOnCancelLine_AndStopsReading()
    {
        using var cts = new CancellationTokenSource();
        var input = new StringReader("progress please\n  Cancel  \nleft over\n");

        StdinControl.Watch(input, cts);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("left over", input.ReadLine());
    }

    [Fact]
    public void Watch_CancelsAtEndOfInput()
    {
        using var cts = new CancellationTokenSource();

        StdinControl.Watch(new StringReader(""), cts);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_IgnoresOtherLinesWhileInputStaysOpen()
    {
        using var cts = new CancellationTokenSource();
        using var input = new QueueReader();
        var watcher = Task.Run(() => StdinControl.Watch(input, cts), TestContext.Current.CancellationToken);

        input.Push("hello");
        input.Push("");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(cts.IsCancellationRequested);

        input.Push("cancel");
        await watcher.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Watch_TreatsBrokenInputAsEndOfInput()
    {
        using var cts = new CancellationTokenSource();

        StdinControl.Watch(new ThrowingReader(), cts);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Watch_DoesNotThrowWhenTheSourceIsAlreadyDisposed()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose();

        StdinControl.Watch(new StringReader(""), cts);
    }

    /// <summary>A reader whose lines arrive over time, like a pipe kept open by the host.</summary>
    private sealed class QueueReader : TextReader
    {
        private readonly BlockingCollection<string> lines = new();

        public void Push(string line) => lines.Add(line);

        public override string? ReadLine() =>
            lines.TryTake(out var line, TimeSpan.FromSeconds(10)) ? line : null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lines.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("The pipe has been ended.");
    }
}
