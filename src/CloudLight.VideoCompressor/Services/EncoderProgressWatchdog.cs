using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record EncoderProgressWatchdogOptions(
    TimeSpan StartupGracePeriod,
    TimeSpan NoProgressTimeout,
    int MinimumProgressSamples = 3)
{
    public static EncoderProgressWatchdogOptions Default { get; } = new(
        TimeSpan.FromSeconds(75),
        TimeSpan.FromSeconds(45),
        3);
}

public sealed record EncoderProgressWatchdogDecision(
    bool IsStalled,
    string Reason,
    TimeSpan NoProgressFor);

/// <summary>
/// Watches actual FFmpeg progress values rather than Process.Responding. It
/// deliberately has a long startup grace period because hardware encoders may
/// spend time initializing, muxing or flushing before the next progress line.
/// </summary>
public sealed class EncoderProgressWatchdog
{
    private readonly EncoderProgressWatchdogOptions _options;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastMeaningfulProgressAt;
    private double _lastProcessedDuration;
    private long _lastFrame = -1;
    private long _lastSizeBytes = -1;
    private int _meaningfulSamples;
    private bool _hasObservedProgress;

    public EncoderProgressWatchdog(EncoderProgressWatchdogOptions? options = null, DateTimeOffset? startedAt = null)
    {
        _options = options ?? EncoderProgressWatchdogOptions.Default;
        _startedAt = startedAt ?? DateTimeOffset.UtcNow;
        _lastMeaningfulProgressAt = _startedAt;
    }

    public EncoderProgressWatchdog(TimeSpan startupGracePeriod, TimeSpan noProgressTimeout)
        : this(new EncoderProgressWatchdogOptions(startupGracePeriod, noProgressTimeout))
    {
    }

    public DateTimeOffset LastProgressAt => _lastMeaningfulProgressAt;
    public int MeaningfulSampleCount => _meaningfulSamples;

    public void Observe(EncodingProgress progress, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var processedChanged = progress.ProcessedDurationSeconds > _lastProcessedDuration + 0.001;
        var frameChanged = progress.Frame is { } frame && frame > _lastFrame;
        var sizeChanged = progress.TotalSizeBytes is { } size && size > _lastSizeBytes;
        if (processedChanged || frameChanged || sizeChanged || !_hasObservedProgress && progress.Percent > 0)
        {
            _lastMeaningfulProgressAt = now;
            _meaningfulSamples++;
            _lastProcessedDuration = Math.Max(_lastProcessedDuration, progress.ProcessedDurationSeconds);
            if (progress.Frame is { } observedFrame)
            {
                _lastFrame = Math.Max(_lastFrame, observedFrame);
            }
            if (progress.TotalSizeBytes is { } observedSize)
            {
                _lastSizeBytes = Math.Max(_lastSizeBytes, observedSize);
            }
            _hasObservedProgress = true;
        }
    }

    public EncoderProgressWatchdogDecision Check(DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var elapsed = now - _startedAt;
        if (elapsed < _options.StartupGracePeriod)
        {
            return new EncoderProgressWatchdogDecision(false, "编码器仍在启动宽限期内。", TimeSpan.Zero);
        }

        var noProgressFor = now - _lastMeaningfulProgressAt;
        if (!_hasObservedProgress && elapsed < _options.StartupGracePeriod + _options.NoProgressTimeout)
        {
            return new EncoderProgressWatchdogDecision(false, "尚未收到首个有效编码进度，继续等待。", noProgressFor);
        }
        if (_meaningfulSamples < _options.MinimumProgressSamples && elapsed < _options.StartupGracePeriod + _options.NoProgressTimeout)
        {
            return new EncoderProgressWatchdogDecision(false, "有效进度样本不足，继续等待。", noProgressFor);
        }
        if (noProgressFor < _options.NoProgressTimeout)
        {
            return new EncoderProgressWatchdogDecision(false, "编码进度仍在推进。", noProgressFor);
        }

        return new EncoderProgressWatchdogDecision(
            true,
            $"超过 {_options.NoProgressTimeout.TotalSeconds:0} 秒未观察到 out_time/frame 进展。",
            noProgressFor);
    }
}

public sealed record EtaEstimate(
    TimeSpan? Remaining,
    bool IsStable,
    double? SmoothedSpeed = null,
    int SampleCount = 0,
    int ValidSpeedSampleCount = 0,
    EtaConfidence Confidence = EtaConfidence.Unknown)
{
    public string Display => IsStable && Remaining is { } remaining
        ? DisplayFormat.EstimatedDuration(remaining)
        : "计算中";
}

/// <summary>
/// Smooths ETA from recent progress samples. A single noisy speed report is
/// not enough to expose a countdown in the UI.
/// </summary>
public sealed class EtaCalculator
{
    private const int MaximumSamples = 512;
    private const double SpeedSmoothingFactor = 0.25;
    private const double RemainingSmoothingFactor = 0.25;
    private readonly Queue<ProgressSample> _samples = new();
    private readonly int _minimumSamples;
    private readonly TimeSpan _window;
    private readonly TimeSpan _minimumRuntime;
    private readonly bool _requireValidSpeedSamples;
    private double? _smoothedSpeed;
    private TimeSpan? _smoothedRemaining;
    private double _lastProcessedSeconds;

    public EtaCalculator(int minimumSamples = 3, TimeSpan? window = null, TimeSpan? minimumRuntime = null)
    {
        _minimumSamples = Math.Max(2, minimumSamples);
        _window = window ?? TimeSpan.FromSeconds(8);
        _minimumRuntime = minimumRuntime.GetValueOrDefault();
    }

    public EtaCalculator(EtaCalculatorOptions options)
        : this(options.MinimumSamples, options.SampleWindow, options.MinimumRuntime)
    {
        _requireValidSpeedSamples = options.RequireValidSpeedSamples;
    }

    public double? SmoothedSpeed => _smoothedSpeed;
    public int SampleCount => _samples.Count;
    public int ValidSpeedSampleCount => _samples.Count(sample => sample.Speed is not null);
    public double LastProcessedSeconds => _lastProcessedSeconds;

    public EtaEstimate Update(
        double processedSeconds,
        double? totalSeconds,
        DateTimeOffset? at = null,
        double? reportedSpeed = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (processedSeconds >= 0 && double.IsFinite(processedSeconds))
        {
            _lastProcessedSeconds = Math.Max(_lastProcessedSeconds, processedSeconds);
            var previous = _samples.Count == 0 ? null : _samples.Last();
            var derivedSpeed = previous is { } prior &&
                               now > prior.At &&
                               processedSeconds > prior.Seconds + 0.1
                ? (processedSeconds - prior.Seconds) / (now - prior.At).TotalSeconds
                : (double?)null;
            var speed = IsUsableSpeed(reportedSpeed) ? reportedSpeed : derivedSpeed;
            _samples.Enqueue(new ProgressSample(now, processedSeconds, speed));
        }
        while (_samples.Count > 0 && _window > TimeSpan.Zero && now - _samples.Peek().At > _window)
        {
            _samples.Dequeue();
        }
        while (_samples.Count > MaximumSamples)
        {
            _samples.Dequeue();
        }

        var validSpeedSamples = ValidSpeedSampleCount;
        if (_samples.Count > 0)
        {
            var newestSpeed = _samples.Last().Speed;
            if (IsUsableSpeed(newestSpeed))
            {
                _smoothedSpeed = _smoothedSpeed is { } previousSpeed
                    ? previousSpeed * (1 - SpeedSmoothingFactor) + newestSpeed!.Value * SpeedSmoothingFactor
                    : newestSpeed;
            }
        }

        var enoughSamples = _requireValidSpeedSamples
            ? validSpeedSamples >= _minimumSamples
            : _samples.Count >= _minimumSamples;
        if (totalSeconds is not > 0 || !enoughSamples)
        {
            return UnstableEstimate(validSpeedSamples);
        }

        var first = _samples.Peek();
        var last = _samples.Last();
        var elapsed = (last.At - first.At).TotalSeconds;
        if (elapsed < 0.5 || elapsed < _minimumRuntime.TotalSeconds || _smoothedSpeed is not > 0)
        {
            return UnstableEstimate(validSpeedSamples);
        }

        var remainingSeconds = Math.Max(0, totalSeconds.Value - last.Seconds);
        if (remainingSeconds <= 0.05)
        {
            _smoothedRemaining = TimeSpan.Zero;
            return new EtaEstimate(
                TimeSpan.Zero,
                true,
                _smoothedSpeed,
                _samples.Count,
                validSpeedSamples,
                GetConfidence(elapsed, validSpeedSamples));
        }

        var rawRemaining = TimeSpan.FromSeconds(remainingSeconds / _smoothedSpeed.Value);
        var remaining = SmoothRemaining(rawRemaining);
        return new EtaEstimate(
            remaining,
            true,
            _smoothedSpeed,
            _samples.Count,
            validSpeedSamples,
            GetConfidence(elapsed, validSpeedSamples));
    }

    public void Reset()
    {
        _samples.Clear();
        _smoothedSpeed = null;
        _smoothedRemaining = null;
        _lastProcessedSeconds = 0;
    }

    private EtaEstimate UnstableEstimate(int validSpeedSamples) =>
        new(null, false, _smoothedSpeed, _samples.Count, validSpeedSamples, EtaConfidence.Unknown);

    private TimeSpan SmoothRemaining(TimeSpan rawRemaining)
    {
        if (_smoothedRemaining is not { } previous)
        {
            _smoothedRemaining = rawRemaining;
            return rawRemaining;
        }

        // Bound a single update before applying the EMA. The limits prevent a
        // noisy FFmpeg speed line from changing hours into minutes, while a
        // new fallback attempt gets a fresh calculator and can reset fully.
        var maximumDownwardChange = TimeSpan.FromSeconds(Math.Max(2, previous.TotalSeconds * 0.35));
        var maximumUpwardChange = TimeSpan.FromSeconds(Math.Max(2, previous.TotalSeconds * 0.50));
        var bounded = TimeSpan.FromSeconds(Math.Clamp(
            rawRemaining.TotalSeconds,
            Math.Max(0, previous.TotalSeconds - maximumDownwardChange.TotalSeconds),
            previous.TotalSeconds + maximumUpwardChange.TotalSeconds));
        _smoothedRemaining = TimeSpan.FromSeconds(
            previous.TotalSeconds * (1 - RemainingSmoothingFactor) +
            bounded.TotalSeconds * RemainingSmoothingFactor);
        return _smoothedRemaining.Value;
    }

    private static bool IsUsableSpeed(double? speed) =>
        speed is > 0 && double.IsFinite(speed.Value) && speed.Value <= 1_000;

    private static EtaConfidence GetConfidence(double elapsedSeconds, int validSpeedSamples) =>
        validSpeedSamples >= 12 && elapsedSeconds >= 30
            ? EtaConfidence.High
            : validSpeedSamples >= 8 && elapsedSeconds >= 20
                ? EtaConfidence.Medium
                : validSpeedSamples >= 5
                    ? EtaConfidence.Low
                    : EtaConfidence.Unknown;

    private sealed record ProgressSample(DateTimeOffset At, double Seconds, double? Speed);
}
