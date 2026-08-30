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

public sealed record EtaEstimate(TimeSpan? Remaining, bool IsStable)
{
    public string Display => IsStable && Remaining is { } remaining
        ? DisplayFormat.Duration(remaining.TotalSeconds)
        : "计算中";
}

/// <summary>
/// Smooths ETA from recent progress samples. A single noisy speed report is
/// not enough to expose a countdown in the UI.
/// </summary>
public sealed class EtaCalculator
{
    private readonly Queue<(DateTimeOffset At, double Seconds)> _samples = new();
    private readonly int _minimumSamples;
    private readonly TimeSpan _window;

    public EtaCalculator(int minimumSamples = 3, TimeSpan? window = null)
    {
        _minimumSamples = Math.Max(2, minimumSamples);
        _window = window ?? TimeSpan.FromSeconds(8);
    }

    public EtaEstimate Update(double processedSeconds, double? totalSeconds, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (processedSeconds >= 0)
        {
            _samples.Enqueue((now, processedSeconds));
        }
        while (_samples.Count > 0 && now - _samples.Peek().At > _window)
        {
            _samples.Dequeue();
        }

        if (totalSeconds is not > 0 || _samples.Count < _minimumSamples)
        {
            return new EtaEstimate(null, false);
        }

        var first = _samples.Peek();
        var last = _samples.Last();
        var elapsed = (last.At - first.At).TotalSeconds;
        var processed = last.Seconds - first.Seconds;
        if (elapsed < 0.5 || processed <= 0.1)
        {
            return new EtaEstimate(null, false);
        }

        var speed = processed / elapsed;
        return new EtaEstimate(
            TimeSpan.FromSeconds(Math.Max(0, (totalSeconds.Value - last.Seconds) / speed)),
            true);
    }
}
