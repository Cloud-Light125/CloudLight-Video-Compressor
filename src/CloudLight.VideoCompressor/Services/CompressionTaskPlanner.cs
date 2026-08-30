using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Builds the immutable preview snapshot consumed by the compression task page.
/// Every decision that affects FFmpeg arguments is made here before the task
/// window is shown.
/// </summary>
public sealed class CompressionTaskPlanner
{
    private readonly RuleEngine _ruleEngine;
    private readonly FFprobeService _ffprobeService;
    private readonly CompressionPlanner _compressionPlanner;
    private readonly TargetSizeCalculator _targetSizeCalculator;
    private readonly OutputPathService _outputPathService;

    public CompressionTaskPlanner(
        RuleEngine ruleEngine,
        FFprobeService ffprobeService,
        CompressionPlanner compressionPlanner,
        TargetSizeCalculator targetSizeCalculator,
        OutputPathService outputPathService)
    {
        _ruleEngine = ruleEngine;
        _ffprobeService = ffprobeService;
        _compressionPlanner = compressionPlanner;
        _targetSizeCalculator = targetSizeCalculator;
        _outputPathService = outputPathService;
    }

    public async Task<CompressionTaskSession> CreateSessionAsync(
        IEnumerable<VideoTaskItem> candidates,
        AppSettings settings,
        string scanRoot,
        FFmpegTools tools,
        EncoderCapabilitySet capabilities,
        CancellationToken cancellationToken)
    {
        var settingsSnapshot = settings.Clone();
        var entries = new List<CompressionTaskEntry>();
        var planningNotes = new List<string>();
        var reservedPreviewPaths = new List<string>();

        foreach (var item in candidates.Where(candidate => candidate.IsSelected && candidate.ConditionResult.IsMatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = item.Media;
            if (!source.HasProbeData)
            {
                source = await _ffprobeService.ProbeAsync(tools, source.FullPath, cancellationToken).ConfigureAwait(false);
            }

            var condition = _ruleEngine.Evaluate(source, settingsSnapshot.Rules);
            if (!condition.IsMatch)
            {
                planningNotes.Add($"{source.FileName}：重新判断后不符合当前条件。");
                continue;
            }

            TargetSizeCalculation? targetSize = null;
            if (settingsSnapshot.CompressionMode == CompressionMode.TargetSize)
            {
                targetSize = _targetSizeCalculator.Calculate(
                    settingsSnapshot.TargetSize,
                    source,
                    settingsSnapshot.AudioMode,
                    settingsSnapshot.AudioBitrateKbps);
                if (!targetSize.IsValid)
                {
                    planningNotes.Add($"{source.FileName}：{targetSize.Error}");
                    continue;
                }

                if (targetSize.IsLargerThanSource)
                {
                    planningNotes.Add($"{source.FileName}：目标大小不小于源文件，已从计划中排除。");
                    continue;
                }
            }

            var plan = _compressionPlanner.CreatePlan(source, settingsSnapshot, targetSize, capabilities);
            if (plan.SmartDecision is { ShouldCompress: false } decision)
            {
                planningNotes.Add($"{source.FileName}：智能跳过：{decision.Reason}");
                continue;
            }

            var targetPath = _outputPathService.GetOutputPath(source, settingsSnapshot, scanRoot, reservedPreviewPaths);
            reservedPreviewPaths.Add(targetPath);
            var estimate = EstimateOutput(source, plan, targetSize);
            var targetCodec = plan.SmartDecision?.TargetCodec
                ?? settingsSnapshot.TargetVideoCodec
                ?? CodecFromEncoder(plan.Encoder);
            var reason = plan.SmartDecision?.Reason ?? "符合当前压缩条件，按已设置的压缩参数执行。";
            plan = plan with
            {
                SourcePath = source.FullPath,
                TargetPath = targetPath,
                TargetCodec = targetCodec,
                RateControl = BuildRateControl(plan, targetSize),
                EstimatedOutputSizeBytes = estimate.ExactBytes,
                EstimatedOutputLowerBoundBytes = estimate.LowerBoundBytes,
                EstimatedOutputUpperBoundBytes = estimate.UpperBoundBytes,
                TargetSizeBytes = targetSize?.TargetSizeBytes,
                Reason = reason
            };

            var comparison = BuildComparison(source, plan);
            entries.Add(new CompressionTaskEntry(source, plan, condition, comparison));
        }

        return new CompressionTaskSession(entries, settingsSnapshot, scanRoot, planningNotes);
    }

    private static (long? ExactBytes, long? LowerBoundBytes, long? UpperBoundBytes) EstimateOutput(
        VideoFileInfo source,
        CompressionPlan plan,
        TargetSizeCalculation? targetSize)
    {
        if (targetSize is { IsValid: true })
        {
            return (targetSize.TargetSizeBytes, targetSize.TargetSizeBytes, targetSize.TargetSizeBytes);
        }

        // A quality target has no reliable pre-encode size. Leaving it unknown
        // is deliberate: the task page must not present a made-up exact number.
        if (plan.Mode == CompressionMode.Crf || source.DurationSeconds is not > 0 || plan.TargetVideoBitrateBps is not > 0)
        {
            return (null, null, null);
        }

        var audioBps = source.AudioTrackCount <= 0
            ? 0
            : plan.AudioMode == AudioMode.Aac
                ? plan.AudioBitrateKbps * 1_000L * source.AudioTrackCount
                : Math.Max(0, source.AudioBitrateBps ?? 0);
        var baseline = Math.Max(0, (plan.TargetVideoBitrateBps!.Value + audioBps) * source.DurationSeconds.Value / 8d * 1.02);
        var lowerFactor = plan.Mode == CompressionMode.SmartAutomatic ? 0.75 : 0.90;
        var upperFactor = plan.Mode == CompressionMode.SmartAutomatic ? 1.25 : 1.10;
        return (null, Math.Max(0, (long)Math.Round(baseline * lowerFactor)), Math.Max(0, (long)Math.Round(baseline * upperFactor)));
    }

    private static string BuildRateControl(CompressionPlan plan, TargetSizeCalculation? targetSize)
    {
        if (targetSize is { IsValid: true })
        {
            return plan.IsTwoPass
                ? $"目标大小 · {DisplayFormat.FileSize(targetSize.TargetSizeBytes)} · Two-pass"
                : $"目标大小 · {DisplayFormat.FileSize(targetSize.TargetSizeBytes)} · 平均码率 + 峰值约束";
        }

        return plan.RateControlDisplay;
    }

    private static CompressionPlanComparison BuildComparison(VideoFileInfo source, CompressionPlan plan)
    {
        var sourceCodec = CodecDisplay(source.VideoCodec);
        var targetCodec = CodecDisplay(plan.EffectiveTargetCodec);
        var sourceResolution = source.Width is { } width && source.Height is { } height
            ? $"{width} × {height}"
            : "未知";
        var targetResolution = GetTargetResolution(source, plan);
        var sourceFps = source.FrameRate is { } fps ? $"{fps:0.###} FPS" : "未知";
        var targetFps = GetTargetFps(source, plan);
        var sourceVideoBitrate = DisplayFormat.Bitrate(source.VideoBitrateBps);
        var targetVideoBitrate = plan.TargetVideoBitrateBps is { } targetBps
            ? DisplayFormat.Bitrate(targetBps)
            : $"质量模式（{plan.RateControlDisplay}）";
        var sourceAudioCodec = source.AudioTrackCount == 0 ? "无" : source.AudioCodec ?? "未知";
        var targetAudioCodec = plan.AudioMode == AudioMode.Copy
            ? source.AudioTrackCount == 0 ? "无" : $"复制（{sourceAudioCodec}）"
            : "AAC";
        var sourceAudioBitrate = DisplayFormat.Bitrate(source.AudioBitrateBps);
        var targetAudioBitrate = plan.AudioMode == AudioMode.Copy
            ? source.AudioTrackCount == 0 ? "无" : "复制"
            : $"{plan.AudioBitrateKbps} Kbps";
        var targetTotalBitrate = plan.TargetVideoBitrateBps is { } videoBps
            ? DisplayFormat.Bitrate(videoBps + GetPlannedAudioBitrate(source, plan))
            : "质量模式（无法预知）";
        var targetEncoder = CompressionPlan.FfmpegEncoderName(plan.Encoder);

        var changes = new List<CompressionParameterChange>
        {
            Change("容器", NormalizeContainer(source.Extension), NormalizeContainer(plan.OutputExtension),
                NormalizeContainer(source.Extension) == NormalizeContainer(plan.OutputExtension)
                    ? CompressionParameterChangeType.Unchanged
                    : CompressionParameterChangeType.Converted),
            Change("视频编码", sourceCodec, targetCodec, sourceCodec == targetCodec
                ? CompressionParameterChangeType.Unchanged
                : CompressionParameterChangeType.Converted),
            Change("视频编码器", source.VideoCodec ?? "未知", targetEncoder, CompressionParameterChangeType.Converted),
            Change("分辨率", sourceResolution, targetResolution, sourceResolution == targetResolution
                ? CompressionParameterChangeType.Unchanged
                : CompressionParameterChangeType.Reduced),
            Change("FPS", sourceFps, targetFps, sourceFps == targetFps
                ? CompressionParameterChangeType.Unchanged
                : CompressionParameterChangeType.Reduced),
            Change("视频码率", sourceVideoBitrate, targetVideoBitrate, GetNumericChange(source.VideoBitrateBps, plan.TargetVideoBitrateBps)),
            Change("总码率", DisplayFormat.Bitrate(source.TotalBitrateBps), targetTotalBitrate,
                plan.TargetVideoBitrateBps is null ? CompressionParameterChangeType.Changed : GetNumericChange(source.TotalBitrateBps, ParseBitrate(targetTotalBitrate))),
            Change("码率控制", "源文件", plan.RateControlDisplay, CompressionParameterChangeType.Changed),
            Change("音频编码", sourceAudioCodec, targetAudioCodec, plan.AudioMode == AudioMode.Copy
                ? CompressionParameterChangeType.Copied
                : sourceAudioCodec.Equals("AAC", StringComparison.OrdinalIgnoreCase)
                    ? CompressionParameterChangeType.Unchanged
                    : CompressionParameterChangeType.Converted),
            Change("音频码率", sourceAudioBitrate, targetAudioBitrate, plan.AudioMode == AudioMode.Copy
                ? CompressionParameterChangeType.Copied
                : GetNumericChange(source.AudioBitrateBps, plan.AudioBitrateKbps * 1_000L)),
            Change("音轨数量", source.AudioTrackCount.ToString(), source.AudioTrackCount.ToString(), CompressionParameterChangeType.Unchanged),
            Change("硬件 / 软件", "源文件", EncoderCatalog.Get(plan.Encoder).IsHardware ? "GPU 硬件编码" : "CPU 软件编码", CompressionParameterChangeType.Converted),
            Change("文件大小", DisplayFormat.FileSize(source.FileSizeBytes), plan.EstimatedOutputDisplay, CompressionParameterChangeType.Changed)
        };

        if (plan.MaxVideoBitrateBps is > 0)
        {
            changes.Add(Change("目标最大码率", "未设置", DisplayFormat.Bitrate(plan.MaxVideoBitrateBps), CompressionParameterChangeType.Changed));
        }
        if (plan.BufferSizeBps is > 0)
        {
            changes.Add(Change("VBV / Buffer", "未设置", DisplayFormat.Bitrate(plan.BufferSizeBps), CompressionParameterChangeType.Changed));
        }

        return new CompressionPlanComparison(changes);
    }

    private static CompressionParameterChange Change(
        string parameter,
        string oldValue,
        string newValue,
        CompressionParameterChangeType type) =>
        new(parameter, oldValue, newValue, type);

    private static CompressionParameterChangeType GetNumericChange(long? oldValue, long? newValue)
    {
        if (oldValue is null || newValue is null)
        {
            return CompressionParameterChangeType.Changed;
        }

        if (oldValue == newValue)
        {
            return CompressionParameterChangeType.Unchanged;
        }

        return newValue < oldValue
            ? CompressionParameterChangeType.Reduced
            : CompressionParameterChangeType.Increased;
    }

    private static long? ParseBitrate(string display)
    {
        var marker = display.IndexOf(" Mbps", StringComparison.Ordinal);
        if (marker <= 0 || !double.TryParse(display[..marker], out var mbps))
        {
            return null;
        }

        return (long)Math.Round(mbps * 1_000_000d);
    }

    private static long GetPlannedAudioBitrate(VideoFileInfo source, CompressionPlan plan) =>
        source.AudioTrackCount <= 0
            ? 0
            : plan.AudioMode == AudioMode.Aac
                ? plan.AudioBitrateKbps * 1_000L * source.AudioTrackCount
                : Math.Max(0, source.AudioBitrateBps ?? 0);

    private static string GetTargetResolution(VideoFileInfo source, CompressionPlan plan)
    {
        if (source.Width is not { } width || source.Height is not { } height)
        {
            return "未知";
        }
        if (plan.ResolutionLimit is not { } limit)
        {
            return $"{width} × {height}";
        }

        var scale = Math.Min(1d, Math.Min(limit.Width / (double)width, limit.Height / (double)height));
        var targetWidth = (int)Math.Max(2, Math.Floor(width * scale / 2) * 2);
        var targetHeight = (int)Math.Max(2, Math.Floor(height * scale / 2) * 2);
        return $"{targetWidth} × {targetHeight}";
    }

    private static string GetTargetFps(VideoFileInfo source, CompressionPlan plan)
    {
        if (source.FrameRate is not { } fps)
        {
            return "未知";
        }

        return plan.FpsLimit is { } limit && limit < fps
            ? $"{limit:0.###} FPS"
            : $"{fps:0.###} FPS";
    }

    private static string CodecDisplay(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "h264" or "avc" => "H.264",
        "hevc" or "h265" or "x265" => "H.265",
        "av1" => "AV1",
        _ => string.IsNullOrWhiteSpace(codec) ? "未知" : codec
    };

    private static string CodecDisplay(VideoCodecKind codec) => codec == VideoCodecKind.H264 ? "H.264" : "H.265";

    private static string NormalizeContainer(string extension) => extension.Trim().TrimStart('.').ToUpperInvariant();

    private static VideoCodecKind CodecFromEncoder(VideoEncoder encoder) => EncoderCatalog.Get(encoder).Codec;
}
