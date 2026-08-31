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
    Copied,
    Removed,
    Added
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
        CompressionParameterChangeType.Removed => "将移除",
        CompressionParameterChangeType.Added => "将添加",
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
    Abandoned,
    [System.ComponentModel.Description("上次运行中断")]
    Interrupted,
    [System.ComponentModel.Description("源文件已变化")]
    SourceChanged
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
    private EncodingProgress? _latestEncoding;
    private CompressionProgress? _latestProgress;
    private TimeSpan? _estimatedDuration;
    private bool _isDetailsExpanded;
    private CompressionJobResult? _result;

    public CompressionTaskEntry(
        VideoFileInfo source,
        CompressionPlan plan,
        ConditionEvaluationResult conditionEvaluation,
        CompressionPlanComparison comparison,
        CompressionJob? job = null,
        MediaFileFingerprint? sourceFingerprint = null)
    {
        Source = source;
        Plan = plan;
        ConditionEvaluation = conditionEvaluation;
        SmartDecision = plan.SmartDecision;
        Comparison = comparison;
        Job = job ?? CompressionJob.Create(source, new AppSettings(), conditionEvaluation, null).WithPlan(plan);
        SourceFingerprint = sourceFingerprint ?? MediaFileFingerprint.FromVideoInfo(source);
    }

    public VideoFileInfo Source { get; }
    public CompressionPlan Plan { get; }
    public CompressionJob Job { get; }
    public ConditionEvaluationResult ConditionEvaluation { get; }
    public SmartCompressionDecision? SmartDecision { get; }
    public CompressionPlanComparison Comparison { get; }
    public MediaFileFingerprint SourceFingerprint { get; }
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
                OnPropertyChanged(nameof(HasActualResult));
                OnPropertyChanged(nameof(HasResultRejection));
                OnPropertyChanged(nameof(IsValidationFailed));
                OnPropertyChanged(nameof(ProgressEtaDisplay));
                OnPropertyChanged(nameof(TimeEstimateDisplay));
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
    public string PlannedCodecDisplay => Plan.EffectiveTargetCodec.GetDescription();
    public string PlannedTuningDisplay => Plan.EncoderTuningPreset.GetDescription();
    public string PlannedBitDepthDisplay => $"{Plan.TargetBitDepth}-bit · {Plan.TargetPixelFormat ?? "默认像素格式"}";
    public string PlannedProfileDisplay => Plan.TargetProfile ?? "默认 profile";
    public string HdrProtectionDisplay => Plan.IsHdrSource
        ? $"HDR 保护：源 {Source.HdrSummaryDisplay} · 保留色彩标签{(Source.MasteringDisplayMetadata.Count > 0 || Source.ContentLightMetadata.Count > 0 ? "及 HDR 元数据" : string.Empty)}"
        : $"色彩保护：{Source.HdrSummaryDisplay}";
    public string AutoEncoderDecisionDisplay => Plan.AutoEncoderDecision is { } decision
        ? $"Auto 选择：{EncoderCatalog.Get(decision.SelectedEncoder).DisplayName} · {decision.ConfidenceDisplay}置信度 · score {decision.SelectedScore:0.0}/100 · 回退：{(decision.FallbackChain.Count == 0 ? "无" : string.Join("、", decision.FallbackChain.Select(encoder => EncoderCatalog.Get(encoder).DisplayName)))}"
        : string.Empty;
    public bool HasAutoEncoderDecision => Plan.AutoEncoderDecision is not null;
    public string JobIdDisplay => Job.JobId[..Math.Min(12, Job.JobId.Length)];
    public string PlanIdDisplay => Plan.PlanId.ToString("N")[..12];
    public string RateControlDisplay => Plan.RateControlDisplay;
    public string TargetVideoBitrateDisplay => DisplayFormat.Bitrate(Plan.TargetVideoBitrateBps);
    public string MaxVideoBitrateDisplay => DisplayFormat.Bitrate(Plan.MaxVideoBitrateBps);
    public string TargetAndMaxVideoBitrateDisplay => $"{TargetVideoBitrateDisplay} / {MaxVideoBitrateDisplay}";
    public string SourceBppfDisplay => Plan.SourceBitsPerPixelPerFrame is { } sourceBppf ? $"{sourceBppf:0.000000}" : "—";
    public string TargetBppfDisplay => Plan.TargetBitsPerPixelPerFrame is { } targetBppf ? $"{targetBppf:0.000000}" : "—";
    public string SourceTargetBppfDisplay => $"{SourceBppfDisplay} → {TargetBppfDisplay}";
    public string BenefitScoreDisplay => $"{Plan.CompressionBenefitScore:0.0} / 100";
    public string QualityCalibrationDisplay => Plan.QualityCalibration is { IsAvailable: true } calibration
        ? $"推荐质量参数：{Plan.RateControlDisplay} · {calibration.Measurements.Count} 次抽样"
        : Plan.Warnings.FirstOrDefault(warning => warning.Contains("VMAF", StringComparison.OrdinalIgnoreCase)) ?? "未启用";
    public bool HasQualityCalibration => Plan.QualityCalibration is { IsAvailable: true };
    public string QualityCalibrationSummaryDisplay
    {
        get
        {
            if (Plan.QualityCalibration is not { IsAvailable: true } calibration)
            {
                return string.Empty;
            }

            var selected = calibration.SelectedQuality is { } quality
                ? calibration.Measurements.Where(measurement => Math.Abs(measurement.Quality - quality) < 0.01)
                : [];
            var measured = selected.Any()
                ? $"VMAF {selected.Average(measurement => measurement.Score):0.0}"
                : "VMAF 已测得";
            var target = SmartDecision?.Preset.GetDescription() ?? "自定义";
            var recommendation = RateControlDisplay;
            return $"质量校准：已完成 · 目标视觉质量：{target} · 测得：{measured} · 推荐质量参数：{recommendation}";
        }
    }
    public string QualityCalibrationDetailsDisplay => Plan.QualityCalibration is { IsAvailable: true } calibration
        ? string.Join(Environment.NewLine, calibration.Measurements.Select(measurement =>
            $"{measurement.Quality:0.##} · {measurement.Score:0.0} VMAF · {measurement.Encoder.GetDescription()} · {measurement.Sample.StartSeconds:0.##}–{measurement.Sample.EndSeconds:0.##} 秒 · {measurement.Sample.ComplexityDisplay}"))
        : "—";
    public string CoreChangesDisplay => string.Join(" · ", Comparison.Parameters
        .Where(parameter => parameter.Parameter is "视频编码" or "视频码率" or "分辨率" or "FPS" or "音频编码" or "音频码率" or "位深 / HDR" or "编码倾向")
        .Select(FormatCoreChange));
    public string CommandPreviewDisplay => BuildCommandPreviewDisplay();
    public string PlannedEncoderKindDisplay => EncoderCatalog.Get(Plan.Encoder).IsHardware ? "GPU 硬件编码" : "CPU 软件编码";
    public string ActualEncoderDisplay => ActualEncoder is { } encoder ? EncoderCatalog.Get(encoder).DisplayName : "—";
    public string ActualEncoderIdDisplay => ActualEncoder is { } encoder ? CompressionPlan.FfmpegEncoderName(encoder) : "—";
    public string ActualEncoderKindDisplay => ActualEncoder is { } encoder
        ? EncoderCatalog.Get(encoder).IsHardware ? "GPU 硬件编码" : "CPU 软件编码"
        : "—";
    public string ActualEncoderSummaryDisplay => HasActualEncoderResult
        ? $"实际编码方式：{ActualEncoderDisplay}"
        : string.Empty;
    public bool HasActualEncoderResult => _result?.Encoder is not null;
    public string ExecutionStateDisplay => IsValidationFailed
        ? "验证失败"
        : HasResultRejection
            ? "已放弃结果"
            : ExecutionState.GetDescription();
    public string ProgressDisplay => $"{ProgressPercent:0.0}%";
    public string PlannedOutputSizeDisplay => Plan.EstimatedOutputDisplay;
    public string PlannedSavingDisplay => Plan.EstimatedSavingDisplay(Source.FileSizeBytes);
    public bool HasPlanWarnings => Plan.Warnings.Count > 0;
    public string PlanWarningsDisplay => Plan.Warnings.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, Plan.Warnings);
    public string StreamRetentionDisplay => Plan.StreamAudit?.SummaryDisplay
        ?? (Source.AudioTrackCount > 0 || Source.SubtitleTrackCount > 0
            ? $"音轨 {Source.AudioTrackCount} 条 · 字幕 {Source.SubtitleTrackCount} 条 · 按源容器保留"
            : "无额外流");
    public bool HasStreamAuditWarnings => Plan.StreamAudit?.Warnings.Count > 0;
    public string StreamAuditWarningsDisplay => Plan.StreamAudit is { Warnings.Count: > 0 } audit
        ? string.Join(Environment.NewLine, audit.Warnings)
        : string.Empty;
    public string ResultHeading => HasActualResult ? "实际结果" : "计划压缩后";
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
    public string ActualVideoCodecDisplay => FinalVideoInfo?.VideoCodec is { } codec
        ? CodecDisplay(codec)
        : "—";
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
    public string FailureReasonDisplay
    {
        get
        {
            if (HasResultRejection)
            {
                return "已放弃结果：压缩后的文件未小于源文件。源文件已保留。";
            }

            if (IsValidationFailed)
            {
                return $"验证失败：{FirstLine(Result?.Message) ?? "输出文件未通过完整性检查。"} 源文件已保留。";
            }

            if (ExecutionState == CompressionExecutionState.Cancelled)
            {
                return "任务已取消，源文件已保留。";
            }

            if (!string.IsNullOrWhiteSpace(FailureReason))
            {
                return $"处理失败：{FirstLine(FailureReason) ?? "编码未完成。"} 源文件已保留。";
            }

            return StatusDetail;
        }
    }
    public bool HasFailureReason => !string.IsNullOrWhiteSpace(FailureReason);
    public bool HasFallback => !string.IsNullOrWhiteSpace(FallbackReason);
    public bool HasActualResult => ExecutionState == CompressionExecutionState.Completed;
    public bool HasResultRejection => _result?.FailureKind == CompressionFailureKind.ResultRejected;
    public bool IsValidationFailed => _result?.FailureKind == CompressionFailureKind.ValidationFailed;
    public string DiagnosticErrorDisplay => Result?.Message ?? "—";
    public string FailureCategoryDisplay => Result?.FailureKind.GetDescription() ?? "—";
    public string DecisionReasonDisplay => Plan.Reason ?? SmartDecision?.Reason ?? ConditionEvaluation.Summary;
    public string SmartExplanationDisplay => SmartDecision?.Explanation ?? string.Empty;
    public bool HasSmartExplanation => SmartDecision is not null;
    public EncodingProgress? LatestEncoding => _latestEncoding;
    public CompressionProgress? LatestProgress => _latestProgress;
    public TimeSpan? EstimatedDuration
    {
        get => _estimatedDuration;
        private set
        {
            if (SetProperty(ref _estimatedDuration, value))
            {
                OnPropertyChanged(nameof(EstimatedDurationDisplay));
                OnPropertyChanged(nameof(TimeEstimateDisplay));
            }
        }
    }

    public string EstimatedDurationDisplay => EstimatedDuration is { } duration
        ? DisplayFormat.EstimatedDuration(duration)
        : "等待速度样本";
    public string ProgressEtaDisplay => ExecutionState switch
    {
        CompressionExecutionState.Compressing => _latestEncoding is { } encoding && encoding.IsEtaStable
            ? encoding.EtaDisplay
            : "计算中",
        CompressionExecutionState.Verifying => "正在验证…",
        CompressionExecutionState.Committing => "正在提交输出…",
        CompressionExecutionState.Queued or CompressionExecutionState.WaitingToStart => EstimatedDurationDisplay,
        _ => "—"
    };
    public string TimeEstimateDisplay => ExecutionState switch
    {
        CompressionExecutionState.Compressing => $"预计剩余 {ProgressEtaDisplay}",
        CompressionExecutionState.Verifying => "正在验证…",
        CompressionExecutionState.Committing => "正在提交输出…",
        CompressionExecutionState.Queued or CompressionExecutionState.WaitingToStart => $"预计耗时 {EstimatedDurationDisplay}",
        _ => "—"
    };
    public string ProgressSpeedDisplay => _latestEncoding?.Speed ?? "—";
    public string SmoothedEncodingSpeedDisplay => _latestEncoding?.SmoothedSpeed is { } speed
        ? $"{speed:0.00}x"
        : "—";
    public string ProgressBitrateDisplay => DisplayFormat.Bitrate(_latestEncoding?.BitrateBps);
    public string ProgressStageDisplay => _latestProgress?.Stage.GetDescription() ?? "—";
    public string AttemptsDisplay => Result?.Attempts is { Count: > 0 } attempts
        ? string.Join(" → ", attempts.Select(attempt => $"{CompressionPlan.FfmpegEncoderName(attempt.Encoder)}（{attempt.Status.GetDescription()}）"))
        : "—";

    public void ApplyProgress(WorkflowProgress progress)
    {
        ExecutionState = ToExecutionState(progress.Status);
        StatusDetail = progress.Detail;
        if (progress.Encoder is { } encoder)
        {
            ActualEncoder = encoder;
        }

        if (progress.ResetEta)
        {
            _latestEncoding = null;
            _latestProgress = null;
            OnPropertyChanged(nameof(ProgressEtaDisplay));
            OnPropertyChanged(nameof(TimeEstimateDisplay));
            OnPropertyChanged(nameof(ProgressSpeedDisplay));
            OnPropertyChanged(nameof(SmoothedEncodingSpeedDisplay));
            OnPropertyChanged(nameof(ProgressBitrateDisplay));
            OnPropertyChanged(nameof(ProgressStageDisplay));
        }

        if (progress.Encoding is { } encoding)
        {
            _latestEncoding = encoding;
            _latestProgress = progress.ProgressSnapshot ?? encoding.ToCompressionProgress(PipelineStage.Execute);
            ProgressPercent = encoding.Percent;
            OnPropertyChanged(nameof(ProgressEtaDisplay));
            OnPropertyChanged(nameof(ProgressSpeedDisplay));
            OnPropertyChanged(nameof(SmoothedEncodingSpeedDisplay));
            OnPropertyChanged(nameof(ProgressBitrateDisplay));
            OnPropertyChanged(nameof(ProgressStageDisplay));
            OnPropertyChanged(nameof(TimeEstimateDisplay));
        }
    }

    public void ApplyEstimatedDuration(TimeSpan? duration)
    {
        if (duration is { } value && value < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        EstimatedDuration = duration;
    }

    public void ApplyResult(CompressionJobResult result)
    {
        _result = result;
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(AttemptsDisplay));
        OnPropertyChanged(nameof(HasActualEncoderResult));
        OnPropertyChanged(nameof(HasResultRejection));
        OnPropertyChanged(nameof(IsValidationFailed));
        OnPropertyChanged(nameof(DiagnosticErrorDisplay));
        OnPropertyChanged(nameof(FailureCategoryDisplay));
        if (result.Encoder is { } encoder)
        {
            ActualEncoder = encoder;
        }

        FinalVideoInfo = result.OutputInfo;
        ActualOutputSizeBytes = result.OutputInfo?.FileSizeBytes;
        FallbackReason = result.FallbackReason;
        StatusDetail = result.Message;
        FailureReason = result.Status is VideoTaskStatus.Failed or VideoTaskStatus.Cancelled
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

    public void MarkInterrupted(string detail = "上次应用退出时任务正在执行；恢复后将从头重新处理该视频。")
    {
        _latestEncoding = null;
        _latestProgress = null;
        ProgressPercent = 0;
        ExecutionState = CompressionExecutionState.Interrupted;
        StatusDetail = detail;
        FailureReason = null;
        OnPropertyChanged(nameof(ProgressEtaDisplay));
        OnPropertyChanged(nameof(TimeEstimateDisplay));
        OnPropertyChanged(nameof(ProgressSpeedDisplay));
        OnPropertyChanged(nameof(SmoothedEncodingSpeedDisplay));
        OnPropertyChanged(nameof(ProgressBitrateDisplay));
        OnPropertyChanged(nameof(ProgressStageDisplay));
    }

    public void MarkSourceChanged(string detail = "源文件大小或修改时间已变化；必须重新规划后才能继续。")
    {
        ExecutionState = CompressionExecutionState.SourceChanged;
        StatusDetail = detail;
        FailureReason = detail;
        ProgressPercent = 0;
    }

    public void RestoreSnapshot(
        CompressionExecutionState state,
        double progressPercent,
        VideoEncoder? actualEncoder,
        string statusDetail,
        string? failureReason,
        string? fallbackReason,
        VideoFileInfo? finalVideoInfo,
        long? actualOutputSizeBytes,
        CompressionJobResult? result)
    {
        _result = result;
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(AttemptsDisplay));
        OnPropertyChanged(nameof(HasActualEncoderResult));
        OnPropertyChanged(nameof(HasResultRejection));
        OnPropertyChanged(nameof(IsValidationFailed));
        OnPropertyChanged(nameof(DiagnosticErrorDisplay));
        OnPropertyChanged(nameof(FailureCategoryDisplay));
        ActualEncoder = actualEncoder;
        FinalVideoInfo = finalVideoInfo;
        ActualOutputSizeBytes = actualOutputSizeBytes;
        FallbackReason = fallbackReason;
        FailureReason = failureReason;
        StatusDetail = statusDetail;
        ProgressPercent = progressPercent;
        ExecutionState = state;
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

    private string BuildCommandPreviewDisplay()
    {
        if (string.IsNullOrWhiteSpace(Plan.TargetPath))
        {
            return "—";
        }

        var arguments = Plan.BuildCommandPreview(Source.FullPath, Plan.TargetPath);
        return "ffmpeg " + string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
    }

    private static string FormatCoreChange(CompressionParameterChange change) =>
        change.ChangeType is CompressionParameterChangeType.Unchanged or CompressionParameterChangeType.Copied
            ? $"{change.Parameter}：{change.NewValue} · 保持"
            : $"{change.Parameter}：{change.OldValue} → {change.NewValue}";

    private static string? FirstLine(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    private static string CodecDisplay(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "h264" or "avc" => "H.264",
        "hevc" or "h265" => "H.265",
        "av1" => "AV1",
        _ => codec
    };
}

public sealed class CompressionTaskSession
{
    public CompressionTaskSession(
        IEnumerable<CompressionTaskEntry> entries,
        AppSettings settingsSnapshot,
        string scanRoot,
        IReadOnlyList<string>? planningNotes = null,
        LongRunningTaskPolicy? executionPolicy = null,
        string? sessionId = null,
        DateTimeOffset? createdAt = null,
        bool queuePaused = false)
    {
        Entries = new System.Collections.ObjectModel.ObservableCollection<CompressionTaskEntry>(entries);
        SettingsSnapshot = settingsSnapshot;
        ScanRoot = scanRoot;
        PlanningNotes = planningNotes ?? Array.Empty<string>();
        ExecutionPolicy = executionPolicy ?? LongRunningTaskPolicy.ForSettings(settingsSnapshot, plannedEncoders: Entries.Select(entry => entry.Plan.Encoder));
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        QueuePaused = queuePaused;
    }

    public System.Collections.ObjectModel.ObservableCollection<CompressionTaskEntry> Entries { get; }
    public AppSettings SettingsSnapshot { get; }
    public string ScanRoot { get; }
    public IReadOnlyList<string> PlanningNotes { get; }
    public LongRunningTaskPolicy ExecutionPolicy { get; }
    public EncodingPerformanceHistory PerformanceHistory { get; } = new();
    public string SessionId { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool QueuePaused { get; set; }
}
