using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public readonly record struct EncodingPerformanceKey(
    EncoderImplementation Encoder,
    int ResolutionBucket,
    int FpsBucket,
    string Preset);

public sealed record EncodingPerformanceObservation(
    EncodingPerformanceKey Key,
    VideoEncoder Encoder,
    VideoCodecKind Codec,
    string? SourceCodec,
    long? PixelCount,
    double? FrameRate,
    double MediaDurationSeconds,
    TimeSpan WallClockEncodeDuration,
    double AverageSpeed,
    DateTimeOffset ObservedAt);

/// <summary>
/// Session-local observations. It is deliberately bounded and never persisted
/// in settings, because encoder speed depends on the current machine, driver
/// and thermals.
/// </summary>
public sealed class EncodingPerformanceHistory
{
    private const int MaximumObservations = 128;
    private readonly object _sync = new();
    private readonly List<EncodingPerformanceObservation> _observations = [];

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _observations.Count;
            }
        }
    }

    public IReadOnlyList<EncodingPerformanceObservation> Snapshot()
    {
        lock (_sync)
        {
            return _observations.ToArray();
        }
    }

    public void Record(
        VideoFileInfo source,
        CompressionPlan plan,
        VideoEncoder encoder,
        TimeSpan wallClockEncodeDuration,
        double? averageSpeed = null)
    {
        if (source.DurationSeconds is not > 0 || wallClockEncodeDuration <= TimeSpan.Zero)
        {
            return;
        }

        var speed = averageSpeed is > 0 && double.IsFinite(averageSpeed.Value)
            ? averageSpeed.Value
            : source.DurationSeconds.Value / wallClockEncodeDuration.TotalSeconds;
        if (!double.IsFinite(speed) || speed <= 0 || speed > 1_000)
        {
            return;
        }

        var observation = new EncodingPerformanceObservation(
            CompressionDurationEstimator.CreateKey(source, encoder, plan.EncodingPreset),
            encoder,
            EncoderCatalog.Get(encoder).Codec,
            source.VideoCodec,
            source.PixelCount,
            source.FrameRate,
            source.DurationSeconds.Value,
            wallClockEncodeDuration,
            speed,
            DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _observations.Add(observation);
            if (_observations.Count > MaximumObservations)
            {
                _observations.RemoveRange(0, _observations.Count - MaximumObservations);
            }
        }
    }

    public void Record(CompressionTaskEntry entry, CompressionJobResult result)
    {
        var encoder = result.Encoder ?? entry.Plan.Encoder;
        var successfulAttempt = result.Attempts?
            .LastOrDefault(attempt => attempt.Status == CompressionAttemptStatus.Completed &&
                                      attempt.Duration is { } duration && duration > TimeSpan.Zero);
        var duration = successfulAttempt?.Duration;
        if (duration is null)
        {
            return;
        }

        Record(entry.Source, entry.Plan, encoder, duration.Value, successfulAttempt?.AverageSpeed);
    }

    internal IReadOnlyList<EncodingPerformanceObservation> Find(EncodingPerformanceKey key)
    {
        lock (_sync)
        {
            return _observations
                .Where(observation => observation.Key == key)
                .OrderByDescending(observation => observation.ObservedAt)
                .ToArray();
        }
    }

    internal IReadOnlyList<EncodingPerformanceObservation> FindByEncoderAndPreset(
        EncoderImplementation encoder,
        string preset)
    {
        lock (_sync)
        {
            return _observations
                .Where(observation => observation.Key.Encoder == encoder &&
                                      string.Equals(observation.Key.Preset, preset, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(observation => observation.ObservedAt)
                .ToArray();
        }
    }
}

public sealed record QueueEtaEstimate(
    TimeSpan? Remaining,
    EtaConfidence Confidence,
    int KnownTaskCount,
    int UnknownTaskCount)
{
    public bool IsKnown => Remaining is not null && UnknownTaskCount == 0;

    public string Display => Remaining is { } remaining
        ? DisplayFormat.EstimatedDuration(remaining)
        : "计算中";
}

/// <summary>
/// Estimates one task and a queue without assuming all files have the same
/// duration or encoder speed. Queue simulation respects CPU/hardware lanes
/// and uses simple list scheduling, which is sufficient for a user-facing ETA.
/// </summary>
public sealed class CompressionDurationEstimator
{
    private static readonly TimeSpan ValidationCommitOverhead = TimeSpan.FromSeconds(5);

    public bool TryEstimateEncodingDuration(
        VideoFileInfo source,
        CompressionPlan plan,
        EncodingPerformanceHistory history,
        out TimeSpan duration,
        out EtaConfidence confidence,
        VideoEncoder? actualEncoder = null)
    {
        duration = default;
        confidence = EtaConfidence.Unknown;
        if (source.DurationSeconds is not > 0 ||
            !history.TryEstimateSpeed(source, plan, actualEncoder, out var speed, out confidence))
        {
            return false;
        }

        var seconds = source.DurationSeconds.Value / speed;
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            confidence = EtaConfidence.Unknown;
            return false;
        }

        duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, TimeSpan.FromDays(7).TotalSeconds));
        return true;
    }

    public QueueEtaEstimate EstimateQueue(
        IEnumerable<CompressionTaskEntry> entries,
        LongRunningTaskPolicy policy,
        EncodingPerformanceHistory history)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(history);

        var entryList = entries.ToArray();
        var activeSpeeds = entryList
            .Where(entry => entry.ExecutionState == CompressionExecutionState.Compressing)
            .Select(entry => TryCreateActiveSpeed(entry, out var active) ? active : null)
            .Where(active => active is not null)
            .Cast<ActiveSpeed>()
            .ToArray();
        var jobs = new List<EstimatedJob>();
        var known = 0;
        var unknown = 0;
        var weakestConfidence = EtaConfidence.High;
        foreach (var entry in entryList)
        {
            if (IsTerminal(entry.ExecutionState))
            {
                continue;
            }

            if (!TryEstimateEntry(entry, policy, history, activeSpeeds, out var duration, out var confidence))
            {
                unknown++;
                continue;
            }

            known++;
            weakestConfidence = Weaken(weakestConfidence, confidence);
            jobs.Add(new EstimatedJob(
                EncoderCatalog.Get(entry.ActualEncoder ?? entry.Plan.Encoder).IsHardware,
                duration.TotalSeconds));
        }

        if (unknown > 0)
        {
            return new QueueEtaEstimate(null, EtaConfidence.Unknown, known, unknown);
        }
        if (jobs.Count == 0)
        {
            return new QueueEtaEstimate(TimeSpan.Zero, EtaConfidence.High, known, 0);
        }

        var makespan = Schedule(jobs, policy);
        return new QueueEtaEstimate(
            TimeSpan.FromSeconds(Math.Max(0, makespan)),
            weakestConfidence,
            known,
            0);
    }

    public bool TryEstimateEntry(
        CompressionTaskEntry entry,
        LongRunningTaskPolicy policy,
        EncodingPerformanceHistory history,
        out TimeSpan duration,
        out EtaConfidence confidence)
        => TryEstimateEntry(entry, policy, history, Array.Empty<ActiveSpeed>(), out duration, out confidence);

    public bool TryEstimateEntry(
        CompressionTaskEntry entry,
        LongRunningTaskPolicy policy,
        EncodingPerformanceHistory history,
        IEnumerable<CompressionTaskEntry> contextEntries,
        out TimeSpan duration,
        out EtaConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(contextEntries);
        var activeSpeeds = contextEntries
            .Where(contextEntry => contextEntry.ExecutionState == CompressionExecutionState.Compressing)
            .Select(contextEntry => TryCreateActiveSpeed(contextEntry, out var active) ? active : null)
            .Where(active => active is not null)
            .Cast<ActiveSpeed>()
            .ToArray();
        return TryEstimateEntry(entry, policy, history, activeSpeeds, out duration, out confidence);
    }

    private bool TryEstimateEntry(
        CompressionTaskEntry entry,
        LongRunningTaskPolicy policy,
        EncodingPerformanceHistory history,
        IReadOnlyList<ActiveSpeed> activeSpeeds,
        out TimeSpan duration,
        out EtaConfidence confidence)
    {
        duration = default;
        confidence = EtaConfidence.Unknown;
        if (entry.ExecutionState is CompressionExecutionState.Verifying or CompressionExecutionState.Committing)
        {
            duration = ValidationCommitOverhead;
            confidence = EtaConfidence.High;
            return true;
        }

        if (entry.ExecutionState == CompressionExecutionState.Compressing &&
            entry.LatestEncoding is { } latest &&
            latest.TotalDurationSeconds is > 0 &&
            latest.ProcessedDurationSeconds >= 0 &&
            latest.SmoothedSpeed is > 0 &&
            latest.IsEtaStable)
        {
            var remaining = Math.Max(0, latest.TotalDurationSeconds.Value - latest.ProcessedDurationSeconds) /
                            latest.SmoothedSpeed.Value;
            if (double.IsFinite(remaining))
            {
                duration = TimeSpan.FromSeconds(Math.Max(0, remaining));
                confidence = latest.EtaConfidence == EtaConfidence.Unknown
                    ? EtaConfidence.Low
                    : latest.EtaConfidence;
                return true;
            }
        }

        if (TryEstimateEncodingDuration(
                entry.Source,
                entry.Plan,
                history,
                out duration,
                out confidence,
                entry.ActualEncoder))
        {
            return true;
        }

        var encoder = entry.ActualEncoder ?? entry.Plan.Encoder;
        var active = activeSpeeds.FirstOrDefault(item =>
            item.Encoder == encoder &&
            string.Equals(item.Preset, entry.Plan.EncodingPreset, StringComparison.OrdinalIgnoreCase));
        if (active is null)
        {
            return false;
        }

        var speed = EncodingPerformanceHistoryExtensions.ScaleSpeed(
            active.Speed,
            active.Source,
            entry.Source);
        if (entry.Source.DurationSeconds is not > 0 || speed <= 0 || !double.IsFinite(speed))
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(Math.Clamp(entry.Source.DurationSeconds.GetValueOrDefault() / speed, 1, TimeSpan.FromDays(7).TotalSeconds));
        confidence = active.Source.PixelCount == entry.Source.PixelCount &&
                     Math.Abs(active.Source.FrameRate.GetValueOrDefault() - entry.Source.FrameRate.GetValueOrDefault()) < 0.1
            ? EtaConfidence.Medium
            : EtaConfidence.Low;
        return true;
    }

    public static EncodingPerformanceKey CreateKey(
        VideoFileInfo source,
        VideoEncoder encoder,
        string preset) => new(
        (EncoderImplementation)encoder,
        ResolutionBucket(source),
        FpsBucket(source.FrameRate),
        NormalizePreset(preset));

    internal static int ResolutionBucket(VideoFileInfo source)
    {
        var pixels = source.PixelCount;
        return pixels switch
        {
            null => 0,
            <= 640L * 360 => 360,
            <= 1280L * 720 => 720,
            <= 1920L * 1080 => 1080,
            <= 2560L * 1440 => 1440,
            <= 3840L * 2160 => 2160,
            _ => 4320
        };
    }

    internal static int FpsBucket(double? fps) => fps switch
    {
        null => 0,
        <= 24 => 24,
        <= 30 => 30,
        <= 60 => 60,
        _ => 120
    };

    private static double Schedule(IReadOnlyList<EstimatedJob> jobs, LongRunningTaskPolicy policy)
    {
        var maxTotalWorkers = Math.Max(1, policy.MaxTotalWorkers);
        var slots = new List<LaneSlot>();
        slots.AddRange(Enumerable.Range(0, Math.Max(1, policy.MaxCpuWorkers)).Select(_ => new LaneSlot(false)));
        slots.AddRange(Enumerable.Range(0, Math.Max(1, policy.MaxHardwareWorkers)).Select(_ => new LaneSlot(true)));
        var scheduled = new List<ScheduledInterval>();

        foreach (var job in jobs)
        {
            var candidates = slots.Where(slot => slot.IsHardware == job.IsHardware).ToArray();
            if (candidates.Length == 0)
            {
                return double.PositiveInfinity;
            }

            var selected = candidates
                .Select(slot => (Slot: slot, Start: FindEarliestStart(slot.AvailableAt, scheduled, maxTotalWorkers)))
                .OrderBy(item => item.Start)
                .First();
            var end = selected.Start + job.Seconds;
            selected.Slot.AvailableAt = end;
            scheduled.Add(new ScheduledInterval(selected.Start, end));
        }

        return scheduled.Count == 0 ? 0 : scheduled.Max(interval => interval.End);
    }

    private static double FindEarliestStart(
        double candidate,
        IReadOnlyList<ScheduledInterval> scheduled,
        int maxTotalWorkers)
    {
        var start = Math.Max(0, candidate);
        while (true)
        {
            var active = scheduled.Count(interval => interval.Start < start && interval.End > start ||
                                                     Math.Abs(interval.Start - start) < 0.0001 && interval.End > start);
            if (active < maxTotalWorkers)
            {
                return start;
            }

            var next = scheduled
                .Where(interval => interval.End > start)
                .Select(interval => interval.End)
                .DefaultIfEmpty(start + 0.001)
                .Min();
            start = Math.Max(start + 0.001, next);
        }
    }

    private static bool IsTerminal(CompressionExecutionState state) => state is
        CompressionExecutionState.Completed or
        CompressionExecutionState.Cancelled or
        CompressionExecutionState.Failed or
        CompressionExecutionState.Abandoned;

    private static EtaConfidence Weaken(EtaConfidence current, EtaConfidence next) =>
        (EtaConfidence)Math.Min((int)current, (int)next);

    private static string NormalizePreset(string? preset) =>
        string.IsNullOrWhiteSpace(preset) ? "default" : preset.Trim().ToLowerInvariant();

    private static bool TryCreateActiveSpeed(CompressionTaskEntry entry, out ActiveSpeed active)
    {
        active = default!;
        if (entry.LatestEncoding is not { } latest ||
            latest.IsEtaStable is false ||
            latest.SmoothedSpeed is not > 0)
        {
            return false;
        }

        active = new ActiveSpeed(
            entry.ActualEncoder ?? entry.Plan.Encoder,
            NormalizePreset(entry.Plan.EncodingPreset),
            entry.Source,
            latest.SmoothedSpeed.Value);
        return true;
    }

    private sealed record EstimatedJob(bool IsHardware, double Seconds);
    private sealed record ScheduledInterval(double Start, double End);
    private sealed record ActiveSpeed(VideoEncoder Encoder, string Preset, VideoFileInfo Source, double Speed);

    private sealed class LaneSlot(bool isHardware)
    {
        public bool IsHardware { get; } = isHardware;
        public double AvailableAt { get; set; }
    }
}

public static class EncodingPerformanceHistoryExtensions
{
    public static bool TryEstimateSpeed(
        this EncodingPerformanceHistory history,
        VideoFileInfo source,
        CompressionPlan plan,
        VideoEncoder? actualEncoder,
        out double speed,
        out EtaConfidence confidence)
    {
        speed = 0;
        confidence = EtaConfidence.Unknown;
        var encoder = actualEncoder ?? plan.Encoder;
        var key = CompressionDurationEstimator.CreateKey(source, encoder, plan.EncodingPreset);
        var exact = history.Find(key);
        if (exact.Count > 0)
        {
            speed = WeightedAverage(exact.Select(observation => observation.AverageSpeed));
            confidence = exact.Count >= 3 ? EtaConfidence.High : EtaConfidence.Medium;
            return speed > 0;
        }

        var nearby = history.FindByEncoderAndPreset(
            (EncoderImplementation)encoder,
            key.Preset);
        if (nearby.Count == 0)
        {
            return false;
        }

        var targetWork = WorkFactor(source);
        var estimates = nearby.Select(observation =>
        {
            var reference = observation.PixelCount is > 0 && observation.FrameRate is > 0
                ? observation.PixelCount.Value * observation.FrameRate.Value
                : 1920d * 1080 * 30;
            var scale = Math.Pow(reference / targetWork, 0.65);
            var decodeScale = DecodeCost(observation.SourceCodec) / DecodeCost(source.VideoCodec);
            return observation.AverageSpeed * Math.Clamp(scale * decodeScale, 0.15, 6);
        });
        speed = WeightedAverage(estimates);
        confidence = EtaConfidence.Low;
        return speed > 0;
    }

    private static double WorkFactor(VideoFileInfo source) =>
        (source.PixelCount is > 0 ? source.PixelCount.Value : 1920d * 1080) *
        (source.FrameRate is > 0 ? source.FrameRate.Value : 30);

    private static double DecodeCost(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "av1" => 1.15,
        "hevc" or "h265" => 1.05,
        _ => 1.0
    };

    private static double WeightedAverage(IEnumerable<double> values)
    {
        var weighted = 0d;
        var totalWeight = 0d;
        var weight = 1d;
        foreach (var value in values)
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                continue;
            }

            weighted += value * weight;
            totalWeight += weight;
            weight *= 0.75;
        }

        return totalWeight <= 0 ? 0 : weighted / totalWeight;
    }

    internal static double ScaleSpeed(double referenceSpeed, VideoFileInfo reference, VideoFileInfo target)
    {
        var referenceWork = WorkFactor(reference);
        var targetWork = WorkFactor(target);
        var scale = Math.Pow(referenceWork / targetWork, 0.65);
        var decodeScale = DecodeCost(reference.VideoCodec) / DecodeCost(target.VideoCodec);
        return referenceSpeed * Math.Clamp(scale * decodeScale, 0.15, 6);
    }
}
