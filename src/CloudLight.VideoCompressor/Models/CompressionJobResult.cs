namespace CloudLight.VideoCompressor.Models;

public sealed record CompressionJobResult(
    VideoTaskStatus Status,
    string Message,
    string? OutputPath = null,
    VideoFileInfo? SourceInfo = null,
    VideoFileInfo? OutputInfo = null,
    ConditionEvaluationResult? Condition = null,
    SmartCompressionDecision? SmartDecision = null,
    VideoEncoder? Encoder = null,
    string? FallbackReason = null);
