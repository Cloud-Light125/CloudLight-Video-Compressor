using System.Globalization;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class CompressionPlanner
{
    private readonly SmartCompressionPlanner _smartCompressionPlanner;
    private readonly CompressionResultCache? _resultCache;
    private readonly ContainerCompatibilityPolicy _containerCompatibilityPolicy;

    public CompressionPlanner(
        SmartCompressionPlanner? smartCompressionPlanner = null,
        CompressionResultCache? resultCache = null,
        ContainerCompatibilityPolicy? containerCompatibilityPolicy = null)
    {
        _smartCompressionPlanner = smartCompressionPlanner ?? new SmartCompressionPlanner();
        _resultCache = resultCache;
        _containerCompatibilityPolicy = containerCompatibilityPolicy ?? new ContainerCompatibilityPolicy();
    }

    public CompressionResultCache? ResultCache => _resultCache;

    public CompressionPlan CreatePlan(
        VideoFileInfo media,
        AppSettings settings,
        TargetSizeCalculation? targetSizeCalculation = null,
        EncoderCapabilitySet? capabilities = null,
        EncoderBenchmarkSnapshot? benchmark = null)
    {
        var warnings = new List<string>();
        var targetCodec = settings.TargetVideoCodec ?? LegacyCodec(settings.VideoEncoder);
        var targetBitDepth = BitDepthPolicyResolver.ResolveTargetBitDepth(
            BitDepthPolicyResolver.DetectBitDepth(media),
            settings.BitDepthPolicy);
        SmartCompressionDecision? smartDecision = null;
        EncoderSelectionResult selection;

        if (settings.CompressionMode == CompressionMode.SmartAutomatic)
        {
            var smartSettings = settings;
            if (settings.EncoderSelection is null)
            {
                smartSettings = settings.Clone();
                smartSettings.EncoderSelection = EncoderSelectionMode.Automatic;
            }

            if (_resultCache?.TryGet(media, settings, out var cachedResult) == true &&
                cachedResult.DecisionSnapshot is { } cachedDecision)
            {
                targetCodec = cachedDecision.TargetCodec;
                selection = EncoderSelectionResolver.Resolve(
                    smartSettings,
                    targetCodec,
                    capabilities,
                    benchmark: benchmark,
                    media: media,
                    targetBitDepth: targetBitDepth,
                    tuningPreset: settings.EncoderTuningPreset,
                    profile: settings.CompressionProfile,
                    performanceMode: settings.PerformanceMode);
                smartDecision = cachedDecision with
                {
                    SelectedEncoder = selection.SelectedEncoder,
                    AutoEncoderDecision = selection.AutoDecision
                };
                DiagnosticLog.Write("result-cache", $"Smart 命中缓存：{media.FullPath}");
            }
            else
            {
                smartDecision = _smartCompressionPlanner.CreateDecision(media, settings, capabilities, benchmark);
                selection = EncoderSelectionResolver.Resolve(
                    smartSettings,
                    smartDecision.TargetCodec,
                    capabilities,
                    benchmark: benchmark,
                    media: media,
                    targetBitDepth: targetBitDepth,
                    tuningPreset: settings.EncoderTuningPreset,
                    profile: settings.CompressionProfile,
                    performanceMode: settings.PerformanceMode);
                smartDecision = smartDecision with
                {
                    SelectedEncoder = selection.SelectedEncoder,
                    AutoEncoderDecision = selection.AutoDecision
                };
                _resultCache?.Set(media, settings, smartDecision);
                DiagnosticLog.Write("result-cache", $"Smart 缓存未命中：{media.FullPath}");
            }

            targetCodec = smartDecision.TargetCodec;
        }
        else
        {
            selection = EncoderSelectionResolver.Resolve(
                settings,
                targetCodec,
                capabilities,
                preferHardwareForAutomatic: false,
                benchmark: benchmark,
                media: media,
                targetBitDepth: targetBitDepth,
                tuningPreset: settings.EncoderTuningPreset,
                profile: settings.CompressionProfile,
                performanceMode: settings.PerformanceMode);
        }

        warnings.AddRange(selection.Warnings);
        var effectiveEncoder = selection.SelectedEncoder;
        var bitDepthDecision = BitDepthPolicyResolver.Resolve(
            media,
            settings.BitDepthPolicy,
            effectiveEncoder,
            capabilities);
        if (!string.IsNullOrWhiteSpace(bitDepthDecision.Warning))
        {
            warnings.Add(bitDepthDecision.Warning);
        }
        var encodingPreset = EncoderTuningCatalog.Resolve(
            effectiveEncoder,
            settings.EncoderTuningPreset,
            settings.EncodingPreset);

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

        var outputExtension = OutputPathService.GetOutputExtension(media, settings).ToLowerInvariant();
        var streamAudit = _containerCompatibilityPolicy.Audit(media, outputExtension, settings.AudioMode);
        warnings.AddRange(streamAudit.Warnings);
        if (outputExtension is ".webm" or ".avi")
        {
            warnings.Add($"{outputExtension} 容器与 H.264/H.265 兼容性有限；建议输出为 MP4 或 MKV。若 FFmpeg 拒绝该组合，源文件不会被修改。");
        }

        return new CompressionPlan(
            isTwoPass,
            effectiveEncoder,
            settings.CompressionMode,
            settings.Crf,
            encodingPreset,
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
            StreamAudit = streamAudit,
            CompressionBenefitScore = smartDecision?.CompressionBenefitScore ?? 0,
            SourceBitsPerPixel = smartDecision?.SourceBitsPerPixel,
            SourceBitsPerPixelPerFrame = smartDecision?.SourceBitsPerPixelPerFrame,
            TargetBitsPerPixelPerFrame = smartDecision?.TargetBitsPerPixelPerFrame,
            EncoderTuningPreset = settings.EncoderTuningPreset,
            BitDepthPolicy = settings.BitDepthPolicy,
            TargetBitDepth = bitDepthDecision.TargetBitDepth,
            TargetPixelFormat = bitDepthDecision.TargetPixelFormat,
            TargetProfile = bitDepthDecision.TargetProfile,
            BitDepthDecision = bitDepthDecision,
            AutoEncoderDecision = selection.AutoDecision,
            BlocksExecution = streamAudit.BlocksExecution || bitDepthDecision.BlocksExecution,
            Reason = selection.AutoDecision?.Reason ??
                     smartDecision?.Reason ??
                     $"使用 {EncoderCatalog.Get(effectiveEncoder).DisplayName}，目标为 {targetCodec.GetDescription()} {bitDepthDecision.TargetBitDepth}-bit。"
        };
    }

    public SmartCompressionDecision CreateSmartDecision(
        VideoFileInfo media,
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null,
        EncoderBenchmarkSnapshot? benchmark = null)
    {
        if (_resultCache?.TryGet(media, settings, out var cachedResult) == true &&
            cachedResult.DecisionSnapshot is { } cachedDecision)
        {
            var selected = EncoderSelectionResolver.Resolve(
                settings,
                cachedDecision.TargetCodec,
                capabilities,
                benchmark: benchmark,
                media: media,
                targetBitDepth: BitDepthPolicyResolver.ResolveTargetBitDepth(
                    BitDepthPolicyResolver.DetectBitDepth(media),
                    settings.BitDepthPolicy),
                tuningPreset: settings.EncoderTuningPreset,
                profile: settings.CompressionProfile,
                performanceMode: settings.PerformanceMode);
            return cachedDecision with
            {
                SelectedEncoder = selected.SelectedEncoder,
                AutoEncoderDecision = selected.AutoDecision
            };
        }

        var decision = _smartCompressionPlanner.CreateDecision(media, settings, capabilities, benchmark);
        _resultCache?.Set(media, settings, decision);
        return decision;
    }

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
        settings.FpsLimit != FpsLimitPreset.Keep ||
        settings.BitDepthPolicy != BitDepthPolicy.EightBit;

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
    public ContainerCompatibilityAudit? StreamAudit { get; init; }
    public VmafCalibrationResult? QualityCalibration { get; init; }
    public double CompressionBenefitScore { get; init; }
    public double? SourceBitsPerPixel { get; init; }
    public double? SourceBitsPerPixelPerFrame { get; init; }
    public double? TargetBitsPerPixelPerFrame { get; init; }
    public EncoderTuningPreset EncoderTuningPreset { get; init; } = EncoderTuningPreset.Balanced;
    public BitDepthPolicy BitDepthPolicy { get; init; } = BitDepthPolicy.Auto;
    public int TargetBitDepth { get; init; } = 8;
    public string? TargetPixelFormat { get; init; }
    public string? TargetProfile { get; init; }
    public BitDepthDecision? BitDepthDecision { get; init; }
    public AutoEncoderDecision? AutoEncoderDecision { get; init; }
    public bool BlocksExecution { get; init; }

    public bool IsHdrSource => InputInfo?.IsHdr == true;

    public IReadOnlyList<VideoEncoder> EncoderCandidates =>
        [Encoder, .. (FallbackEncoders ?? Array.Empty<VideoEncoder>()).Where(candidate => candidate != Encoder).Take(3)];

    public CompressionPlan WithEncoder(VideoEncoder encoder) =>
        WithEncoderCore(encoder);

    private CompressionPlan WithEncoderCore(VideoEncoder encoder)
    {
        var bitDepthDecision = InputInfo is { } source
            ? BitDepthPolicyResolver.Resolve(source, BitDepthPolicy, encoder, null)
            : BitDepthDecision;
        return this with
        {
            Encoder = encoder,
            EncodingPreset = EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset),
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
                : SmartDecision with { SelectedEncoder = encoder },
            TargetPixelFormat = bitDepthDecision is null
                ? BitDepthPolicyResolver.TargetPixelFormat(encoder, TargetBitDepth)
                : bitDepthDecision.TargetPixelFormat,
            TargetProfile = bitDepthDecision?.TargetProfile ?? BitDepthPolicyResolver.TargetProfile(encoder, TargetBitDepth),
            BitDepthDecision = bitDepthDecision,
            BlocksExecution = StreamAudit?.BlocksExecution == true ||
                              (bitDepthDecision?.BlocksExecution ?? BlocksExecution),
            AutoEncoderDecision = AutoEncoderDecision is null
                ? null
                : AutoEncoderDecision with
                {
                    SelectedEncoder = encoder,
                    FallbackChain = EncoderCandidates.Where(candidate => candidate != encoder).ToArray()
                }
        };
    }

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
            arguments.AddRange(["-map", GetPrimaryVideoMap(), "-an", "-sn", "-dn"]);
        }
        else if (StreamAudit is { Decisions.Count: > 0 } audit)
        {
            foreach (var decision in audit.Decisions.Where(decision => decision.Action != StreamRetentionAction.Remove))
            {
                arguments.AddRange(["-map", $"0:{decision.Stream.StreamIndex}"]);
            }
            arguments.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        }
        else
        {
            // Use the probed primary index when available so an attached
            // picture that happens to be the first video stream is never fed
            // into a VMAF sample or a legacy no-audit plan as the main video.
            arguments.AddRange(["-map", GetPrimaryVideoMap(), "-map", "0:a?", "-map", "0:s?", "-map", "0:t?", "-map_metadata", "0", "-map_chapters", "0"]);
        }

        arguments.AddRange(BuildVideoArguments());
        if (!firstPass && StreamAudit is { Decisions.Count: > 0 })
        {
            arguments.AddRange(BuildAttachedPictureArguments());
            arguments.AddRange(BuildStreamMetadataArguments());
        }
        var filters = BuildVideoFilters();
        if (filters.Count > 0)
        {
            if (!firstPass && StreamAudit is { Decisions.Count: > 0 })
            {
                arguments.AddRange(BuildAuditedVideoFilterArguments(filters));
            }
            else
            {
                arguments.Add("-vf");
                arguments.Add(string.Join(',', filters));
            }
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

    private string GetPrimaryVideoMap() =>
        InputInfo?.PrimaryVideoStreamIndex is { } streamIndex && streamIndex >= 0
            ? $"0:{streamIndex}"
            : "0:v:0";

    private IEnumerable<string> BuildVideoArguments()
    {
        yield return "-c:v";
        yield return FfmpegEncoderName(Encoder);
        foreach (var argument in EncoderStrategyCatalog.Get(Encoder).BuildVideoArguments(this))
        {
            yield return argument;
        }

        yield return "-pix_fmt";
        yield return TargetPixelFormat ?? BitDepthPolicyResolver.TargetPixelFormat(Encoder, TargetBitDepth);
        if (TargetBitDepth >= 10 && !string.IsNullOrWhiteSpace(TargetProfile))
        {
            yield return "-profile:v";
            yield return TargetProfile!;
        }

        if (IsHdrSource)
        {
            if (!string.IsNullOrWhiteSpace(InputInfo?.ColorPrimaries))
            {
                yield return "-color_primaries";
                yield return InputInfo.ColorPrimaries!;
            }
            if (!string.IsNullOrWhiteSpace(InputInfo?.ColorTransfer))
            {
                yield return "-color_trc";
                yield return InputInfo.ColorTransfer!;
            }
            if (!string.IsNullOrWhiteSpace(InputInfo?.ColorSpace))
            {
                yield return "-colorspace";
                yield return InputInfo.ColorSpace!;
            }
            if (!string.IsNullOrWhiteSpace(InputInfo?.ColorRange))
            {
                yield return "-color_range";
                yield return InputInfo.ColorRange!;
            }
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
        if (StreamAudit is { Decisions.Count: > 0 } audit)
        {
            var audioDecisions = audit.Decisions
                .Where(decision => decision.Stream.StreamType == MediaStreamType.Audio)
                .ToArray();
            if (audioDecisions.Length > 0)
            {
                var encodeAudio = audioDecisions.Any(decision => decision.Action == StreamRetentionAction.EncodeAudio);
                yield return "-c:a";
                yield return encodeAudio ? "aac" : "copy";
                if (encodeAudio)
                {
                    yield return "-b:a";
                    yield return $"{AudioBitrateKbps}k";
                }
            }

            var subtitleDecisions = audit.Decisions
                .Where(decision => decision.Stream.StreamType == MediaStreamType.Subtitle)
                .ToArray();
            if (subtitleDecisions.Length > 0)
            {
                yield return "-c:s";
                yield return "copy";
                for (var ordinal = 0; ordinal < subtitleDecisions.Length; ordinal++)
                {
                    if (subtitleDecisions[ordinal].Action == StreamRetentionAction.ConvertSubtitle)
                    {
                        yield return $"-c:s:{ordinal}";
                        yield return "mov_text";
                    }
                }
            }

            if (audit.Decisions.Any(decision => decision.Stream.StreamType == MediaStreamType.Attachment))
            {
                yield return "-c:t";
                yield return "copy";
            }
            if (audit.Decisions.Any(decision => decision.Stream.StreamType == MediaStreamType.Data))
            {
                yield return "-c:d";
                yield return "copy";
            }

            yield break;
        }

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

    private IEnumerable<string> BuildAttachedPictureArguments()
    {
        var videoOrdinal = 0;
        foreach (var decision in StreamAudit!.Decisions.Where(decision =>
                     decision.Action != StreamRetentionAction.Remove &&
                     decision.Stream.StreamType == MediaStreamType.Video))
        {
            if (decision.Stream.IsAttachedPicture)
            {
                yield return $"-c:v:{videoOrdinal}";
                yield return "copy";
            }
            videoOrdinal++;
        }
    }

    private IEnumerable<string> BuildAuditedVideoFilterArguments(IReadOnlyList<string> filters)
    {
        var videoOrdinal = 0;
        var filter = string.Join(',', filters);
        foreach (var decision in StreamAudit!.Decisions.Where(decision =>
                     decision.Action != StreamRetentionAction.Remove &&
                     decision.Stream.StreamType == MediaStreamType.Video))
        {
            if (!decision.Stream.IsAttachedPicture)
            {
                yield return $"-filter:v:{videoOrdinal}";
                yield return filter;
            }

            videoOrdinal++;
        }
    }

    private IEnumerable<string> BuildStreamMetadataArguments()
    {
        var ordinals = new Dictionary<MediaStreamType, int>();
        foreach (var decision in StreamAudit!.Decisions.Where(decision => decision.Action != StreamRetentionAction.Remove))
        {
            var ordinal = ordinals.TryGetValue(decision.Stream.StreamType, out var current) ? current : 0;
            ordinals[decision.Stream.StreamType] = ordinal + 1;
            var specifier = decision.Stream.StreamType switch
            {
                MediaStreamType.Video => "v",
                MediaStreamType.Audio => "a",
                MediaStreamType.Subtitle => "s",
                MediaStreamType.Attachment => "t",
                MediaStreamType.Data => "d",
                _ => null
            };
            if (specifier is null)
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (decision.Stream.Metadata is not null)
            {
                foreach (var item in decision.Stream.Metadata)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                    {
                        metadata[item.Key] = item.Value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(decision.Stream.Language))
            {
                metadata.TryAdd("language", decision.Stream.Language);
            }
            if (!string.IsNullOrWhiteSpace(decision.Stream.Title))
            {
                metadata.TryAdd("title", decision.Stream.Title);
            }
            foreach (var item in metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"-metadata:s:{specifier}:{ordinal}";
                yield return $"{item.Key}={item.Value}";
            }

            var dispositionFlags = GetDispositionFlags(decision.Stream);
            if (dispositionFlags is not null)
            {
                yield return $"-disposition:{specifier}:{ordinal}";
                yield return dispositionFlags;
            }
        }
    }

    private static string? GetDispositionFlags(MediaStreamInfo stream)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "default",
            ["dub"] = "dub",
            ["original"] = "original",
            ["comment"] = "comment",
            ["lyrics"] = "lyrics",
            ["karaoke"] = "karaoke",
            ["forced"] = "forced",
            ["hearing_impaired"] = "hearing_impaired",
            ["visual_impaired"] = "visual_impaired",
            ["clean_effects"] = "clean_effects",
            ["attached_pic"] = "attached_pic",
            ["timed_thumbnails"] = "timed_thumbnails",
            ["captions"] = "captions",
            ["descriptions"] = "descriptions",
            ["metadata"] = "metadata",
            ["dependent"] = "dependent",
            ["still_image"] = "still_image",
            ["multilayer"] = "multilayer",
            ["non_diegetic"] = "non_diegetic"
        };

        var hasDisposition = stream.Disposition is not null;
        var flags = stream.Disposition is null
            ? []
            : stream.Disposition
                .Where(item => item.Value != 0 && names.ContainsKey(item.Key))
                .Select(item => names[item.Key])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (stream.Default && !flags.Contains("default", StringComparer.OrdinalIgnoreCase))
        {
            flags.Add("default");
        }
        if (stream.Forced && !flags.Contains("forced", StringComparer.OrdinalIgnoreCase))
        {
            flags.Add("forced");
        }

        if (!hasDisposition && flags.Count == 0)
        {
            return null;
        }

        return flags.Count == 0 ? "0" : string.Join('+', flags);
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
