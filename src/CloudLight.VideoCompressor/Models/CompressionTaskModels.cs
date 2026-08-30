using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Models;

public enum CompressionParameterChangeType
{
    Unchanged,
    Changed,
    Reduced,
    Increased,
    Converted,
    Copied
}

/// <summary>
/// One row in the before/after plan comparison. The planner decides the change
/// type once so that the task page never has to infer intent from formatted text.
/// </summary>
public sealed record CompressionParameterChange(
    string Parameter,
    string OldValue,
    string NewValue,
    CompressionParameterChangeType ChangeType)
{
    public bool IsChanged => ChangeType is not CompressionParameterChangeType.Unchanged and not CompressionParameterChangeType.Copied;

    public string ChangeDisplay => ChangeType switch
    {
        CompressionParameterChangeType.Unchanged => "保持",
        CompressionParameterChangeType.Copied => "复制 · 保持",
        CompressionParameterChangeType.Reduced => "将降低",
        CompressionParameterChangeType.Increased => "将提高",
        CompressionParameterChangeType.Converted => "将转换",
        _ => "将修改"
    };
}

public sealed record CompressionPlanComparison(IReadOnlyList<CompressionParameterChange> Parameters)
{
    public bool HasChanges => Parameters.Any(parameter => parameter.IsChanged);
}

/// <summary>
/// UI state for a planned entry. It intentionally differs from VideoTaskStatus,
/// which is still used by the legacy scan/direct-processing surface and workflow.
/// </summary>
public enum CompressionExecutionState
{
    [System.ComponentModel.Description("等待开始")]
    WaitingToStart,
    [System.ComponentModel.Description("排队中")]
    Queued,
    [System.ComponentModel.Description("压缩中")]
    Compressing,
    [System.ComponentModel.Description("验证中")]
    Verifying,
    [System.ComponentModel.Description("正在提交输出")]
    Committing,
    [System.ComponentModel.Description("已完成")]
    Completed,
    [System.ComponentModel.Description("已取消")]
    Cancelled,
    [System.ComponentModel.Description("失败")]
    Failed,
    [System.ComponentModel.Description("放弃结果")]
    Abandoned
}

public sealed class CompressionTaskEntry : ObservableObject
{
    private CompressionExecutionState _executionState = CompressionExecutionState.WaitingToStart;
    private double _progressPercent;
    private VideoEncoder? _actualEncoder;
    private string _statusDetail = "等待开始";
    private string? _failureReason;
    private string? _fallbackReason;
    private VideoFileInfo? _finalVideoInfo;
    private long? _actualOutputSizeBytes;
    private bool _isDetailsExpanded;
    private CompressionJobResult? _result;

    public CompressionTaskEntry(
        VideoFileInfo source,
        CompressionPlan plan,
        ConditionEvaluationResult conditionEvaluation,
        CompressionPlanComparison comparison)
    {
        Source = source;
        Plan = plan;
        ConditionEvaluation = conditionEvaluation;
        SmartDecision = plan.SmartDecision;
        Comparison = comparison;
    }

    public VideoFileInfo Source { get; }
    public CompressionPlan Plan { get; }
    public ConditionEvaluationResult ConditionEvaluation { get; }
    public SmartCompressionDecision? SmartDecision { get; }
    public CompressionPlanComparison Comparison { get; }
    public CompressionJobResult? Result => _result;

    public CompressionExecutionState ExecutionState
    {
        get => _executionState;
        set
        {
            if (SetProperty(ref _executionState, value))
            {
                OnPropertyChanged(nameof(ExecutionStateDisplay));
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(ResultHeading));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (SetProperty(ref _progressPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public VideoEncoder? ActualEncoder
    {
        get => _actualEncoder;
        private set
        {
            if (SetProperty(ref _actualEncoder, value))
            {
                OnPropertyChanged(nameof(ActualEncoderDisplay));
                OnPropertyChanged(nameof(ActualEncoderIdDisplay));
                OnPropertyChanged(nameof(ActualEncoderKindDisplay));
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
                OnPropertyChanged(nameof(FailureReasonDisplay));
            }
        }
    }

    public string? FailureReason
    {
        get => _failureReason;
        private set
        {
            if (SetProperty(ref _failureReason, value))
            {
                OnPropertyChanged(nameof(FailureReasonDisplay));
                OnPropertyChanged(nameof(HasFailureReason));
            }
        }
    }

    public string? FallbackReason
    {
        get => _fallbackReason;
        private set
        {
            if (SetProperty(ref _fallbackReason, value))
            {
                OnPropertyChanged(nameof(HasFallback));
            }
        }
    }

    public VideoFileInfo? FinalVideoInfo
    {
        get => _finalVideoInfo;
        private set
        {
            if (SetProperty(ref _finalVideoInfo, value))
            {
                OnPropertyChanged(nameof(ActualVideoCodecDisplay));
                OnPropertyChanged(nameof(ActualResolutionDisplay));
                OnPropertyChanged(nameof(ActualResolutionFpsDisplay));
                OnPropertyChanged(nameof(ActualFpsDisplay));
                OnPropertyChanged(nameof(ActualVideoBitrateDisplay));
                OnPropertyChanged(nameof(ActualTotalBitrateDisplay));
                OnPropertyChanged(nameof(ActualAudioDisplay));
            }
        }
    }

    public long? ActualOutputSizeBytes
    {
        get => _actualOutputSizeBytes;
        private set
        {
            if (SetProperty(ref _actualOutputSizeBytes, value))
            {
                OnPropertyChanged(nameof(ActualOutputSizeDisplay));
                OnPropertyChanged(nameof(ActualSizeSavingDisplay));
                OnPropertyChanged(nameof(ActualSavingDisplay));
                OnPropertyChanged(nameof(ResultSizeDisplay));
            }
        }
    }

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set => SetProperty(ref _isDetailsExpanded, value);
    }

    public string FileName => Source.FileName;
    public string FullPath => Source.FullPath;
    public string OutputPath => Plan.TargetPath ?? "—";
    public string SourceSizeDisplay => DisplayFormat.FileSize(Source.FileSizeBytes);
    public string PlannedEncoderDisplay => EncoderCatalog.Get(Plan.Encoder).DisplayName;
    public string PlannedEncoderIdDisplay => CompressionPlan.FfmpegEncoderName(Plan.Encoder);
    public string PlannedEncoderKindDisplay => EncoderCatalog.Get(Plan.Encoder).IsHardware ? "GPU 硬件编码" : "CPU 软件编码";
    public string ActualEncoderDisplay => ActualEncoder is { } encoder ? EncoderCatalog.Get(encoder).DisplayName : "—";
    public string ActualEncoderIdDisplay => ActualEncoder is { } encoder ? CompressionPlan.FfmpegEncoderName(encoder) : "—";
    public string ActualEncoderKindDisplay => ActualEncoder is { } encoder
        ? EncoderCatalog.Get(encoder).IsHardware ? "GPU 硬件编码" : "CPU 软件编码"
        : "—";
    public string ExecutionStateDisplay => ExecutionState.GetDescription();
    public string ProgressDisplay => $"{ProgressPercent:0.0}%";
    public string PlannedOutputSizeDisplay => Plan.EstimatedOutputDisplay;
    public string PlannedSavingDisplay => Plan.EstimatedSavingDisplay(Source.FileSizeBytes);
    public string ResultHeading => ExecutionState == CompressionExecutionState.Completed ? "实际结果" : "计划压缩后";
    public string ResultSizeDisplay => ExecutionState == CompressionExecutionState.Completed && ActualOutputSizeBytes is { } actual
        ? $"{SourceSizeDisplay} → {DisplayFormat.FileSize(actual)}"
        : $"{SourceSizeDisplay} → {PlannedOutputSizeDisplay}";
    public string ActualOutputSizeDisplay => ActualOutputSizeBytes is { } actual ? DisplayFormat.FileSize(actual) : "—";
    public string ActualSavingDisplay => ActualOutputSizeBytes is { } actual && Source.FileSizeBytes > 0
        ? $"{Math.Clamp((1 - actual / (double)Source.FileSizeBytes) * 100, -9_999, 9_999):0.0}%"
        : "—";
    public string ActualSizeSavingDisplay => ActualOutputSizeBytes is { }
        ? $"{ActualOutputSizeDisplay} · 节省 {ActualSavingDisplay}"
        : "—";
    public string ActualVideoCodecDisplay => FinalVideoInfo?.VideoCodec ?? "—";
    public string ActualResolutionDisplay => FinalVideoInfo?.Width is { } width && FinalVideoInfo.Height is { } height
        ? $"{width} × {height}"
        : "—";
    public string ActualFpsDisplay => FinalVideoInfo?.FrameRate is { } fps ? $"{fps:0.###} FPS" : "—";
    public string ActualResolutionFpsDisplay => FinalVideoInfo is null
        ? "—"
        : $"{ActualResolutionDisplay} · {ActualFpsDisplay}";
    public string ActualVideoBitrateDisplay => DisplayFormat.Bitrate(FinalVideoInfo?.VideoBitrateBps);
    public string ActualTotalBitrateDisplay => DisplayFormat.Bitrate(FinalVideoInfo?.TotalBitrateBps);
    public string ActualAudioDisplay => FinalVideoInfo is null
        ? "—"
        : FinalVideoInfo.AudioTrackCount == 0
            ? "无"
            : $"{FinalVideoInfo.AudioCodec ?? "未知"} · {DisplayFormat.Bitrate(FinalVideoInfo.AudioBitrateBps)} · {FinalVideoInfo.AudioTrackCount} 轨";
    public string FailureReasonDisplay => FailureReason ?? StatusDetail;
    public bool HasFailureReason => !string.IsNullOrWhiteSpace(FailureReason);
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackReason);
    public string DecisionReasonDisplay => Plan.Reason ?? SmartDecision?.Reason ?? ConditionEvaluation.Summary;
    public string SmartExplanationDisplay => SmartDecision?.Explanation ?? string.Empty;
    public bool HasSmartExplanation => SmartDecision is not null;

    public void ApplyProgress(WorkflowProgress progress)
    {
        ExecutionState = ToExecutionState(progress.Status);
        StatusDetail = progress.Detail;
        if (progress.Encoder is { } encoder)
        {
            ActualEncoder = encoder;
        }

        if (progress.Encoding is { } encoding)
        {
            ProgressPercent = encoding.Percent;
        }
    }

    public void ApplyResult(CompressionJobResult result)
    {
        _result = result;
        OnPropertyChanged(nameof(Result));
        if (result.Encoder is { } encoder)
        {
            ActualEncoder = encoder;
        }

        FinalVideoInfo = result.OutputInfo;
        ActualOutputSizeBytes = result.OutputInfo?.FileSizeBytes;
        FallbackReason = result.FallbackReason;
        StatusDetail = result.Message;
        FailureReason = result.Status is VideoTaskStatus.Failed or VideoTaskStatus.Cancelled or VideoTaskStatus.Skipped
            ? result.Message
            : null;
        ExecutionState = result.Status switch
        {
            VideoTaskStatus.Completed => CompressionExecutionState.Completed,
            VideoTaskStatus.Cancelled => CompressionExecutionState.Cancelled,
            VideoTaskStatus.Skipped => CompressionExecutionState.Abandoned,
            _ => CompressionExecutionState.Failed
        };
        if (ExecutionState == CompressionExecutionState.Completed)
        {
            ProgressPercent = 100;
        }
    }

    public void MarkQueued() =>
        (ExecutionState, StatusDetail) = (CompressionExecutionState.Queued, "排队中");

    public void MarkCancelled(string detail = "任务已取消，原文件未被修改。")
    {
        ExecutionState = CompressionExecutionState.Cancelled;
        StatusDetail = detail;
        FailureReason = detail;
    }

    private static CompressionExecutionState ToExecutionState(VideoTaskStatus status) => status switch
    {
        VideoTaskStatus.Queued => CompressionExecutionState.Queued,
        VideoTaskStatus.Compressing => CompressionExecutionState.Compressing,
        VideoTaskStatus.Verifying => CompressionExecutionState.Verifying,
        VideoTaskStatus.Committing => CompressionExecutionState.Committing,
        VideoTaskStatus.Completed => CompressionExecutionState.Completed,
        VideoTaskStatus.Cancelled => CompressionExecutionState.Cancelled,
        VideoTaskStatus.Skipped => CompressionExecutionState.Abandoned,
        _ => CompressionExecutionState.Failed
    };
}

public sealed class CompressionTaskSession
{
    public CompressionTaskSession(
        IEnumerable<CompressionTaskEntry> entries,
        AppSettings settingsSnapshot,
        string scanRoot,
        IReadOnlyList<string>? planningNotes = null)
    {
        Entries = new System.Collections.ObjectModel.ObservableCollection<CompressionTaskEntry>(entries);
        SettingsSnapshot = settingsSnapshot;
        ScanRoot = scanRoot;
        PlanningNotes = planningNotes ?? Array.Empty<string>();
    }

    public System.Collections.ObjectModel.ObservableCollection<CompressionTaskEntry> Entries { get; }
    public AppSettings SettingsSnapshot { get; }
    public string ScanRoot { get; }
    public IReadOnlyList<string> PlanningNotes { get; }
}
