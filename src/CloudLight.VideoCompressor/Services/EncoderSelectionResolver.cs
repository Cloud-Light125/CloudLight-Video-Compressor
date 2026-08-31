using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record EncoderSelectionResult(
    VideoEncoder SelectedEncoder,
    IReadOnlyList<VideoEncoder> FallbackEncoders,
    IReadOnlyList<string> Warnings,
    AutoEncoderDecision? AutoDecision = null);

/// <summary>
/// Maps the user-facing codec/vendor choices to concrete FFmpeg encoders. All
/// hardware choices are checked against the two-level capability result before
/// they are returned to the workflow.
/// </summary>
public static class EncoderSelectionResolver
{
    public static EncoderSelectionResult Resolve(
        AppSettings settings,
        VideoCodecKind codec,
        EncoderCapabilitySet? capabilities,
        bool preferHardwareForAutomatic = true,
        EncoderBenchmarkSnapshot? benchmark = null,
        VideoFileInfo? media = null,
        int? targetBitDepth = null,
        EncoderTuningPreset? tuningPreset = null,
        CompressionProfile? profile = null,
        PerformanceMode performanceMode = PerformanceMode.Automatic)
    {
        var mode = settings.EncoderSelection;
        var requestedBitDepth = Math.Max(8, targetBitDepth ?? 8);
        if (mode is null)
        {
            var legacyEncoder = settings.VideoEncoder;
            if (EncoderCatalog.Get(legacyEncoder).Codec != codec)
            {
                var softwareForCodec = SoftwareEncoder(codec);
                return new EncoderSelectionResult(
                    softwareForCodec,
                    [],
                    [$"旧设置中的编码器 {EncoderCatalog.Get(legacyEncoder).DisplayName} 与目标编码格式不一致，已使用 {EncoderCatalog.Get(softwareForCodec).DisplayName}。"]);
            }

            var legacyCapability = capabilities?.Get(legacyEncoder);
            if (legacyCapability is { IsUsable: false } ||
                capabilities?.IsUsable(legacyEncoder, requestedBitDepth) == false)
            {
                var software = SoftwareEncoder(codec);
                return new EncoderSelectionResult(
                    software,
                    [],
                    [$"{EncoderCatalog.Get(legacyEncoder).DisplayName} 当前不可用：" +
                     $"{legacyCapability?.UnavailableReason ?? $"{requestedBitDepth}-bit 输出能力未通过检测"}，" +
                     $"已回退到 {EncoderCatalog.Get(software).DisplayName}。"]);
            }

            var legacyUsable = capabilities?.IsUsable(legacyEncoder, requestedBitDepth) ?? !IsHardware(legacyEncoder);
            return new EncoderSelectionResult(
                settings.VideoEncoder,
                IsHardware(legacyEncoder) && legacyUsable ? [SoftwareEncoder(codec)] : [],
                [],
                null);
        }

        if (mode is EncoderSelectionMode.Automatic or EncoderSelectionMode.HardwareAutomatic)
        {
            var auto = new AutoEncoderSelectionService().Select(new AutoEncoderSelectionRequest(
                codec,
                profile ?? settings.CompressionProfile,
                tuningPreset ?? settings.EncoderTuningPreset,
                capabilities,
                benchmark,
                media?.Width,
                media?.Height,
                media?.FrameRate,
                media?.VideoCodec,
                requestedBitDepth,
                null,
                mode == EncoderSelectionMode.HardwareAutomatic,
                performanceMode));
            var autoWarnings = new List<string>();
            if (mode == EncoderSelectionMode.HardwareAutomatic &&
                !EncoderCatalog.Get(auto.SelectedEncoder).IsHardware)
            {
                autoWarnings.Add($"当前没有可用的 {codec.GetDescription()} {requestedBitDepth}-bit 硬件编码器，已回退到 {EncoderCatalog.Get(auto.SelectedEncoder).DisplayName}。");
            }
            var autoFallbacks = auto.FallbackChain.ToList();
            if (mode == EncoderSelectionMode.HardwareAutomatic &&
                auto.SelectedEncoder != SoftwareEncoder(codec) &&
                !autoFallbacks.Contains(SoftwareEncoder(codec)))
            {
                autoFallbacks.Add(SoftwareEncoder(codec));
            }
            return new EncoderSelectionResult(auto.SelectedEncoder, autoFallbacks, autoWarnings, auto);
        }

        var candidates = mode.Value switch
        {
            EncoderSelectionMode.CpuSoftware => [SoftwareEncoder(codec)],
            EncoderSelectionMode.NvidiaNvenc => [HardwareEncoder(codec, EncoderVendor.Nvidia), SoftwareEncoder(codec)],
            EncoderSelectionMode.IntelQsv => [HardwareEncoder(codec, EncoderVendor.Intel), SoftwareEncoder(codec)],
            EncoderSelectionMode.AmdAmf => [HardwareEncoder(codec, EncoderVendor.Amd), SoftwareEncoder(codec)],
            EncoderSelectionMode.HardwareAutomatic => HardwareCandidates(codec),
            EncoderSelectionMode.Automatic when preferHardwareForAutomatic => HardwareCandidates(codec),
            _ => [SoftwareEncoder(codec)]
        };

        var usable = candidates
            .Where(encoder => capabilities?.IsUsable(encoder, requestedBitDepth) ??
                              (!IsHardware(encoder) && EncoderStrategyCatalog.Get(encoder).SupportsBitDepth(requestedBitDepth)))
            .Distinct()
            .ToList();
        if (usable.Count == 0)
        {
            usable.Add(SoftwareEncoder(codec));
        }

        var warnings = new List<string>();
        if (IsHardware(candidates[0]) && usable[0] != candidates[0])
        {
            warnings.Add($"{EncoderCatalog.Get(candidates[0]).DisplayName} 当前不可用，已回退到 {EncoderCatalog.Get(usable[0]).DisplayName}。 ");
        }

        var fallback = mode is not EncoderSelectionMode.CpuSoftware
            ? usable.Skip(1).Take(3).ToArray()
            : [];
        return new EncoderSelectionResult(usable[0], fallback, warnings);
    }

    public static VideoEncoder SoftwareEncoder(VideoCodecKind codec) =>
        codec switch
        {
            VideoCodecKind.H264 => VideoEncoder.Libx264,
            VideoCodecKind.Av1 => VideoEncoder.LibsvtAv1,
            _ => VideoEncoder.Libx265
        };

    public static VideoEncoder HardwareEncoder(VideoCodecKind codec, EncoderVendor vendor) =>
        (codec, vendor) switch
        {
            (VideoCodecKind.H264, EncoderVendor.Nvidia) => VideoEncoder.H264Nvenc,
            (VideoCodecKind.H265, EncoderVendor.Nvidia) => VideoEncoder.HevcNvenc,
            (VideoCodecKind.H264, EncoderVendor.Intel) => VideoEncoder.H264Qsv,
            (VideoCodecKind.H265, EncoderVendor.Intel) => VideoEncoder.HevcQsv,
            (VideoCodecKind.H264, EncoderVendor.Amd) => VideoEncoder.H264Amf,
            (VideoCodecKind.H265, EncoderVendor.Amd) => VideoEncoder.HevcAmf,
            _ => SoftwareEncoder(codec)
        };

    public static bool IsHardware(VideoEncoder encoder) => EncoderCatalog.Get(encoder).IsHardware;

    private static IReadOnlyList<VideoEncoder> HardwareCandidates(VideoCodecKind codec) =>
    [
        HardwareEncoder(codec, EncoderVendor.Nvidia),
        HardwareEncoder(codec, EncoderVendor.Intel),
        HardwareEncoder(codec, EncoderVendor.Amd),
        SoftwareEncoder(codec)
    ];
}
