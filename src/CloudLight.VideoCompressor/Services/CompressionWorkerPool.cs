using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Bounded queue execution with an additional conservative encoder-class gate.
/// A user value of four therefore never means four simultaneous CPU x265 jobs,
/// and hardware jobs are capped at two until a device-specific policy exists.
/// The scheduler only starts at most <c>configuredConcurrency</c> entries and
/// skips a temporarily blocked encoder class so a CPU item cannot starve a
/// hardware item later in the queue.
/// </summary>
public sealed class CompressionWorkerPool
{
    public async Task ExecuteAsync(
        IReadOnlyList<CompressionTaskEntry> entries,
        int configuredConcurrency,
        Func<CompressionTaskEntry, Task> execute,
        CancellationToken cancellationToken,
        Func<bool>? isQueuePaused = null)
    {
        var concurrency = Math.Clamp(configuredConcurrency, 1, 4);
        var legacyPolicy = new LongRunningTaskPolicy(
            PerformanceMode.Balanced,
            1,
            Math.Min(2, concurrency),
            concurrency,
            2,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(30),
            5,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(45),
            ProcessPriorityMode.Normal,
            SoftwareThreadPolicy.EncoderDefault,
            null);
        await ExecuteAsync(entries, legacyPolicy, execute, cancellationToken, isQueuePaused).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(
        IReadOnlyList<CompressionTaskEntry> entries,
        LongRunningTaskPolicy policy,
        Func<CompressionTaskEntry, Task> execute,
        CancellationToken cancellationToken,
        Func<bool>? isQueuePaused = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(execute);
        if (entries.Count == 0)
        {
            return;
        }

        var concurrency = Math.Clamp(policy.MaxTotalWorkers, 1, 4);
        using var cpuGate = new SemaphoreSlim(Math.Clamp(policy.MaxCpuWorkers, 1, concurrency), Math.Clamp(policy.MaxCpuWorkers, 1, concurrency));
        using var hardwareGate = new SemaphoreSlim(Math.Clamp(policy.MaxHardwareWorkers, 1, concurrency), Math.Clamp(policy.MaxHardwareWorkers, 1, concurrency));
        var pending = entries
            .Where(entry => entry.ExecutionState is not
                (CompressionExecutionState.Completed or
                 CompressionExecutionState.Cancelled or
                 CompressionExecutionState.Failed or
                 CompressionExecutionState.Abandoned or
                 CompressionExecutionState.SourceChanged))
            .ToList();
        var running = new List<Task>();

        while (pending.Count > 0 || running.Count > 0)
        {
            if (!cancellationToken.IsCancellationRequested && !(isQueuePaused?.Invoke() ?? false))
            {
                for (var index = 0; index < pending.Count && running.Count < concurrency;)
                {
                    var entry = pending[index];
                    var classGate = EncoderCatalog.Get(entry.Plan.Encoder).IsHardware ? hardwareGate : cpuGate;
                    if (!classGate.Wait(0))
                    {
                        index++;
                        continue;
                    }

                    pending.RemoveAt(index);
                    running.Add(ExecuteHeldGateAsync(entry, classGate, execute));
                }
            }

            if (running.Count == 0)
            {
                if (pending.Count == 0 || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // A gate can only be unavailable while a running task owns it;
                // this short delay is a defensive back-off for a cancellation
                // or scheduler race and avoids a hot spin.
                await Task.Delay(isQueuePaused?.Invoke() == true ? 250 : 25, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var completed = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(completed);
            await completed.ConfigureAwait(false);
        }

        if (running.Count > 0)
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteHeldGateAsync(
        CompressionTaskEntry entry,
        SemaphoreSlim classGate,
        Func<CompressionTaskEntry, Task> execute)
    {
        try
        {
            await execute(entry).ConfigureAwait(false);
        }
        finally
        {
            classGate.Release();
        }
    }
}
