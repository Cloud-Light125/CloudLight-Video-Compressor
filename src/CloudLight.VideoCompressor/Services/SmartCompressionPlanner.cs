using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Makes a decision for one source file. The planner first answers whether a
/// transcode has enough benefit, then produces the target bitrate and encoder
/// details. BPP/BPPF are supporting signals only; they never replace codec,
/// duration, source bitrate, or the configured profile.
/// </summary>
public sealed class SmartCompressionPlanner
{
    private const double BaselinePixels = 1920d * 1080d;
    private const double MinimumBenefitScore = 20;

    public SmartCompressionDecision CreateDecision(
        VideoFileInfo media,
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null)
    {
        // SmartPreset is the legacy persisted field and is kept synchronized
        // with CompressionProfile. Reading it here also keeps old settings
        // objects (created before the new field existed) deterministic.
        var profile = CompressionProfileCatalog.Get(CompressionProfileCatalog.FromLegacy(settings.SmartPreset));
        var preset = settings.SmartPreset;
        var targetCodec = settings.TargetVideoCodec ?? profile.PreferredCodec;
        var targetDimensions = GetTargetDimensions(media, settings, profile);
        var targetWidth = targetDimensions.Width;
        var targetHeight = targetDimensions.Height;
        var targetFps = GetTargetFps(media, settings, profile);
        var audioBps = GetTargetAudioBitrate(media, settings, profile);
        var sourceVideoBps = GetSourceVideoBitrate(media);
        var sourceCodec = NormalizeCodec(media.VideoCodec);

        var encoderSettings = settings;
        if (settings.EncoderSelection is null)
        {
            encoderSettings = settings.Clone();
            encoderSettings.EncoderSelection = profile.PreferredEncoderPolicy;
        }
        var encoderSelection = EncoderSelectionResolver.Resolve(encoderSettings, targetCodec, capabilities);
        var selectedEncoder = encoderSelection.SelectedEncoder;
        var encoderEfficiency = GetEncoderEfficiency(selectedEncoder, profile);

        var qualityFactor = Math.Clamp(profile.BitrateScale * settings.SmartQualityFactor, 0.35, 2.0);
        var pixelRatio = targetWidth is > 0 && targetHeight is > 0
            ? Math.Max(0.05, targetWidth.Value * (double)targetHeight.Value / BaselinePixels)
            : 1d;
        var pixelFactor = Math.Pow(pixelRatio, 0.65);
        var fpsFactor = targetFps is > 0
            ? Math.Pow(Math.Clamp(targetFps.Value / 30d, 0.5, 4d), 0.55)
            : 1d;
        var codecBaseline = targetCodec switch
        {
            VideoCodecKind.H264 => 8_500_000d,
            VideoCodecKind.Av1 => 5_400_000d,
            _ => 6_300_000d
        };
        var targetVideoBps = (long)Math.Round(codecBaseline * pixelFactor * fpsFactor * qualityFactor * encoderEfficiency);

        var remoteBudgetApplied = false;
        double? bandwidthBudgetBps = null;
        if (profile.BandwidthPolicy == BandwidthPolicy.RespectSafeTotalBudget && settings.RemotePlaybackBandwidthMbps > 0)
        {
            targetVideoBps = ApplyRemoteBudget(targetVideoBps, audioBps, settings, out remoteBudgetApplied, out bandwidthBudgetBps);
        }

        if (settings.SmartMaximumVideoBitrateMbps > 0)
        {
            targetVideoBps = Math.Min(targetVideoBps, ToBitsPerSecond(settings.SmartMaximumVideoBitrateMbps));
        }
        targetVideoBps = Math.Max(100_000, targetVideoBps);
        var maxVideoBps = CalculateMaxVideoBitrate(targetVideoBps, settings, profile, audioBps, bandwidthBudgetBps);

        var sourceSize = Math.Max(0, media.FileSizeBytes);
        var estimatedOutputSize = EstimateOutputSize(media.DurationSeconds, targetVideoBps, audioBps);
        var expectedSavingRatio = sourceSize > 0 && estimatedOutputSize > 0
            ? Math.Clamp(1d - estimatedOutputSize / (double)sourceSize, -10, 1)
            : 0;

        var sourceBpp = sourceVideoBps > 0 && media.PixelCount is > 0
            ? sourceVideoBps / (double)media.PixelCount.Value
            : (double?)null;
        var sourceBppf = sourceVideoBps > 0 && media.PixelCount is > 0 && media.FrameRate is > 0
            ? sourceVideoBps / (media.PixelCount.Value * media.FrameRate.Value)
            : (double?)null;
        var targetBppf = targetWidth is > 0 && targetHeight is > 0 && targetFps is > 0
            ? targetVideoBps / (double)(targetWidth.Value * targetHeight.Value * targetFps.Value)
            : (double?)null;

        var sourceEfficiency = CodecEfficiency(sourceCodec);
        var targetEfficiency = CodecEfficiency(targetCodec);
        var codecUpgradeBenefit = Math.Clamp((targetEfficiency / sourceEfficiency - 1) * 45, -20, 18);
        var excessRatio = targetVideoBps > 0 ? Math.Max(0, sourceVideoBps / (double)targetVideoBps - 1) : 0;
        var savingScore = Math.Clamp(expectedSavingRatio, 0, 1) * 55;
        var excessScore = Math.Clamp(excessRatio / 1.5, 0, 1) * 25;
        var lowBppPenalty = sourceBppf is < 0.035 ? 15 : 0;
        var repeatedLossyPenalty = sourceCodec == SourceCodec.Hevc && targetCodec == VideoCodecKind.H265
            ? 12
            : sourceCodec == SourceCodec.Av1 && targetCodec == VideoCodecKind.Av1
                ? 18
                : 0;
        var compressionBenefitScore = Math.Clamp(
            savingScore + excessScore + codecUpgradeBenefit - lowBppPenalty - repeatedLossyPenalty,
            0,
            100);

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
        else if (expectedSavingRatio < GetMinimumSavingRatio(settings, profile))
        {
            shouldCompress = false;
            reasons.Add($"预计体积仅减少 {expectedSavingRatio:P0}，低于该预设要求的 {GetMinimumSavingRatio(settings, profile):P0} 最低收益");
        }
        else if (compressionBenefitScore < MinimumBenefitScore)
        {
            shouldCompress = false;
            reasons.Add($"综合压缩收益评分仅 {compressionBenefitScore:0}/100，低于安全阈值");
        }

        var sourceDescription = $"{media.Width}×{media.Height} {media.FrameRate:0.#} FPS {SourceCodecDisplay(sourceCodec)} {DisplayFormat.Bitrate(sourceVideoBps)}";
        var budgetDescription = remoteBudgetApplied
            ? $"带宽 {settings.RemotePlaybackBandwidthMbps:0.##} Mbps 经 {settings.RemotePlaybackSafetyRatio:P0} 安全系数和音频/容器预算后"
            : $"按像素数量、FPS、目标编码效率和 {profile.DisplayName} 质量目标计算";
        var reason = shouldCompress
            ? $"当前为 {SourceCodecDisplay(sourceCodec)} {media.Width}×{media.Height} {media.FrameRate:0.#} 视频，视频码率约 {DisplayFormat.Bitrate(sourceVideoBps)}，高于{profile.DisplayName}模式建议范围。转换为 {targetCodec.GetDescription()} 后预计可以明显减小文件体积，同时保持当前分辨率和帧率。"
            : BuildSkipReason(sourceCodec, media, sourceVideoBps, targetVideoBps, profile, reasons.FirstOrDefault());
        var bppDescription = sourceBppf is { } bppf ? $"源 BPPF {bppf:0.####}，目标 BPPF {targetBppf:0.####}" : "BPPF 不可用";
        var explanation = shouldCompress
            ? $"智能决策：建议压缩。\n原因：{reason}\n计划：{targetCodec.GetDescription()} · {EncoderCatalog.Get(selectedEncoder).DisplayName}\n目标平均视频码率：{DisplayFormat.Bitrate(targetVideoBps)}\n最大视频码率：{DisplayFormat.Bitrate(maxVideoBps)}\n目标音频总预算：{DisplayFormat.Bitrate(audioBps)}\n{bppDescription}；预计输出：{DisplayFormat.FileSize(estimatedOutputSize)}，体积减少约 {expectedSavingRatio:P0}。\n计算依据：{sourceDescription}，{budgetDescription}。"
            : $"智能决策：跳过。\n原因：{reason}\n{bppDescription}；预计目标：{targetCodec.GetDescription()} · {DisplayFormat.Bitrate(targetVideoBps)}，预计收益 {expectedSavingRatio:P0}。";

        return new SmartCompressionDecision(
            shouldCompress,
            reason,
            preset,
            targetCodec,
            selectedEncoder,
            targetVideoBps,
            Math.Max(targetVideoBps, maxVideoBps),
            audioBps,
            targetWidth,
            targetHeight,
            targetFps,
            SmartRateControlMode.AverageWithPeak,
            estimatedOutputSize,
            expectedSavingRatio,
            explanation,
            compressionBenefitScore,
            sourceBpp,
            sourceBppf,
            targetBppf,
            bandwidthBudgetBps,
            encoderEfficiency);
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

    private static long GetTargetAudioBitrate(VideoFileInfo media, AppSettings settings, CompressionProfileDefinition profile)
    {
        if (media.AudioTrackCount <= 0)
        {
            return 0;
        }

        var audioMode = profile.AudioPolicy == AudioPolicy.PreferAac ? AudioMode.Aac : settings.AudioMode;
        if (audioMode == AudioMode.Aac)
        {
            return Math.Max(8_000, settings.AudioBitrateKbps * 1_000L) * media.AudioTrackCount;
        }

        return Math.Max(0, media.AudioBitrateBps ?? 0);
    }

    private static double GetMinimumSavingRatio(AppSettings settings, CompressionProfileDefinition profile) =>
        Math.Max(settings.SmartMinimumExpectedSavingRatio, profile.MinimumExpectedSaving);

    private static long ApplyRemoteBudget(
        long calculatedVideoBps,
        long audioBps,
        AppSettings settings,
        out bool applied,
        out double? safeTotalBudget)
    {
        applied = settings.RemotePlaybackBandwidthMbps > 0;
        safeTotalBudget = applied
            ? settings.RemotePlaybackBandwidthMbps * 1_000_000d * settings.RemotePlaybackSafetyRatio
            : null;
        if (!applied || safeTotalBudget is null)
        {
            return calculatedVideoBps;
        }

        var containerAndBurstReserve = Math.Max(64_000d, safeTotalBudget.Value * 0.04);
        var videoBudget = safeTotalBudget.Value - containerAndBurstReserve - audioBps;
        return Math.Max(100_000, Math.Min(calculatedVideoBps, (long)Math.Round(videoBudget)));
    }

    private static long CalculateMaxVideoBitrate(
        long targetVideoBps,
        AppSettings settings,
        CompressionProfileDefinition profile,
        long audioBps,
        double? bandwidthBudgetBps)
    {
        var maximum = profile.BandwidthPolicy == BandwidthPolicy.RespectSafeTotalBudget
            ? Math.Max(targetVideoBps, (long)Math.Round(targetVideoBps * 1.12))
            : Math.Max(targetVideoBps, (long)Math.Round(targetVideoBps * 1.22));
        if (settings.SmartMaximumVideoBitrateMbps > 0)
        {
            maximum = Math.Min(maximum, ToBitsPerSecond(settings.SmartMaximumVideoBitrateMbps));
        }

        if (bandwidthBudgetBps is > 0)
        {
            maximum = Math.Min(maximum, Math.Max(targetVideoBps, (long)Math.Round(bandwidthBudgetBps.Value - audioBps)));
        }

        return Math.Max(targetVideoBps, maximum);
    }

    private static double GetEncoderEfficiency(VideoEncoder encoder, CompressionProfileDefinition profile)
    {
        if (!EncoderCatalog.Get(encoder).IsHardware)
        {
            return 1;
        }

        // Hardware encoders are intentionally given a little more bitrate for
        // the same quality goal. This is a policy input, not a claim that every
        // GPU generation has identical compression efficiency.
        return profile.SpeedVsEfficiencyPreference == SpeedVsEfficiencyPreference.Speed ? 1.16 : 1.08;
    }

    private static double CodecEfficiency(VideoCodecKind codec) => codec switch
    {
        VideoCodecKind.H265 => 1.2,
        VideoCodecKind.Av1 => 1.35,
        _ => 1.0
    };

    private static double CodecEfficiency(SourceCodec codec) => codec switch
    {
        SourceCodec.Hevc => 1.2,
        SourceCodec.Av1 => 1.35,
        _ => 1.0
    };

    private static long EstimateOutputSize(double? durationSeconds, long videoBps, long audioBps)
    {
        if (durationSeconds is not > 0)
        {
            return 0;
        }

        var mediaBytes = (videoBps + audioBps) * durationSeconds.Value / 8d;
        return Math.Max(0, (long)Math.Round(mediaBytes * 1.02));
    }

    private static (int? Width, int? Height) GetTargetDimensions(
        VideoFileInfo media,
        AppSettings settings,
        CompressionProfileDefinition profile)
    {
        if (media.Width is not > 0 || media.Height is not > 0)
        {
            return (media.Width, media.Height);
        }

        if (!profile.AllowResolutionReduction && settings.ResolutionLimit != ResolutionLimitPreset.Keep)
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

    private static double? GetTargetFps(VideoFileInfo media, AppSettings settings, CompressionProfileDefinition profile)
    {
        if (media.FrameRate is not > 0)
        {
            return null;
        }

        if (!profile.AllowFpsReduction && settings.FpsLimit != FpsLimitPreset.Keep)
        {
            return media.FrameRate;
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

    private static string BuildSkipReason(
        SourceCodec sourceCodec,
        VideoFileInfo media,
        long sourceVideoBps,
        long targetVideoBps,
        CompressionProfileDefinition profile,
        string? technicalReason)
    {
        var source = $"当前为 {SourceCodecDisplay(sourceCodec)} {media.Width}×{media.Height} {media.FrameRate:0.#} 视频，视频码率约 {DisplayFormat.Bitrate(sourceVideoBps)}";
        if (sourceCodec == SourceCodec.Av1)
        {
            return $"{source}，已经处于可接受范围，重新编码的收益不足。";
        }

        if (sourceCodec == SourceCodec.Hevc && technicalReason?.Contains("重复", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"{source}，已经是 H.265 且接近{profile.DisplayName}模式建议范围，为避免重复有损转换，本次智能跳过。";
        }

        if (technicalReason?.Contains("缺少", StringComparison.Ordinal) == true)
        {
            return "缺少足够的媒体信息，无法安全估算压缩收益，因此智能跳过。";
        }

        if (technicalReason?.Contains("最低收益", StringComparison.Ordinal) == true)
        {
            return $"{source}，预计节省幅度有限，低于{profile.DisplayName}模式的最低收益要求，因此智能跳过。";
        }

        return $"{source}，已处于{profile.DisplayName}模式建议范围（目标约 {DisplayFormat.Bitrate(targetVideoBps)}），重新编码的收益不足。";
    }

    private enum SourceCodec
    {
        H264,
        Hevc,
        Av1
    }
}
