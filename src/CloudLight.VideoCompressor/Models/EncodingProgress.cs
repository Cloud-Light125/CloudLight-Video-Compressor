namespace CloudLight.VideoCompressor.Models;

public sealed record EncodingProgress(
    double Percent,
    double? Fps,
    string? Speed,
    long? TotalSizeBytes,
    TimeSpan? Remaining);

public sealed record WorkflowProgress(
    VideoTaskStatus Status,
    string Detail,
    EncodingProgress? Encoding = null,
    ConditionEvaluationResult? Condition = null,
    SmartCompressionDecision? SmartDecision = null,
    VideoEncoder? Encoder = null);

public sealed record ScanProgress(int Completed, int Total, string? CurrentPath, bool IsComplete = false);
