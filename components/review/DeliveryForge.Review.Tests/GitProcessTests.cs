using System.ComponentModel;
using System.Diagnostics;

namespace DeliveryForge.Review.Tests;

public sealed class GitProcessTests
{
    public static TheoryData<FailureTrigger, string> ResidualUncertaintyCases => new()
    {
        { FailureTrigger.Timeout, "timeout" },
        { FailureTrigger.OutputOverflow, "output bound" },
        { FailureTrigger.Cancellation, "cancelled" }
    };

    [Theory]
    [MemberData(nameof(ResidualUncertaintyCases))]
    public async Task Failed_capture_returns_bounded_typed_residual_uncertainty_and_settles_every_barrier(
        FailureTrigger trigger,
        string expectedReason)
    {
        Stream output = trigger == FailureTrigger.OutputOverflow
            ? new MemoryStream([1, 2], writable: false)
            : new CancellationBoundReadStream();
        var error = new CancellationBoundReadStream();
        var process = new PermanentlyRunningGitProcess(output, error);
        using var cancellation = new CancellationTokenSource();
        if (trigger == FailureTrigger.Cancellation)
            cancellation.Cancel();

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ReviewPreflightException>(() => GitProcess.RunAsync(
            Directory.GetCurrentDirectory(),
            ["status"],
            cancellation.Token,
            maximumOutputBytes: trigger == FailureTrigger.OutputOverflow ? 1 : long.MaxValue,
            runtime: new GitProcessRuntime(
                _ => process,
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(25))));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Failure returned after {stopwatch.Elapsed}.");
        Assert.True(exception.ResidualProcessUncertain);
        Assert.Contains(expectedReason, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, process.KillAttempts);
        Assert.Equal(0, process.ActiveWaiters);
        Assert.True(process.AllWaitersSettled);
        Assert.True(error.AllReadsSettled);
        if (output is CancellationBoundReadStream blockedOutput)
            Assert.True(blockedOutput.AllReadsSettled);
        Assert.True(process.Disposed);
    }

    public enum FailureTrigger
    {
        Timeout,
        OutputOverflow,
        Cancellation
    }

    private sealed class PermanentlyRunningGitProcess(Stream output, Stream error) : IGitProcess
    {
        private int activeWaiters;
        private int waiterCount;
        private int settledWaiters;

        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput { get; } = output;
        public Stream StandardError { get; } = error;
        public bool HasExited => false;
        public int ExitCode => throw new InvalidOperationException("The process never exits.");
        public int KillAttempts { get; private set; }
        public int ActiveWaiters => Volatile.Read(ref activeWaiters);
        public bool AllWaitersSettled => Volatile.Read(ref waiterCount) == Volatile.Read(ref settledWaiters);
        public bool Disposed { get; private set; }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref waiterCount);
            Interlocked.Increment(ref activeWaiters);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeWaiters);
                Interlocked.Increment(ref settledWaiters);
            }
        }

        public void Kill(bool entireProcessTree)
        {
            KillAttempts++;
            throw new Win32Exception("Forced kill failure.");
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CancellationBoundReadStream : Stream
    {
        private int readCount;
        private int settledReads;

        public bool AllReadsSettled => Volatile.Read(ref readCount) == Volatile.Read(ref settledReads);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            finally
            {
                Interlocked.Increment(ref settledReads);
            }
        }
    }
}
