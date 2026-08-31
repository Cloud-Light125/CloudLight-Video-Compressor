namespace CloudLight.VideoCompressor.Models;

using CloudLight.VideoCompressor.Services;

public sealed class VideoFileInfo
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string Extension { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public double? DurationSeconds { get; init; }
    public string? VideoCodec { get; init; }
    public long? VideoBitrateBps { get; init; }
    public long? TotalBitrateBps { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string? AudioCodec { get; init; }
    public long? AudioBitrateBps { get; init; }
    public int AudioTrackCount { get; init; }
    public IReadOnlyList<string> SubtitleCodecs { get; init; } = Array.Empty<string>();
    public int SubtitleTrackCount { get; init; }
    public int ChapterCount { get; init; }
    public string? Container { get; init; }
    public string? PixelFormat { get; init; }
    public int? BitDepth { get; init; }
    public string? VideoProfile { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    public IReadOnlyDictionary<string, string> MasteringDisplayMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ContentLightMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int? PrimaryVideoStreamIndex { get; init; }
    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = Array.Empty<MediaStreamInfo>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public MediaHealthStatus HealthStatus { get; init; } = MediaHealthStatus.NotChecked;
    public HealthCheckLevel HealthCheckLevel { get; init; } = HealthCheckLevel.Disabled;
    public DateTimeOffset? HealthCheckedAt { get; init; }
    public string? HealthCheckMessage { get; init; }
    public string? HealthCheckFingerprint { get; init; }

    public bool HasProbeData => DurationSeconds is not null || VideoCodec is not null || Width is not null;

    public int EffectiveBitDepth => BitDepth is > 0
        ? BitDepth.Value
        : BitDepthPolicyResolver.DetectPixelFormatBitDepth(PixelFormat) ?? 8;

    public bool IsHdr =>
        EffectiveBitDepth >= 10 &&
        (string.Equals(ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorTransfer, "hlg", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase));

    public string HdrSummaryDisplay => IsHdr
        ? $"HDR · {VideoProfile ?? "Main10"} · {EffectiveBitDepth}-bit · {ColorTransfer ?? "BT.2020"}"
        : $"{EffectiveBitDepth}-bit SDR";

    /// <summary>Number of luma pixels in one frame when probe data is available.</summary>
    public long? PixelCount => Width is > 0 && Height is > 0
        ? (long)Width.Value * Height.Value
        : null;

    /// <summary>
    /// Source bits per pixel per frame. This is a planner signal, not a visual
    /// quality score; motion, grain and scene complexity are not represented by
    /// this scalar.
    /// </summary>
    public double? BitsPerPixelPerFrame => PixelCount is > 0 && FrameRate is > 0 && VideoBitrateBps is > 0
        ? VideoBitrateBps.Value / (double)(PixelCount.Value * FrameRate.Value)
        : null;

    /// <summary>Bits per pixel per second, useful when FPS is unavailable.</summary>
    public double? BitsPerPixelPerSecond => PixelCount is > 0 && VideoBitrateBps is > 0
        ? VideoBitrateBps.Value / (double)PixelCount.Value
        : null;

    public static VideoFileInfo FromFile(string path)
    {
        var file = new FileInfo(path);
        return new VideoFileInfo
        {
            FileName = file.Name,
            FullPath = file.FullName,
            Extension = file.Extension,
            FileSizeBytes = file.Length,
            LastWriteTimeUtc = file.LastWriteTimeUtc
        };
    }

    public VideoFileInfo WithProbeData(VideoFileInfo probe) => new()
    {
        FileName = FileName,
        FullPath = FullPath,
        Extension = Extension,
        FileSizeBytes = FileSizeBytes,
        LastWriteTimeUtc = probe.LastWriteTimeUtc == default ? LastWriteTimeUtc : probe.LastWriteTimeUtc,
        DurationSeconds = probe.DurationSeconds,
        VideoCodec = probe.VideoCodec,
        VideoBitrateBps = probe.VideoBitrateBps,
        TotalBitrateBps = probe.TotalBitrateBps,
        Width = probe.Width,
        Height = probe.Height,
        FrameRate = probe.FrameRate,
        AudioCodec = probe.AudioCodec,
        AudioBitrateBps = probe.AudioBitrateBps,
        AudioTrackCount = probe.AudioTrackCount,
        SubtitleCodecs = probe.SubtitleCodecs,
        SubtitleTrackCount = probe.SubtitleTrackCount,
        ChapterCount = probe.ChapterCount,
        Container = probe.Container,
        PixelFormat = probe.PixelFormat,
        BitDepth = probe.BitDepth,
        VideoProfile = probe.VideoProfile,
        ColorPrimaries = probe.ColorPrimaries,
        ColorTransfer = probe.ColorTransfer,
        ColorSpace = probe.ColorSpace,
        ColorRange = probe.ColorRange,
        MasteringDisplayMetadata = probe.MasteringDisplayMetadata,
        ContentLightMetadata = probe.ContentLightMetadata,
        PrimaryVideoStreamIndex = probe.PrimaryVideoStreamIndex,
        Streams = probe.Streams,
        Metadata = probe.Metadata,
        HealthStatus = probe.HealthStatus,
        HealthCheckLevel = probe.HealthCheckLevel,
        HealthCheckedAt = probe.HealthCheckedAt,
        HealthCheckMessage = probe.HealthCheckMessage,
        HealthCheckFingerprint = probe.HealthCheckFingerprint
    };

    public VideoFileInfo WithHealthCheck(MediaHealthCheckResult result) => new()
    {
        FileName = FileName,
        FullPath = FullPath,
        Extension = Extension,
        FileSizeBytes = FileSizeBytes,
        LastWriteTimeUtc = LastWriteTimeUtc,
        DurationSeconds = DurationSeconds,
        VideoCodec = VideoCodec,
        VideoBitrateBps = VideoBitrateBps,
        TotalBitrateBps = TotalBitrateBps,
        Width = Width,
        Height = Height,
        FrameRate = FrameRate,
        AudioCodec = AudioCodec,
        AudioBitrateBps = AudioBitrateBps,
        AudioTrackCount = AudioTrackCount,
        SubtitleCodecs = SubtitleCodecs,
        SubtitleTrackCount = SubtitleTrackCount,
        ChapterCount = ChapterCount,
        Container = Container,
        PixelFormat = PixelFormat,
        BitDepth = BitDepth,
        VideoProfile = VideoProfile,
        ColorPrimaries = ColorPrimaries,
        ColorTransfer = ColorTransfer,
        ColorSpace = ColorSpace,
        ColorRange = ColorRange,
        MasteringDisplayMetadata = MasteringDisplayMetadata,
        ContentLightMetadata = ContentLightMetadata,
        PrimaryVideoStreamIndex = PrimaryVideoStreamIndex,
        Streams = Streams,
        Metadata = Metadata,
        HealthStatus = result.Status,
        HealthCheckLevel = result.Level,
        HealthCheckedAt = result.CheckedAt,
        HealthCheckMessage = result.Message,
        HealthCheckFingerprint = result.Fingerprint.StableValue
    };

    public VideoFileInfo WithFileIdentity(MediaFileFingerprint fingerprint) => new()
    {
        FileName = Path.GetFileName(fingerprint.NormalizedFullPath),
        FullPath = fingerprint.NormalizedFullPath,
        Extension = Path.GetExtension(fingerprint.NormalizedFullPath),
        FileSizeBytes = fingerprint.FileSizeBytes,
        LastWriteTimeUtc = fingerprint.LastWriteTimeUtc,
        DurationSeconds = DurationSeconds,
        VideoCodec = VideoCodec,
        VideoBitrateBps = VideoBitrateBps,
        TotalBitrateBps = TotalBitrateBps,
        Width = Width,
        Height = Height,
        FrameRate = FrameRate,
        AudioCodec = AudioCodec,
        AudioBitrateBps = AudioBitrateBps,
        AudioTrackCount = AudioTrackCount,
        SubtitleCodecs = SubtitleCodecs,
        SubtitleTrackCount = SubtitleTrackCount,
        ChapterCount = ChapterCount,
        Container = Container,
        PixelFormat = PixelFormat,
        BitDepth = BitDepth,
        VideoProfile = VideoProfile,
        ColorPrimaries = ColorPrimaries,
        ColorTransfer = ColorTransfer,
        ColorSpace = ColorSpace,
        ColorRange = ColorRange,
        MasteringDisplayMetadata = MasteringDisplayMetadata,
        ContentLightMetadata = ContentLightMetadata,
        PrimaryVideoStreamIndex = PrimaryVideoStreamIndex,
        Streams = Streams,
        Metadata = Metadata,
        HealthStatus = HealthStatus,
        HealthCheckLevel = HealthCheckLevel,
        HealthCheckedAt = HealthCheckedAt,
        HealthCheckMessage = HealthCheckMessage,
        HealthCheckFingerprint = HealthCheckFingerprint
    };
}
