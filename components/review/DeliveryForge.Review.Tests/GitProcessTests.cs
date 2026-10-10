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

    [Theory]
    [InlineData(BlockingSeam.StandardInput)]
    [InlineData(BlockingSeam.StandardOutput)]
    [InlineData(BlockingSeam.ExitWait)]
    public async Task Capture_deadline_enters_cleanup_before_noncooperative_operations_settle(
        BlockingSeam seam)
    {
        var process = new NonCooperativeGitProcess(seam);
        var run = GitProcess.RunAsync(
            Directory.GetCurrentDirectory(),
            ["status"],
            TestContext.Current.CancellationToken,
            standardInput: seam == BlockingSeam.StandardInput ? [1, 2, 3] : null,
            runtime: new GitProcessRuntime(
                _ => process,
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMilliseconds(50)));
        Exception? observed = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await run.WaitAsync(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }
        catch (Exception error)
        {
            observed = error;
        }
        finally
        {
            process.ReleaseAll();
            try { await run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken); }
            catch (Exception) { }
        }
        stopwatch.Stop();

        var typed = Assert.IsType<ReviewPreflightException>(observed);
        Assert.Contains("timeout", typed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cleanup returned after {stopwatch.Elapsed}.");
        Assert.Equal(1, process.KillAttempts);
        Assert.True(process.AllBarriersSettled);
        Assert.True(process.Disposed);
    }

    public enum BlockingSeam
    {
        StandardInput,
        StandardOutput,
        ExitWait
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

    private sealed class NonCooperativeGitProcess : IGitProcess
    {
        private readonly BlockingSeam seam;
        private readonly TaskCompletionSource exitReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CloseReleasedStream input;
        private readonly CloseReleasedStream output;
        private bool exited;

        public NonCooperativeGitProcess(BlockingSeam seam)
        {
            this.seam = seam;
            input = new CloseReleasedStream(canRead: false, canWrite: true, block: seam == BlockingSeam.StandardInput);
            output = new CloseReleasedStream(canRead: true, canWrite: false, block: seam == BlockingSeam.StandardOutput);
        }

        public Stream StandardInput => input;
        public Stream StandardOutput => output;
        public Stream StandardError { get; } = new MemoryStream([], writable: false);
        public bool HasExited => exited;
        public int ExitCode => exited ? 0 : throw new InvalidOperationException("Process has not exited.");
        public int KillAttempts { get; private set; }
        public bool Disposed { get; private set; }
        public bool AllBarriersSettled => input.Settled && output.Settled && exitReleased.Task.IsCompleted;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (seam == BlockingSeam.ExitWait)
                await exitReleased.Task;
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Kill(bool entireProcessTree)
        {
            KillAttempts++;
            exited = true;
            exitReleased.TrySetResult();
        }

        public void ReleaseAll()
        {
            exited = true;
            input.Release();
            output.Release();
            exitReleased.TrySetResult();
        }

        public void Dispose()
        {
            Disposed = true;
            ReleaseAll();
        }
    }

    private sealed class CloseReleasedStream(bool canRead, bool canWrite, bool block) : Stream
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Settled => !block || released.Task.IsCompleted;
        public override bool CanRead => canRead;
        public override bool CanSeek => false;
        public override bool CanWrite => canWrite;
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
            if (block) await released.Task;
            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (block) await released.Task;
        }

        protected override void Dispose(bool disposing)
        {
            Release();
            base.Dispose(disposing);
        }

        public void Release() => released.TrySetResult();
    }
}
