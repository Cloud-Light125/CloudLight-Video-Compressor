using System.Globalization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class CompressionPlanner
{
    private readonly SmartCompressionPlanner _smartCompressionPlanner;

    public CompressionPlanner(SmartCompressionPlanner? smartCompressionPlanner = null) =>
        _smartCompressionPlanner = smartCompressionPlanner ?? new SmartCompressionPlanner();

    public CompressionPlan CreatePlan(
        VideoFileInfo media,
        AppSettings settings,
        TargetSizeCalculation? targetSizeCalculation = null,
        EncoderCapabilitySet? capabilities = null)
    {
        var warnings = new List<string>();
        var targetCodec = settings.TargetVideoCodec ?? LegacyCodec(settings.VideoEncoder);
        SmartCompressionDecision? smartDecision = null;
        EncoderSelectionResult selection;

        if (settings.CompressionMode == CompressionMode.SmartAutomatic)
        {
            smartDecision = _smartCompressionPlanner.CreateDecision(media, settings, capabilities);
            var smartSettings = settings;
            if (settings.EncoderSelection is null)
            {
                smartSettings = settings.Clone();
                smartSettings.EncoderSelection = EncoderSelectionMode.Automatic;
            }

            selection = EncoderSelectionResolver.Resolve(smartSettings, smartDecision.TargetCodec, capabilities);
            smartDecision = smartDecision with { SelectedEncoder = selection.SelectedEncoder };
            targetCodec = smartDecision.TargetCodec;
        }
        else
        {
            selection = EncoderSelectionResolver.Resolve(settings, targetCodec, capabilities, preferHardwareForAutomatic: false);
        }

        warnings.AddRange(selection.Warnings);
        var effectiveEncoder = selection.SelectedEncoder;

        var videoBitrate = settings.CompressionMode switch
        {
            CompressionMode.Bitrate => ToBitsPerSecond(settings.TargetVideoBitrateMbps),
            CompressionMode.TargetSize when targetSizeCalculation is { IsValid: true } => targetSizeCalculation.TargetVideoBitrateBps,
            CompressionMode.SmartAutomatic when smartDecision is not null => smartDecision.TargetVideoBitrateBps,
            _ => (long?)null
        };
        if (settings.CompressionMode == CompressionMode.TargetSize && targetSizeCalculation is not { IsValid: true })
        {
            throw new InvalidOperationException(targetSizeCalculation?.Error ?? "目标大小计算失败。");
        }

        var isTwoPass = settings.CompressionMode == CompressionMode.TargetSize && SupportsTwoPass(effectiveEncoder);
        if (settings.CompressionMode == CompressionMode.TargetSize && !isTwoPass && IsHardwareEncoder(effectiveEncoder))
        {
            warnings.Add($"目标大小模式：{EncoderCatalog.Get(effectiveEncoder).DisplayName} 使用平均码率 + 峰值约束的单遍模式；硬件编码器不强制套用 libx264 两遍编码。");
        }

        long? maxVideoBitrate = settings.CompressionMode == CompressionMode.SmartAutomatic
            ? smartDecision?.MaxVideoBitrateBps
            : settings.CompressionMode == CompressionMode.TargetSize && !isTwoPass && videoBitrate is > 0
                ? (long)Math.Round(videoBitrate.Value * 1.05)
                : null;
        long? bufferSize = maxVideoBitrate is > 0
            ? Math.Max(maxVideoBitrate.Value * 2, videoBitrate ?? maxVideoBitrate.Value)
            : null;

        var resolutionLimit = GetResolutionLimit(settings);
        var fpsLimit = GetFpsLimit(settings);
        if (fpsLimit is not null && media.FrameRate is null)
        {
            warnings.Add("无法读取源 FPS，本次不会强行设置 FPS，以避免错误提高帧率。");
            fpsLimit = null;
        }
        else if (fpsLimit is not null && media.FrameRate is not null && media.FrameRate <= fpsLimit)
        {
            fpsLimit = null;
        }

        var outputExtension = media.Extension.ToLowerInvariant();
        if (outputExtension == ".mp4" && media.SubtitleCodecs.Count > 0)
        {
            warnings.Add("MP4 容器不支持部分字幕格式。本次会尝试复制字幕；若不兼容，FFmpeg 将安全失败且不会影响源文件。");
        }
        if (outputExtension is ".webm" or ".avi")
        {
            warnings.Add($"{outputExtension} 容器与 H.264/H.265 兼容性有限；建议输出为 MP4 或 MKV。若 FFmpeg 拒绝该组合，源文件不会被修改。");
        }

        return new CompressionPlan(
            isTwoPass,
            effectiveEncoder,
            settings.CompressionMode,
            settings.Crf,
            settings.EncodingPreset,
            videoBitrate,
            resolutionLimit,
            fpsLimit,
            settings.AudioMode,
            settings.AudioBitrateKbps,
            outputExtension,
            warnings,
            maxVideoBitrate,
            bufferSize,
            smartDecision,
            selection.FallbackEncoders,
            targetCodec)
        {
            InputInfo = media,
            CompressionBenefitScore = smartDecision?.CompressionBenefitScore ?? 0,
            SourceBitsPerPixel = smartDecision?.SourceBitsPerPixel,
            SourceBitsPerPixelPerFrame = smartDecision?.SourceBitsPerPixelPerFrame,
            TargetBitsPerPixelPerFrame = smartDecision?.TargetBitsPerPixelPerFrame
        };
    }

    public SmartCompressionDecision CreateSmartDecision(
        VideoFileInfo media,
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null) =>
        _smartCompressionPlanner.CreateDecision(media, settings, capabilities);

    private static (int Width, int Height)? GetResolutionLimit(AppSettings settings) => settings.ResolutionLimit switch
    {
        ResolutionLimitPreset.UHD4K => (3840, 2160),
        ResolutionLimitPreset.QHD1440p => (2560, 1440),
        ResolutionLimitPreset.FullHd1080p => (1920, 1080),
        ResolutionLimitPreset.Hd720p => (1280, 720),
        ResolutionLimitPreset.Custom => (settings.CustomMaxWidth, settings.CustomMaxHeight),
        _ => null
    };

    private static double? GetFpsLimit(AppSettings settings) => settings.FpsLimit switch
    {
        FpsLimitPreset.Fps120 => 120,
        FpsLimitPreset.Fps60 => 60,
        FpsLimitPreset.Fps30 => 30,
        FpsLimitPreset.Custom => settings.CustomMaxFps,
        _ => null
    };

    public static bool RequiresSourceProbe(AppSettings settings) =>
        settings.CompressionMode is CompressionMode.TargetSize or CompressionMode.SmartAutomatic ||
        settings.FpsLimit != FpsLimitPreset.Keep;

    private static VideoCodecKind LegacyCodec(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Libx264 or VideoEncoder.H264Nvenc or VideoEncoder.H264Qsv or VideoEncoder.H264Amf => VideoCodecKind.H264,
        VideoEncoder.LibsvtAv1 => VideoCodecKind.Av1,
        _ => VideoCodecKind.H265
    };

    private static bool IsHardwareEncoder(VideoEncoder encoder) => EncoderCatalog.Get(encoder).IsHardware;

    private static bool SupportsTwoPass(VideoEncoder encoder) =>
        encoder is VideoEncoder.Libx264 or VideoEncoder.Libx265 or VideoEncoder.LibsvtAv1;

    private static long ToBitsPerSecond(double megabits) =>
        Math.Max(10_000, (long)Math.Round(megabits * 1_000_000d));
}

public sealed record CompressionPlan(
    bool IsTwoPass,
    VideoEncoder Encoder,
    CompressionMode Mode,
    double Crf,
    string EncodingPreset,
    long? TargetVideoBitrateBps,
    (int Width, int Height)? ResolutionLimit,
    double? FpsLimit,
    AudioMode AudioMode,
    int AudioBitrateKbps,
    string OutputExtension,
    IReadOnlyList<string> Warnings,
    long? MaxVideoBitrateBps = null,
    long? BufferSizeBps = null,
    SmartCompressionDecision? SmartDecision = null,
    IReadOnlyList<VideoEncoder>? FallbackEncoders = null,
    VideoCodecKind? TargetCodec = null,
    string? SourcePath = null,
    string? TargetPath = null,
    string? RateControl = null,
    long? EstimatedOutputSizeBytes = null,
    long? EstimatedOutputLowerBoundBytes = null,
    long? EstimatedOutputUpperBoundBytes = null,
    long? TargetSizeBytes = null,
    string? Reason = null)
{
    /// <summary>
    /// Stable identity for the dry-run snapshot. Fallbacks use <c>with</c> and
    /// therefore retain this value; the execute stage never silently creates a
    /// second plan.
    /// </summary>
    public Guid PlanId { get; init; } = Guid.NewGuid();

    public VideoFileInfo? InputInfo { get; init; }
    public VmafCalibrationResult? QualityCalibration { get; init; }
    public double CompressionBenefitScore { get; init; }
    public double? SourceBitsPerPixel { get; init; }
    public double? SourceBitsPerPixelPerFrame { get; init; }
    public double? TargetBitsPerPixelPerFrame { get; init; }

    public IReadOnlyList<VideoEncoder> EncoderCandidates =>
        [Encoder, .. (FallbackEncoders ?? Array.Empty<VideoEncoder>()).Where(candidate => candidate != Encoder).Take(3)];

    public CompressionPlan WithEncoder(VideoEncoder encoder) =>
        this with
        {
            Encoder = encoder,
            IsTwoPass = Mode == CompressionMode.TargetSize && SupportsTwoPass(encoder),
            MaxVideoBitrateBps = Mode == CompressionMode.TargetSize && !SupportsTwoPass(encoder) && TargetVideoBitrateBps is > 0
                ? (long)Math.Round(TargetVideoBitrateBps.Value * 1.05)
                : Mode == CompressionMode.TargetSize
                    ? null
                    : MaxVideoBitrateBps,
            BufferSizeBps = Mode == CompressionMode.TargetSize && !SupportsTwoPass(encoder) && TargetVideoBitrateBps is > 0
                ? (long)Math.Round(TargetVideoBitrateBps.Value * 1.05) * 2
                : Mode == CompressionMode.TargetSize
                    ? null
                    : BufferSizeBps,
            SmartDecision = SmartDecision is null
                ? null
                : SmartDecision with { SelectedEncoder = encoder }
        };

    private static bool SupportsTwoPass(VideoEncoder encoder) =>
        encoder is VideoEncoder.Libx264 or VideoEncoder.Libx265 or VideoEncoder.LibsvtAv1;

    public VideoCodecKind EffectiveTargetCodec =>
        TargetCodec ?? EncoderCatalog.Get(Encoder).Codec;

    public VideoCodec Codec => EffectiveTargetCodec switch
    {
        VideoCodecKind.H264 => VideoCodec.H264,
        VideoCodecKind.Av1 => VideoCodec.Av1,
        _ => VideoCodec.Hevc
    };

    public EncoderImplementation EncoderImplementation => (EncoderImplementation)Encoder;

    public EncoderType EncoderType => EncoderCatalog.Get(Encoder).IsHardware
        ? EncoderType.Hardware
        : EncoderType.Software;

    public RateControlMode EffectiveRateControlMode => Mode switch
    {
        CompressionMode.Crf when Encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc => RateControlMode.ConstantQuality,
        CompressionMode.Crf when Encoder is VideoEncoder.H264Qsv or VideoEncoder.HevcQsv => RateControlMode.ConstantQuality,
        CompressionMode.Crf when Encoder is VideoEncoder.H264Amf or VideoEncoder.HevcAmf => RateControlMode.ConstantQuantizer,
        CompressionMode.Crf => RateControlMode.ConstantRateFactor,
        CompressionMode.TargetSize when IsTwoPass => RateControlMode.TargetSizeTwoPass,
        CompressionMode.TargetSize => RateControlMode.VariableBitrate,
        _ => RateControlMode.AverageBitrate
    };

    public string DecisionReason => Reason ?? "未提供计划说明。";

    public IReadOnlyList<string> BuildCommandPreview(string inputPath, string outputPath) =>
        BuildArguments(inputPath, outputPath, firstPass: false, IsTwoPass ? "<pass-log>" : null);

    public string RateControlDisplay => RateControl ?? Mode switch
    {
        CompressionMode.Crf => Encoder switch
        {
            VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc => $"CQ {Crf:0.##}",
            VideoEncoder.H264Qsv or VideoEncoder.HevcQsv => $"Global Quality {Crf:0.##}",
            VideoEncoder.H264Amf or VideoEncoder.HevcAmf => $"CQP {Crf:0.##}",
            _ => $"CRF {Crf:0.##}"
        },
        CompressionMode.TargetSize => (TargetSizeBytes is { } target
            ? $"目标大小 · {DisplayFormat.FileSize(target)}{(IsTwoPass ? " · Two-pass" : " · 平均码率 + 峰值约束")}"
            : "目标大小"),
        CompressionMode.SmartAutomatic => "平均码率 + 峰值约束",
        _ => MaxVideoBitrateBps is > 0 ? "平均码率 + 峰值约束" : "平均视频码率"
    };

    public string EstimatedOutputDisplay
    {
        get
        {
            if (EstimatedOutputLowerBoundBytes is { } lower && EstimatedOutputUpperBoundBytes is { } upper)
            {
                return lower == upper
                    ? $"约 {DisplayFormat.FileSize(lower)}"
                    : $"约 {DisplayFormat.FileSize(lower)}～{DisplayFormat.FileSize(upper)}";
            }

            return EstimatedOutputSizeBytes is { } exact
                ? $"约 {DisplayFormat.FileSize(exact)}"
                : "无法精确预估";
        }
    }

    public string EstimatedSavingDisplay(long sourceSizeBytes)
    {
        if (sourceSizeBytes <= 0)
        {
            return "无法精确预估";
        }

        if (EstimatedOutputLowerBoundBytes is { } lower && EstimatedOutputUpperBoundBytes is { } upper)
        {
            var savingFromUpper = (1 - upper / (double)sourceSizeBytes) * 100;
            var savingFromLower = (1 - lower / (double)sourceSizeBytes) * 100;
            return lower == upper
                ? $"预计节省 {savingFromLower:0.0}%"
                : $"预计节省约 {Math.Min(savingFromUpper, savingFromLower):0.0}%～{Math.Max(savingFromUpper, savingFromLower):0.0}%";
        }

        if (EstimatedOutputSizeBytes is { } exact)
        {
            return $"预计节省 {(1 - exact / (double)sourceSizeBytes) * 100:0.0}%";
        }

        return "无法精确预估";
    }

    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, bool firstPass, string? passLogPrefix = null)
    {
        var arguments = new List<string> { "-y", "-i", inputPath };
        if (firstPass)
        {
            arguments.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0"]);
        }

        arguments.AddRange(BuildVideoArguments());
        var filters = BuildVideoFilters();
        if (filters.Count > 0)
        {
            arguments.Add("-vf");
            arguments.Add(string.Join(',', filters));
        }

        if (IsTwoPass)
        {
            arguments.AddRange(["-pass", firstPass ? "1" : "2", "-passlogfile", passLogPrefix ?? throw new ArgumentNullException(nameof(passLogPrefix))]);
        }

        if (firstPass)
        {
            arguments.AddRange(["-f", "null", "NUL"]);
            return arguments;
        }

        arguments.AddRange(BuildAudioAndAttachmentArguments());
        arguments.Add("-max_muxing_queue_size");
        arguments.Add("4096");
        if (OutputExtension is ".mp4" or ".m4v" or ".mov")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(outputPath);
        return arguments;
    }

    private IEnumerable<string> BuildVideoArguments()
    {
        yield return "-c:v";
        yield return FfmpegEncoderName(Encoder);
        foreach (var argument in EncoderStrategyCatalog.Get(Encoder).BuildVideoArguments(this))
        {
            yield return argument;
        }
    }

    private List<string> BuildVideoFilters()
    {
        var filters = new List<string>();
        if (ResolutionLimit is { } limit)
        {
            filters.Add($"scale=w='min(iw,{limit.Width})':h='min(ih,{limit.Height})':force_original_aspect_ratio=decrease:force_divisible_by=2");
        }
        if (FpsLimit is { } fps)
        {
            filters.Add($"fps=fps={fps.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return filters;
    }

    private IEnumerable<string> BuildAudioAndAttachmentArguments()
    {
        yield return "-c:a";
        yield return AudioMode == AudioMode.Copy ? "copy" : "aac";
        if (AudioMode == AudioMode.Aac)
        {
            yield return "-b:a";
            yield return $"{AudioBitrateKbps}k";
        }

        yield return "-c:s";
        yield return "copy";
        yield return "-c:t";
        yield return "copy";
    }

    public static string FfmpegEncoderName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Libx264 => "libx264",
        VideoEncoder.Libx265 => "libx265",
        VideoEncoder.H264Nvenc => "h264_nvenc",
        VideoEncoder.HevcNvenc => "hevc_nvenc",
        VideoEncoder.H264Qsv => "h264_qsv",
        VideoEncoder.HevcQsv => "hevc_qsv",
        VideoEncoder.H264Amf => "h264_amf",
        VideoEncoder.HevcAmf => "hevc_amf",
        VideoEncoder.LibsvtAv1 => "libsvtav1",
        _ => "libx264"
    };
}
