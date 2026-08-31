using CloudLight.VideoCompressor.Services;

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
    string? UnavailableReason)
{
    public bool ListedByFfmpeg => IsSupportedByFfmpeg;
    public bool InitializationTestPassed { get; init; } = !IsHardware || IsUsable;
    public bool Available => IsUsable;
    public DateTimeOffset? LastProbeTime { get; init; }
    public IReadOnlyList<RateControlMode> SupportedRateControls { get; init; } = Array.Empty<RateControlMode>();
    public IReadOnlyList<string> SupportedPresets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedPixelFormats { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> SupportedBitDepths { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> SupportedProfiles { get; init; } = Array.Empty<string>();
    public bool HelpProbePassed { get; init; }
    public string? FFmpegVersion { get; init; }
    public string? CapabilityFingerprint { get; init; }
    public int? MaxResolutionKnown { get; init; }

    public bool SupportsBitDepth(int bitDepth)
    {
        var requiredDepth = bitDepth >= 10 ? 10 : 8;
        if (SupportedBitDepths.Count > 0)
        {
            return SupportedBitDepths.Any(depth => requiredDepth == 8 ? depth == 8 : depth >= 10);
        }

        if (SupportedPixelFormats.Count > 0)
        {
            return SupportedPixelFormats.Any(format =>
            {
                var depth = BitDepthPolicyResolver.DetectPixelFormatBitDepth(format);
                return requiredDepth == 8 ? depth == 8 : depth >= requiredDepth;
            });
        }

        // Older capability records did not persist pixel-format details. Keep
        // their conservative 8-bit default, while requiring explicit evidence
        // before allowing a 10-bit plan.
        return requiredDepth == 8;
    }
}

public sealed class EncoderCapabilitySet
{
    private readonly IReadOnlyDictionary<VideoEncoder, EncoderCapability> _byEncoder;

    public EncoderCapabilitySet(
        IEnumerable<EncoderCapability> capabilities,
        string? ffmpegVersion = null,
        string? capabilityFingerprint = null)
    {
        Capabilities = capabilities.ToArray();
        _byEncoder = Capabilities
            .GroupBy(capability => capability.Encoder)
            .ToDictionary(group => group.Key, group => group.Last());
        FFmpegVersion = ffmpegVersion ?? Capabilities.Select(capability => capability.FFmpegVersion)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
        CapabilityFingerprint = capabilityFingerprint ?? BuildCapabilityFingerprint(Capabilities);
    }

    public IReadOnlyList<EncoderCapability> Capabilities { get; }
    public string? FFmpegVersion { get; }
    public string CapabilityFingerprint { get; }

    public bool IsUsable(VideoEncoder encoder) =>
        _byEncoder.TryGetValue(encoder, out var capability)
            ? capability.IsUsable
            : !EncoderCatalog.Get(encoder).IsHardware;

    public bool IsUsable(VideoEncoder encoder, int targetBitDepth) =>
        _byEncoder.TryGetValue(encoder, out var capability)
            ? capability.IsUsable && capability.SupportsBitDepth(targetBitDepth)
            : !EncoderCatalog.Get(encoder).IsHardware &&
              EncoderStrategyCatalog.Get(encoder).SupportsBitDepth(targetBitDepth);

    public bool SupportsBitDepth(VideoEncoder encoder, int targetBitDepth) =>
        IsUsable(encoder, targetBitDepth);

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
                null)
            {
                InitializationTestPassed = true,
                LastProbeTime = DateTimeOffset.UtcNow,
                SupportedRateControls = EncoderStrategyCatalog.Get(definition.Encoder).SupportedRateControls,
                SupportedPresets = EncoderStrategyCatalog.Get(definition.Encoder).SupportedPresets,
                SupportedPixelFormats = EncoderStrategyCatalog.Get(definition.Encoder).SupportedPixelFormats,
                SupportedBitDepths = SupportedBitDepths(EncoderStrategyCatalog.Get(definition.Encoder).SupportedPixelFormats),
                SupportedProfiles = DefaultProfiles(definition)
            }));

    private static IReadOnlyList<int> SupportedBitDepths(IReadOnlyList<string> formats) =>
        formats.Select(BitDepthPolicyResolver.DetectPixelFormatBitDepth)
            .Where(depth => depth is > 0)
            .Select(depth => depth!.Value >= 10 ? 10 : 8)
            .Distinct()
            .Order()
            .ToArray();

    private static IReadOnlyList<string> DefaultProfiles(EncoderDefinition definition) =>
        definition.Codec == VideoCodecKind.H265
            ? ["main", "main10"]
            : definition.Codec == VideoCodecKind.H264
                ? ["main", "high", "high10"]
                : [];

    private static string BuildCapabilityFingerprint(IEnumerable<EncoderCapability> capabilities) =>
        string.Join(
            "|",
            capabilities
                .OrderBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
                .Select(capability =>
                    $"{capability.Id}:{capability.IsSupportedByFfmpeg}:{capability.InitializationTestPassed}:{capability.IsUsable}:" +
                    $"{string.Join(',', capability.SupportedBitDepths.Order())}:{string.Join(',', capability.SupportedPixelFormats.Order(StringComparer.OrdinalIgnoreCase))}"));
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
        new("libsvtav1", "CPU 软件编码 · AV1", VideoEncoder.LibsvtAv1, VideoCodecKind.Av1, EncoderVendor.Cpu, false)
    ];

    public static EncoderDefinition Get(VideoEncoder encoder) =>
        Definitions.First(definition => definition.Encoder == encoder);
}
