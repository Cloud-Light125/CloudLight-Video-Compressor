using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Infrastructure;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Makes a separate decision for every source file. The planner uses a resolution
/// and frame-rate normalized bitrate budget, then refuses to re-encode sources that
/// are already efficient or whose predicted saving is too small.
/// </summary>
public sealed class SmartCompressionPlanner
{
    private const double BaselinePixels = 1920d * 1080d;

    public SmartCompressionDecision CreateDecision(
        VideoFileInfo media,
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null)
    {
        var preset = settings.SmartPreset;
        var targetCodec = settings.TargetVideoCodec ?? VideoCodecKind.H265;
        var (targetWidth, targetHeight) = GetTargetDimensions(media, settings);
        var targetFps = GetTargetFps(media, settings);
        var audioBps = GetTargetAudioBitrate(media, settings);
        var sourceVideoBps = GetSourceVideoBitrate(media);
        var sourceCodec = NormalizeCodec(media.VideoCodec);

        var qualityFactor = GetQualityFactor(settings, preset);
        var minimumSavingRatio = GetMinimumSavingRatio(settings, preset);
        var pixelRatio = targetWidth is > 0 && targetHeight is > 0
            ? Math.Max(0.05, targetWidth.Value * (double)targetHeight.Value / BaselinePixels)
            : 1d;
        var pixelFactor = Math.Pow(pixelRatio, 0.65);
        var fpsFactor = targetFps is > 0
            ? Math.Pow(Math.Clamp(targetFps.Value / 30d, 0.5, 4d), 0.55)
            : 1d;
        var codecBaseline = targetCodec == VideoCodecKind.H264 ? 8_500_000d : 6_300_000d;
        var targetVideoBps = (long)Math.Round(codecBaseline * pixelFactor * fpsFactor * qualityFactor);

        var remoteBudgetApplied = false;
        if (preset == SmartCompressionPreset.RemotePlayback)
        {
            targetVideoBps = ApplyRemoteBudget(targetVideoBps, audioBps, settings, out remoteBudgetApplied);
        }

        if (settings.SmartMaximumVideoBitrateMbps > 0)
        {
            targetVideoBps = Math.Min(targetVideoBps, ToBitsPerSecond(settings.SmartMaximumVideoBitrateMbps));
        }

        var maxVideoBps = CalculateMaxVideoBitrate(targetVideoBps, settings, preset, audioBps);
        var sourceSize = Math.Max(0, media.FileSizeBytes);
        var estimatedOutputSize = EstimateOutputSize(media.DurationSeconds, targetVideoBps, audioBps);
        var expectedSavingRatio = sourceSize > 0
            ? Math.Clamp(1d - estimatedOutputSize / (double)sourceSize, -10, 1)
            : 0;

        var reasons = new List<string>();
        var shouldCompress = true;
        if (media.DurationSeconds is not > 0 || media.Width is not > 0 || media.Height is not > 0 || sourceVideoBps is not > 0)
        {
            shouldCompress = false;
            reasons.Add("缺少时长、分辨率或源视频码率，无法安全估算收益");
        }
        else if (sourceCodec == SourceCodec.Av1 && sourceVideoBps <= targetVideoBps * 1.35)
        {
            shouldCompress = false;
            reasons.Add("源文件已经是 AV1，当前码率处于合理范围，重新编码收益不足");
        }
        else if (sourceCodec == SourceCodec.Hevc && targetCodec == VideoCodecKind.H265 && sourceVideoBps <= targetVideoBps * 1.18)
        {
            shouldCompress = false;
            reasons.Add("源文件已经是 HEVC，当前码率处于合理范围，避免重复有损转码");
        }
        else if (sourceVideoBps <= targetVideoBps * 1.03)
        {
            shouldCompress = false;
            reasons.Add("当前码率已经处于合理目标范围");
        }
        else if (expectedSavingRatio < minimumSavingRatio)
        {
            shouldCompress = false;
            reasons.Add($"预计体积仅减少 {expectedSavingRatio:P0}，低于该预设要求的 {minimumSavingRatio:P0} 最低收益");
        }

        var encoderSettings = settings;
        if (settings.EncoderSelection is null)
        {
            // Smart mode is a new decision layer. Legacy files still keep their
            // explicit encoder in manual modes, while smart mode may use available hardware.
            encoderSettings = settings.Clone();
            encoderSettings.EncoderSelection = EncoderSelectionMode.Automatic;
        }
        var encoderSelection = EncoderSelectionResolver.Resolve(encoderSettings, targetCodec, capabilities);
        var selectedEncoder = encoderSelection.SelectedEncoder;
        var sourceDescription = $"{media.Width}×{media.Height} {media.FrameRate:0.#} FPS {SourceCodecDisplay(sourceCodec)} {DisplayFormat.Bitrate(sourceVideoBps)}";
        var budgetDescription = remoteBudgetApplied
            ? $"带宽 {settings.RemotePlaybackBandwidthMbps:0.##} Mbps 经 {settings.RemotePlaybackSafetyRatio:P0} 安全系数和音频/容器预算后"
            : $"按像素数量、FPS、目标编码效率和{preset.GetDescription()}质量系数计算";
        var reason = shouldCompress ? "源视频码率明显高于当前目标范围" : reasons.FirstOrDefault() ?? "智能判断认为无需压缩";
        var explanation = shouldCompress
            ? $"智能决策：建议压缩。\n原因：{sourceDescription}，{budgetDescription}。\n计划：{targetCodec.GetDescription()} · {EncoderCatalog.Get(selectedEncoder).DisplayName}\n目标平均视频码率：{DisplayFormat.Bitrate(targetVideoBps)}\n最大视频码率：{DisplayFormat.Bitrate(maxVideoBps)}\n目标音频总预算：{DisplayFormat.Bitrate(audioBps)}\n预计输出：{DisplayFormat.FileSize(estimatedOutputSize)}，体积减少约 {expectedSavingRatio:P0}。"
            : $"智能决策：跳过。\n原因：{sourceDescription}，{reason}。\n预计目标：{targetCodec.GetDescription()} · {DisplayFormat.Bitrate(targetVideoBps)}，预计收益 {expectedSavingRatio:P0}。";

        return new SmartCompressionDecision(
            shouldCompress,
            reason,
            preset,
            targetCodec,
            selectedEncoder,
            Math.Max(100_000, targetVideoBps),
            Math.Max(targetVideoBps, maxVideoBps),
            audioBps,
            targetWidth,
            targetHeight,
            targetFps,
            SmartRateControlMode.AverageWithPeak,
            estimatedOutputSize,
            expectedSavingRatio,
            explanation);
    }

    private static long GetSourceVideoBitrate(VideoFileInfo media)
    {
        if (media.VideoBitrateBps is > 0)
        {
            return media.VideoBitrateBps.Value;
        }

        if (media.TotalBitrateBps is > 0)
        {
            var audio = Math.Max(0, media.AudioBitrateBps ?? 0);
            return Math.Max(1, media.TotalBitrateBps.Value - audio);
        }

        return media.DurationSeconds is > 0 && media.FileSizeBytes > 0
            ? Math.Max(1, (long)Math.Round(media.FileSizeBytes * 8d / media.DurationSeconds.Value))
            : 0;
    }

    private static long GetTargetAudioBitrate(VideoFileInfo media, AppSettings settings)
    {
        if (media.AudioTrackCount <= 0)
        {
            return 0;
        }

        if (settings.AudioMode == AudioMode.Aac)
        {
            return Math.Max(8_000, settings.AudioBitrateKbps * 1_000L) * media.AudioTrackCount;
        }

        return Math.Max(0, media.AudioBitrateBps ?? 0);
    }

    private static double GetQualityFactor(AppSettings settings, SmartCompressionPreset preset) =>
        preset switch
        {
            SmartCompressionPreset.HighQuality => 1.12 * settings.SmartQualityFactor,
            SmartCompressionPreset.SpaceSaving => 0.78 * settings.SmartQualityFactor,
            SmartCompressionPreset.RemotePlayback => 1.0 * settings.SmartQualityFactor,
            SmartCompressionPreset.Custom => settings.SmartQualityFactor,
            _ => 1.0 * settings.SmartQualityFactor
        };

    private static double GetMinimumSavingRatio(AppSettings settings, SmartCompressionPreset preset) =>
        preset switch
        {
            SmartCompressionPreset.HighQuality => Math.Max(settings.SmartMinimumExpectedSavingRatio, 0.15),
            SmartCompressionPreset.SpaceSaving => Math.Min(settings.SmartMinimumExpectedSavingRatio, 0.05),
            SmartCompressionPreset.RemotePlayback => Math.Max(settings.SmartMinimumExpectedSavingRatio, 0.08),
            _ => settings.SmartMinimumExpectedSavingRatio
        };

    private static long ApplyRemoteBudget(long calculatedVideoBps, long audioBps, AppSettings settings, out bool applied)
    {
        applied = settings.RemotePlaybackBandwidthMbps > 0;
        if (!applied)
        {
            return calculatedVideoBps;
        }

        var safeTotalBudget = settings.RemotePlaybackBandwidthMbps * 1_000_000d * settings.RemotePlaybackSafetyRatio;
        var containerAndBurstReserve = Math.Max(64_000d, safeTotalBudget * 0.04);
        var videoBudget = safeTotalBudget - containerAndBurstReserve - audioBps;
        return Math.Max(100_000, (long)Math.Round(videoBudget));
    }

    private static long CalculateMaxVideoBitrate(long targetVideoBps, AppSettings settings, SmartCompressionPreset preset, long audioBps)
    {
        var maximum = preset == SmartCompressionPreset.RemotePlayback
            ? Math.Max(targetVideoBps, (long)Math.Round(targetVideoBps * 1.16))
            : Math.Max(targetVideoBps, (long)Math.Round(targetVideoBps * 1.22));
        if (settings.SmartMaximumVideoBitrateMbps > 0)
        {
            maximum = Math.Min(maximum, ToBitsPerSecond(settings.SmartMaximumVideoBitrateMbps));
        }

        if (preset == SmartCompressionPreset.RemotePlayback)
        {
            var safeTotalBudget = settings.RemotePlaybackBandwidthMbps * 1_000_000d * settings.RemotePlaybackSafetyRatio;
            maximum = Math.Min(maximum, Math.Max(targetVideoBps, (long)Math.Round(safeTotalBudget - audioBps)));
        }

        return Math.Max(targetVideoBps, maximum);
    }

    private static long EstimateOutputSize(double? durationSeconds, long videoBps, long audioBps)
    {
        if (durationSeconds is not > 0)
        {
            return 0;
        }

        var mediaBytes = (videoBps + audioBps) * durationSeconds.Value / 8d;
        return Math.Max(0, (long)Math.Round(mediaBytes * 1.02));
    }

    private static (int? Width, int? Height) GetTargetDimensions(VideoFileInfo media, AppSettings settings)
    {
        if (media.Width is not > 0 || media.Height is not > 0)
        {
            return (media.Width, media.Height);
        }

        var limit = settings.ResolutionLimit switch
        {
            ResolutionLimitPreset.UHD4K => (3840, 2160),
            ResolutionLimitPreset.QHD1440p => (2560, 1440),
            ResolutionLimitPreset.FullHd1080p => (1920, 1080),
            ResolutionLimitPreset.Hd720p => (1280, 720),
            ResolutionLimitPreset.Custom => (settings.CustomMaxWidth, settings.CustomMaxHeight),
            _ => (int.MaxValue, int.MaxValue)
        };
        var scale = Math.Min(1d, Math.Min(limit.Item1 / (double)media.Width.Value, limit.Item2 / (double)media.Height.Value));
        return ((int)Math.Max(2, Math.Floor(media.Width.Value * scale / 2) * 2),
            (int)Math.Max(2, Math.Floor(media.Height.Value * scale / 2) * 2));
    }

    private static double? GetTargetFps(VideoFileInfo media, AppSettings settings)
    {
        if (media.FrameRate is not > 0)
        {
            return null;
        }

        var limit = settings.FpsLimit switch
        {
            FpsLimitPreset.Fps120 => 120d,
            FpsLimitPreset.Fps60 => 60d,
            FpsLimitPreset.Fps30 => 30d,
            FpsLimitPreset.Custom => settings.CustomMaxFps,
            _ => double.PositiveInfinity
        };
        return Math.Min(media.FrameRate.Value, limit);
    }

    private static long ToBitsPerSecond(double megabits) =>
        Math.Max(100_000, (long)Math.Round(megabits * 1_000_000d));

    private static SourceCodec NormalizeCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => SourceCodec.Hevc,
        "av1" => SourceCodec.Av1,
        _ => SourceCodec.H264
    };

    private static string SourceCodecDisplay(SourceCodec codec) => codec switch
    {
        SourceCodec.Hevc => "HEVC",
        SourceCodec.Av1 => "AV1",
        _ => "H.264"
    };

    private enum SourceCodec
    {
        H264,
        Hevc,
        Av1
    }
}
