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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(execute);
        if (entries.Count == 0)
        {
            return;
        }

        var concurrency = Math.Clamp(configuredConcurrency, 1, 4);
        using var cpuGate = new SemaphoreSlim(1, 1);
        using var hardwareGate = new SemaphoreSlim(Math.Min(2, concurrency), Math.Min(2, concurrency));
        var pending = new List<CompressionTaskEntry>(entries);
        var running = new List<Task>();

        while (pending.Count > 0 || running.Count > 0)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                for (var index = pending.Count - 1; index >= 0 && running.Count < concurrency; index--)
                {
                    var entry = pending[index];
                    var classGate = EncoderCatalog.Get(entry.Plan.Encoder).IsHardware ? hardwareGate : cpuGate;
                    if (!classGate.Wait(0))
                    {
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
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
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
