using DeliveryForge.Execution;
using DeliveryForge.Planning;

namespace DeliveryForge.Tool;

public sealed partial class OwnerHostBridge
{
    HistoricalQualityHost? historicalQuality;
    internal bool HasHistoricalQuality => historicalQuality is not null;
    internal void BindHistoricalQuality(HistoricalQualityHost actual)
    {
        lock (registration)
        {
            if (historicalQuality is not null || frames.Count != 0) throw new ToolUsageException("Historical quality binds the idle actual bridge once.");
            historicalQuality = actual;
        }
    }

    internal async Task<ToolResult> HistoricalQualityAsync(FrozenWorkerPlan assignment, ToolLifetime original)
    {
        if (historicalQuality is null || coordination is null || adapter.Realm != HostObservationRealm.ActualHost)
            throw new ToolUsageException("Historical gates require the real selected scope and live production parent; no fixture fallback.");
        adapter.BindOriginal(original);
        var frame = new HostBridgeFrame(assignment.RunId, original, coordinator, () => historicalQuality.Unknown || adapter.HasUnknownCalls(assignment.RunId), false);
        if (!Register(assignment.RunId, frame)) throw new WorkerEnvelopeException("Historical run already entered; no replay.");
        bool NoWorker() => !adapter.ActualCalls.Any(call => call.RunId == frame.RunId && call.Kind == "Start") && adapter.ObserveBoundNativeQuiescence(frame.RunId);
        frame.EnterRoute();
        try
        {
            frame.Quality.Start(original, async () =>
            {
                try
                {
                    HistoricalQualityHost.RequireNoWorker(frame);
                    await coordination.AcquireAsync(assignment.RunId).ConfigureAwait(false);
                    if (localSingleFactory is not null)
                    {
                        var scheduler = localSingleFactory(schedulingPlan ?? throw new ToolUsageException("Actual frozen scheduling plan required."), assignment, coordination);
                        frame.Scheduler = scheduler;
                        scheduler.BindOriginal(original, coordination, () => frame.Quality.Actual,
                            () => NoWorker() && ObservePipelineSettled(frame.RunId), () => original.HasExplicitUnknown);
                        frame.Reservation = await frame.Enter(() => scheduler.ReserveAsync(original.Token)).ConfigureAwait(false);
                    }
                    return await historicalQuality.RunAsync(frame, coordination, NoWorker).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    frame.Primary ??= error;
                    return ToolResult.Incomplete("HISTORICAL_QUALITY_RETAINED", "Historical gate failure retained; no worker or baseline authority issued.", new { frame.RunId, Type = error.GetType().FullName });
                }
            });
            var result = await frame.Quality.ObserveAsync(original).ConfigureAwait(false);
            if (frame.Primary is null && frame.Post is null && frame.Scheduler is { } scheduler && frame.Reservation is { } ticket)
            {
                frame.SchedulingCompletion.Start(original, async () =>
                {
                    await scheduler.ReleaseCompletedUnprovisionedObservedAsync(ticket,
                        () => NoWorker() && ObservePipelineSettled(frame.RunId), original.Token).ConfigureAwait(false);
                    return true;
                });
                await frame.SchedulingCompletion.ObserveAsync(original).ConfigureAwait(false);
            }
            return RetainedResult(frame, result);
        }
        finally
        {
            frame.LeaveRoute();
            await FinishStoppedSchedulingAsync(frame).ConfigureAwait(false);
            frame.CloseWhenSettled();
        }
    }
}
