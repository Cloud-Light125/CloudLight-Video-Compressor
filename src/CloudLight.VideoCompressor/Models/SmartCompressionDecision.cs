namespace CloudLight.VideoCompressor.Models;

/// <summary>
/// Per-file output decision produced by SmartCompressionPlanner.
/// Bitrates are bits per second; TargetAudioBitrateBps is the total budget for all
/// audio tracks, while FFmpeg applies the corresponding per-track value when needed.
/// </summary>
public sealed record SmartCompressionDecision(
    bool ShouldCompress,
    string Reason,
    SmartCompressionPreset Preset,
    VideoCodecKind TargetCodec,
    VideoEncoder SelectedEncoder,
    long TargetVideoBitrateBps,
    long MaxVideoBitrateBps,
    long TargetAudioBitrateBps,
    int? TargetWidth,
    int? TargetHeight,
    double? TargetFps,
    SmartRateControlMode RateControlMode,
    long EstimatedOutputSizeBytes,
    double ExpectedSavingRatio,
    string Explanation)
{
    public string DecisionDisplay => ShouldCompress ? "建议压缩" : "智能跳过";
}
