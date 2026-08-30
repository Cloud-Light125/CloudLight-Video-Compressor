namespace CloudLight.VideoCompressor.Models;

public sealed record EncodingProgress(
    double Percent,
    double? Fps,
    string? Speed,
    long? TotalSizeBytes,
    TimeSpan? Remaining,
    double ProcessedDurationSeconds = 0,
    double? TotalDurationSeconds = null,
    long? Frame = null,
    long? BitrateBps = null,
    TimeSpan? Elapsed = null,
    DateTimeOffset? LastProgressAt = null,
    bool IsEtaStable = false,
    bool IsStalled = false)
{
    public string EtaDisplay => IsEtaStable && Remaining is { } remaining
        ? DisplayFormat.Duration(remaining.TotalSeconds)
        : Percent <= 0 ? "—" : "计算中";

    public CompressionProgress ToCompressionProgress(PipelineStage stage = PipelineStage.Execute) => new(
        stage,
        ProcessedDurationSeconds,
        TotalDurationSeconds,
        Percent,
        Frame,
        Fps,
        Speed,
        BitrateBps,
        Elapsed ?? TimeSpan.Zero,
        Remaining,
        LastProgressAt ?? DateTimeOffset.UtcNow,
        IsEtaStable,
        IsStalled);
}

public sealed record WorkflowProgress(
    VideoTaskStatus Status,
    string Detail,
    EncodingProgress? Encoding = null,
    ConditionEvaluationResult? Condition = null,
    SmartCompressionDecision? SmartDecision = null,
    VideoEncoder? Encoder = null,
    CompressionProgress? ProgressSnapshot = null,
    CompressionFailureKind FailureKind = CompressionFailureKind.None);

public sealed record ScanProgress(int Completed, int Total, string? CurrentPath, bool IsComplete = false);
