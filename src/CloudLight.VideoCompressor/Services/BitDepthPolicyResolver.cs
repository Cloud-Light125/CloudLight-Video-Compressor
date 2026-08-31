using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record BitDepthDecision(
    BitDepthPolicy Policy,
    int SourceBitDepth,
    int TargetBitDepth,
    bool SourceIsHdr,
    bool IsSupported,
    string? Warning,
    string TargetPixelFormat,
    string? TargetProfile)
{
    public bool BlocksExecution => !IsSupported;
    public bool IsReduced => SourceBitDepth >= 10 && TargetBitDepth < SourceBitDepth;
    public string Display => $"{TargetBitDepth}-bit";
}

/// <summary>
/// Resolves bit depth before encoder selection. Auto preserves Main10/HDR
/// sources and never treats bit depth as an ordinary bitrate optimization.
/// </summary>
public static class BitDepthPolicyResolver
{
    public static BitDepthDecision Resolve(
        VideoFileInfo source,
        BitDepthPolicy policy,
        VideoEncoder encoder,
        EncoderCapabilitySet? capabilities = null)
    {
        var sourceDepth = DetectBitDepth(source);
        var targetDepth = ResolveTargetBitDepth(sourceDepth, policy);
        var sourceIsHdr = source.IsHdr;
        var supported = capabilities?.SupportsBitDepth(encoder, targetDepth) ??
            EncoderStrategyCatalog.Get(encoder).SupportsBitDepth(targetDepth);
        var warning = sourceDepth >= 10 && targetDepth < 10
            ? sourceIsHdr
                ? "源文件为 HDR / 10-bit；当前计划会转换为 8-bit，可能降低 HDR 元数据和高光细节。"
                : "源文件为 10-bit；当前计划会转换为 8-bit，可能降低色阶细节。"
            : targetDepth >= 10 && !supported
                ? $"{EncoderCatalog.Get(encoder).DisplayName} 当前无法可靠输出 10-bit / Main10。"
                : sourceIsHdr
                    ? "检测到 HDR / Main10，计划保持 10-bit 与基础色彩元数据。"
                    : null;

        return new BitDepthDecision(
            policy,
            sourceDepth,
            targetDepth,
            sourceIsHdr,
            supported,
            warning,
            TargetPixelFormat(encoder, targetDepth),
            TargetProfile(encoder, targetDepth));
    }

    public static int DetectBitDepth(VideoFileInfo source) =>
        source.BitDepth is > 0
            ? source.BitDepth.Value
            : DetectPixelFormatBitDepth(source.PixelFormat) ?? 8;

    public static int ResolveTargetBitDepth(int sourceBitDepth, BitDepthPolicy policy) => policy switch
    {
        BitDepthPolicy.TenBit => 10,
        BitDepthPolicy.EightBit => 8,
        _ => sourceBitDepth >= 10 ? 10 : 8
    };

    public static int? DetectPixelFormatBitDepth(string? pixelFormat)
    {
        var value = pixelFormat?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Contains("16", StringComparison.Ordinal))
        {
            return 16;
        }
        if (value.Contains("12", StringComparison.Ordinal))
        {
            return 12;
        }
        if (value.Contains("10", StringComparison.Ordinal) ||
            value is "p010le" or "p010be" or "x2rgb10le" or "x2rgb10be")
        {
            return 10;
        }

        return value.Contains("9", StringComparison.Ordinal) ? 9 : 8;
    }

    public static string TargetPixelFormat(VideoEncoder encoder, int bitDepth) =>
        bitDepth >= 10
            ? encoder is VideoEncoder.H264Qsv or VideoEncoder.HevcQsv or VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.H264Amf or VideoEncoder.HevcAmf
                ? "p010le"
                : "yuv420p10le"
            : encoder is VideoEncoder.H264Qsv or VideoEncoder.HevcQsv or VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc or VideoEncoder.H264Amf or VideoEncoder.HevcAmf
                ? "nv12"
                : "yuv420p";

    public static string? TargetProfile(VideoEncoder encoder, int bitDepth) => bitDepth >= 10
        ? EncoderCatalog.Get(encoder).Codec == VideoCodecKind.H265 ? "main10" : "high10"
        : null;
}
