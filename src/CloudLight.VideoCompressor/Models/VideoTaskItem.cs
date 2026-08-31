using CloudLight.VideoCompressor.Infrastructure;

namespace CloudLight.VideoCompressor.Models;

public sealed class VideoTaskItem : ObservableObject
{
    private VideoFileInfo _media;
    private bool _isSelected = true;
    private VideoTaskStatus _status = VideoTaskStatus.Waiting;
    private string _statusDetail = "等待处理";
    private ConditionEvaluationResult _conditionResult = ConditionEvaluationResult.Pending;
    private double _progressPercent;
    private double? _encodingFps;
    private string? _encodingSpeed;
    private long? _outputSizeBytes;
    private TimeSpan? _remaining;
    private SmartCompressionDecision? _smartDecision;
    private VideoEncoder? _selectedEncoder;

    public VideoTaskItem(VideoFileInfo media) => _media = media;

    public VideoFileInfo Media
    {
        get => _media;
        private set
        {
            _media = value;
            OnPropertyChanged();
            foreach (var property in MediaDisplayProperties)
            {
                OnPropertyChanged(property);
            }
        }
    }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public VideoTaskStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                OnPropertyChanged(nameof(StatusTooltip));
            }
        }
    }

    public ConditionEvaluationResult ConditionResult
    {
        get => _conditionResult;
        private set
        {
            if (SetProperty(ref _conditionResult, value))
            {
                OnPropertyChanged(nameof(ConditionDisplay));
                OnPropertyChanged(nameof(ConditionTooltip));
                OnPropertyChanged(nameof(ConditionStateSort));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public double ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }
    public double? EncodingFps { get => _encodingFps; set => SetProperty(ref _encodingFps, value); }
    public string? EncodingSpeed { get => _encodingSpeed; set => SetProperty(ref _encodingSpeed, value); }
    public long? OutputSizeBytes { get => _outputSizeBytes; set => SetProperty(ref _outputSizeBytes, value); }
    public TimeSpan? Remaining { get => _remaining; set => SetProperty(ref _remaining, value); }

    public SmartCompressionDecision? SmartDecision
    {
        get => _smartDecision;
        private set
        {
            if (SetProperty(ref _smartDecision, value))
            {
                OnPropertyChanged(nameof(SmartDecisionDisplay));
                OnPropertyChanged(nameof(SmartDecisionTooltip));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public VideoEncoder? SelectedEncoder
    {
        get => _selectedEncoder;
        private set
        {
            if (SetProperty(ref _selectedEncoder, value))
            {
                OnPropertyChanged(nameof(EncoderDisplay));
            }
        }
    }

    public string FileName => Media.FileName;
    public string FullPath => Media.FullPath;
    public long FileSizeBytes => Media.FileSizeBytes;
    public double DurationSecondsSort => Media.DurationSeconds ?? -1;
    public int ResolutionPixelsSort => (Media.Width ?? 0) * (Media.Height ?? 0);
    public double FrameRateSort => Media.FrameRate ?? -1;
    public long VideoBitrateBpsSort => Media.VideoBitrateBps ?? -1;
    public long TotalBitrateBpsSort => Media.TotalBitrateBps ?? -1;
    public string SizeDisplay => DisplayFormat.FileSize(Media.FileSizeBytes);
    public string DurationDisplay => DisplayFormat.Duration(Media.DurationSeconds);
    public string ResolutionDisplay => Media.Width is not null && Media.Height is not null ? $"{Media.Width} × {Media.Height}" : "—";
    public string FpsDisplay => Media.FrameRate is not null ? $"{Media.FrameRate:0.###}" : "—";
    public string VideoBitrateDisplay => DisplayFormat.Bitrate(Media.VideoBitrateBps);
    public string TotalBitrateDisplay => DisplayFormat.Bitrate(Media.TotalBitrateBps);
    public string VideoCodecDisplay => Media.VideoCodec ?? "—";
    public string AudioDisplay => Media.AudioTrackCount == 0 ? "无" : $"{Media.AudioCodec ?? "未知"} × {Media.AudioTrackCount}";
    public MediaHealthStatus HealthStatus => Media.HealthStatus;
    public string HealthStatusDisplay => Media.HealthStatus switch
    {
        MediaHealthStatus.Healthy => "健康",
        MediaHealthStatus.Warning => "警告",
        MediaHealthStatus.Corrupt => "损坏",
        _ => "未检查"
    };
    public string HealthStatusTooltip => string.IsNullOrWhiteSpace(Media.HealthCheckMessage)
        ? "尚未执行健康检查。"
        : Media.HealthCheckMessage;
    public string ConditionDisplay => ConditionResult.State switch
    {
        ConditionResultState.Matches => "符合",
        ConditionResultState.DoesNotMatch => "不符合",
        ConditionResultState.AllAllowed => "全部允许",
        ConditionResultState.Failed => "判断失败",
        _ => "待判断"
    };
    public string ConditionTooltip => ConditionResult.Tooltip;
    public int ConditionStateSort => (int)ConditionResult.State;
    public string StatusDisplay => Status switch
    {
        VideoTaskStatus.Waiting => "待处理",
        VideoTaskStatus.Eligible => "符合条件",
        VideoTaskStatus.Skipped when ConditionResult.State == ConditionResultState.DoesNotMatch => "条件跳过",
        VideoTaskStatus.Skipped when SmartDecision is { ShouldCompress: false } => "智能跳过",
        _ => Status.GetDescription()
    };
    public string StatusTooltip => StatusDetail;
    public string ProgressDisplay => Status is VideoTaskStatus.Compressing or VideoTaskStatus.Verifying or VideoTaskStatus.Committing
        ? $"{ProgressPercent:0.0}%"
        : Status == VideoTaskStatus.Completed ? "100%" : "—";
    public string EncodingDisplay => EncodingSpeed is null ? "—" : $"{EncodingFps?.ToString("0.0") ?? "?"} FPS · {EncodingSpeed}";
    public string OutputSizeDisplay => OutputSizeBytes is null ? "—" : DisplayFormat.FileSize(OutputSizeBytes.Value);
    public string RemainingDisplay => Remaining is null ? "—" : DisplayFormat.Duration(Remaining.Value.TotalSeconds);
    public string SmartDecisionDisplay => SmartDecision is null ? "—" : SmartDecision.DecisionDisplay;
    public string SmartDecisionTooltip => SmartDecision?.Explanation ?? string.Empty;
    public string EncoderDisplay => SelectedEncoder is null ? "—" : EncoderCatalog.Get(SelectedEncoder.Value).DisplayName;

    public void UpdateMedia(VideoFileInfo media) => Media = media;

    public void ApplyConditionResult(ConditionEvaluationResult result) => ConditionResult = result;

    public void ApplySmartDecision(SmartCompressionDecision? decision) => SmartDecision = decision;

    public void ApplySelectedEncoder(VideoEncoder? encoder) => SelectedEncoder = encoder;

    public void ApplyProgress(EncodingProgress progress)
    {
        ProgressPercent = progress.Percent;
        EncodingFps = progress.Fps;
        EncodingSpeed = progress.Speed;
        OutputSizeBytes = progress.TotalSizeBytes;
        Remaining = progress.Remaining;
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(EncodingDisplay));
        OnPropertyChanged(nameof(OutputSizeDisplay));
        OnPropertyChanged(nameof(RemainingDisplay));
    }

    public void RefreshDisplays()
    {
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(EncodingDisplay));
        OnPropertyChanged(nameof(OutputSizeDisplay));
        OnPropertyChanged(nameof(RemainingDisplay));
    }

    private static readonly string[] MediaDisplayProperties =
    [
        nameof(FileName), nameof(FullPath), nameof(FileSizeBytes), nameof(DurationSecondsSort), nameof(ResolutionPixelsSort),
        nameof(FrameRateSort), nameof(VideoBitrateBpsSort), nameof(TotalBitrateBpsSort), nameof(SizeDisplay), nameof(DurationDisplay),
        nameof(ResolutionDisplay), nameof(FpsDisplay), nameof(VideoBitrateDisplay), nameof(TotalBitrateDisplay),
        nameof(VideoCodecDisplay), nameof(AudioDisplay), nameof(HealthStatus), nameof(HealthStatusDisplay),
        nameof(HealthStatusTooltip)
    ];
}

public static class DisplayFormat
{
    public static string FileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    public static string Bitrate(long? bitsPerSecond) => bitsPerSecond is null ? "—" : $"{bitsPerSecond.Value / 1_000_000d:0.##} Mbps";

    public static string Duration(double? seconds)
    {
        if (seconds is null || seconds <= 0)
        {
            return "—";
        }

        return TimeSpan.FromSeconds(seconds.Value).ToString(seconds.Value >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    }

    public static string EstimatedDuration(TimeSpan duration)
    {
        var seconds = Math.Max(0, (long)Math.Round(duration.TotalSeconds));
        if (seconds < 60)
        {
            return $"约 {seconds} 秒";
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
        {
            return span.Minutes == 0
                ? $"约 {span.Hours + span.Days * 24} 小时"
                : $"约 {span.Hours + span.Days * 24} 小时 {span.Minutes} 分钟";
        }

        return span.Seconds == 0
            ? $"约 {span.Minutes} 分钟"
            : $"约 {span.Minutes} 分钟 {span.Seconds} 秒";
    }
}
