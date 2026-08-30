namespace CloudLight.VideoCompressor.Models;

public sealed record EncoderCapability(
    string Id,
    string DisplayName,
    VideoEncoder Encoder,
    VideoCodecKind Codec,
    EncoderVendor Vendor,
    bool IsHardware,
    bool IsSupportedByFfmpeg,
    bool IsUsable,
    string? UnavailableReason);

public sealed class EncoderCapabilitySet
{
    private readonly IReadOnlyDictionary<VideoEncoder, EncoderCapability> _byEncoder;

    public EncoderCapabilitySet(IEnumerable<EncoderCapability> capabilities)
    {
        Capabilities = capabilities.ToArray();
        _byEncoder = Capabilities
            .GroupBy(capability => capability.Encoder)
            .ToDictionary(group => group.Key, group => group.Last());
    }

    public IReadOnlyList<EncoderCapability> Capabilities { get; }

    public bool IsUsable(VideoEncoder encoder) =>
        _byEncoder.TryGetValue(encoder, out var capability)
            ? capability.IsUsable
            : !EncoderCatalog.Get(encoder).IsHardware;

    public EncoderCapability? Get(VideoEncoder encoder) =>
        _byEncoder.TryGetValue(encoder, out var capability) ? capability : null;

    public bool HasUsableHardware(EncoderVendor vendor) =>
        Capabilities.Any(capability => capability.IsHardware && capability.Vendor == vendor && capability.IsUsable);

    public bool HasUsableHardware(EncoderVendor vendor, VideoCodecKind codec) =>
        Capabilities.Any(capability =>
            capability.IsHardware &&
            capability.Vendor == vendor &&
            capability.Codec == codec &&
            capability.IsUsable);

    public static EncoderCapabilitySet SoftwareDefaults { get; } = new(
        EncoderCatalog.Definitions
            .Where(definition => !definition.IsHardware)
            .Select(definition => new EncoderCapability(
                definition.Id,
                definition.DisplayName,
                definition.Encoder,
                definition.Codec,
                definition.Vendor,
                false,
                true,
                true,
                null)));
}

public sealed record EncoderDefinition(
    string Id,
    string DisplayName,
    VideoEncoder Encoder,
    VideoCodecKind Codec,
    EncoderVendor Vendor,
    bool IsHardware);

public static class EncoderCatalog
{
    public static IReadOnlyList<EncoderDefinition> Definitions { get; } =
    [
        new("libx264", "CPU 软件编码 · H.264", VideoEncoder.Libx264, VideoCodecKind.H264, EncoderVendor.Cpu, false),
        new("libx265", "CPU 软件编码 · H.265", VideoEncoder.Libx265, VideoCodecKind.H265, EncoderVendor.Cpu, false),
        new("h264_nvenc", "NVIDIA NVENC · H.264", VideoEncoder.H264Nvenc, VideoCodecKind.H264, EncoderVendor.Nvidia, true),
        new("hevc_nvenc", "NVIDIA NVENC · H.265", VideoEncoder.HevcNvenc, VideoCodecKind.H265, EncoderVendor.Nvidia, true),
        new("h264_qsv", "Intel Quick Sync · H.264", VideoEncoder.H264Qsv, VideoCodecKind.H264, EncoderVendor.Intel, true),
        new("hevc_qsv", "Intel Quick Sync · H.265", VideoEncoder.HevcQsv, VideoCodecKind.H265, EncoderVendor.Intel, true),
        new("h264_amf", "AMD AMF · H.264", VideoEncoder.H264Amf, VideoCodecKind.H264, EncoderVendor.Amd, true),
        new("hevc_amf", "AMD AMF · H.265", VideoEncoder.HevcAmf, VideoCodecKind.H265, EncoderVendor.Amd, true),
        new("libsvtav1", "CPU 软件编码 · AV1", VideoEncoder.LibsvtAv1, VideoCodecKind.H265, EncoderVendor.Cpu, false)
    ];

    public static EncoderDefinition Get(VideoEncoder encoder) =>
        Definitions.First(definition => definition.Encoder == encoder);
}
