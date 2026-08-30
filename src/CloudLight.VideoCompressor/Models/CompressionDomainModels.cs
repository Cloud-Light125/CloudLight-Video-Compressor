using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Models;

/// <summary>
/// A named product goal. A profile is more than a bitrate lookup: it carries
/// the constraints that the planner must respect when choosing a plan.
/// </summary>
public sealed record CompressionProfileDefinition(
    CompressionProfile Profile,
    string DisplayName,
    double TargetQualityLevel,
    VideoCodecKind PreferredCodec,
    EncoderSelectionMode PreferredEncoderPolicy,
    double BitrateScale,
    double MinimumExpectedSaving,
    bool AllowResolutionReduction,
    bool AllowFpsReduction,
    AudioPolicy AudioPolicy,
    BandwidthPolicy BandwidthPolicy,
    SpeedVsEfficiencyPreference SpeedVsEfficiencyPreference);

public static class CompressionProfileCatalog
{
    public static IReadOnlyList<CompressionProfileDefinition> Definitions { get; } =
    [
        new(CompressionProfile.HighQuality, "高质量", 96.5, VideoCodecKind.H265,
            EncoderSelectionMode.CpuSoftware, 1.12, 0.15, false, false,
            AudioPolicy.FollowSettings, BandwidthPolicy.None, SpeedVsEfficiencyPreference.Efficiency),
        new(CompressionProfile.Balanced, "平衡", 95, VideoCodecKind.H265,
            EncoderSelectionMode.Automatic, 1.0, 0.08, false, false,
            AudioPolicy.FollowSettings, BandwidthPolicy.None, SpeedVsEfficiencyPreference.Balanced),
        new(CompressionProfile.SpaceSaving, "节省空间", 93, VideoCodecKind.H265,
            EncoderSelectionMode.Automatic, 0.78, 0.05, true, true,
            AudioPolicy.PreferAac, BandwidthPolicy.None, SpeedVsEfficiencyPreference.Efficiency),
        new(CompressionProfile.RemotePlayback, "远程播放", 94, VideoCodecKind.H265,
            EncoderSelectionMode.HardwareAutomatic, 1.0, 0.08, true, true,
            AudioPolicy.PreferAac, BandwidthPolicy.RespectSafeTotalBudget, SpeedVsEfficiencyPreference.Balanced),
        new(CompressionProfile.Custom, "自定义", 95, VideoCodecKind.H265,
            EncoderSelectionMode.Automatic, 1.0, 0, false, false,
            AudioPolicy.FollowSettings, BandwidthPolicy.None, SpeedVsEfficiencyPreference.Balanced)
    ];

    public static CompressionProfileDefinition Get(CompressionProfile profile) =>
        Definitions.First(definition => definition.Profile == profile);

    public static CompressionProfile FromLegacy(SmartCompressionPreset preset) => (CompressionProfile)preset;

    public static SmartCompressionPreset ToLegacy(CompressionProfile profile) => (SmartCompressionPreset)profile;
}

/// <summary>
/// User intent for one source file. It is created before planning and remains
/// stable while a preview is displayed. The executable plan is kept separate
/// and is attached only after planning has completed.
/// </summary>
public sealed record CompressionJob(
    string JobId,
    VideoFileInfo SourceFile,
    AppSettings UserSettings,
    ConditionEvaluationResult Eligibility,
    string? ScanRoot,
    DateTimeOffset CreatedAt)
{
    public CompressionPlan? Plan { get; init; }

    public IReadOnlyList<CompressionRule> Rules => UserSettings.Rules;
    public VideoCodecKind RequestedCodec => UserSettings.SelectedVideoCodec;
    public EncoderSelectionMode RequestedEncoderMode => UserSettings.SelectedEncoderSelection;
    public OutputLocationMode OutputLocation => UserSettings.OutputLocation;
    public OriginalFileAction OriginalFileAction => UserSettings.OriginalFileAction;
    public string SourcePath => SourceFile.FullPath;

    public CompressionJob WithPlan(CompressionPlan plan) => this with { Plan = plan };

    public static CompressionJob Create(
        VideoFileInfo source,
        AppSettings settings,
        ConditionEvaluationResult eligibility,
        string? scanRoot) => new(
            Guid.NewGuid().ToString("N"),
            source,
            settings.Clone(),
            eligibility,
            scanRoot,
            DateTimeOffset.UtcNow);
}

/// <summary>
/// Every concrete encoder invocation is retained, including failed or stalled
/// attempts that preceded a safe fallback.
/// </summary>
public sealed record CompressionAttempt(
    int AttemptNumber,
    VideoEncoder Encoder,
    CompressionAttemptStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    CompressionFailureKind FailureKind = CompressionFailureKind.None,
    string? Message = null)
{
    public EncoderImplementation Implementation => (EncoderImplementation)Encoder;
    public EncoderType Type => EncoderCatalog.Get(Encoder).IsHardware ? EncoderType.Hardware : EncoderType.Software;
    public TimeSpan? Duration => CompletedAt is { } completed ? completed - StartedAt : null;
    public bool IsFallback => AttemptNumber > 1;
}

/// <summary>
/// Full progress state used by the queue. EncodingProgress remains as a
/// compact compatibility shape for existing bindings and integrations.
/// </summary>
public sealed record CompressionProgress(
    PipelineStage Stage,
    double ProcessedDurationSeconds,
    double? TotalDurationSeconds,
    double Percent,
    long? Frame,
    double? Fps,
    string? Speed,
    long? BitrateBps,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset LastProgressAt,
    bool IsEtaStable,
    bool IsStalled = false)
{
    public string EtaDisplay => IsEtaStable && EstimatedRemaining is { } remaining
        ? DisplayFormat.Duration(remaining.TotalSeconds)
        : Percent <= 0 ? "—" : "计算中";
}

public sealed record VmafSample(double StartSeconds, double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

public sealed record VmafMeasurement(
    VmafSample Sample,
    VideoEncoder Encoder,
    double Quality,
    double Score,
    long? EncodedBytes = null);

public sealed record VmafCalibrationResult(
    bool IsAvailable,
    string Message,
    IReadOnlyList<VmafSample> Samples,
    IReadOnlyList<VmafMeasurement> Measurements,
    double? SelectedQuality,
    long? SelectedBitrateBps,
    DateTimeOffset CompletedAt)
{
    public static VmafCalibrationResult Unavailable(string message) => new(
        false,
        message,
        [],
        [],
        null,
        null,
        DateTimeOffset.UtcNow);
}
