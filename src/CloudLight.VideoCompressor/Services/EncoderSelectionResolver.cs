using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record EncoderSelectionResult(
    VideoEncoder SelectedEncoder,
    IReadOnlyList<VideoEncoder> FallbackEncoders,
    IReadOnlyList<string> Warnings);

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
        bool preferHardwareForAutomatic = true)
    {
        var mode = settings.EncoderSelection;
        if (mode is null)
        {
            if (capabilities?.Get(settings.VideoEncoder) is { IsUsable: false } unavailable)
            {
                var software = SoftwareEncoder(codec);
                return new EncoderSelectionResult(
                    software,
                    [],
                    [$"{unavailable.DisplayName} 当前不可用：{unavailable.UnavailableReason} 已回退到 {EncoderCatalog.Get(software).DisplayName}。"]);
            }

            return new EncoderSelectionResult(settings.VideoEncoder, [], []);
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
            .Where(encoder => capabilities?.IsUsable(encoder) ?? !IsHardware(encoder))
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

        var fallback = mode is EncoderSelectionMode.Automatic or EncoderSelectionMode.HardwareAutomatic
            ? usable.Skip(1).Take(3).ToArray()
            : [];
        return new EncoderSelectionResult(usable[0], fallback, warnings);
    }

    public static VideoEncoder SoftwareEncoder(VideoCodecKind codec) =>
        codec == VideoCodecKind.H264 ? VideoEncoder.Libx264 : VideoEncoder.Libx265;

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
