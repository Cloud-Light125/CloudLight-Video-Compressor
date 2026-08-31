using System.Globalization;

namespace CloudLight.VideoCompressor.Models;

using CloudLight.VideoCompressor.Services;

/// <summary>
/// The identity used by all persistent media caches. A path by itself is not a
/// media identity because a library may replace a file in place.
/// </summary>
public sealed record MediaFileFingerprint(
    string NormalizedFullPath,
    long FileSizeBytes,
    DateTime LastWriteTimeUtc)
{
    public string StableValue =>
        $"{NormalizedFullPath}\u001f{FileSizeBytes.ToString(CultureInfo.InvariantCulture)}\u001f{LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";

    public static MediaFileFingerprint FromFile(string path)
    {
        var fullPath = NormalizePath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("找不到媒体文件。", fullPath);
        }

        return new MediaFileFingerprint(fullPath, file.Length, file.LastWriteTimeUtc);
    }

    public static MediaFileFingerprint FromVideoInfo(VideoFileInfo source)
    {
        var fullPath = NormalizePath(source.FullPath);
        if (File.Exists(fullPath))
        {
            var current = new FileInfo(fullPath);
            return new MediaFileFingerprint(fullPath, current.Length, current.LastWriteTimeUtc);
        }

        var lastWrite = source.LastWriteTimeUtc == default
            ? File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : default
            : source.LastWriteTimeUtc;
        return new MediaFileFingerprint(fullPath, source.FileSizeBytes, lastWrite);
    }

    public bool Matches(MediaFileFingerprint other) =>
        string.Equals(NormalizedFullPath, other.NormalizedFullPath, StringComparison.OrdinalIgnoreCase) &&
        FileSizeBytes == other.FileSizeBytes &&
        LastWriteTimeUtc == other.LastWriteTimeUtc;

    public static string NormalizePath(string path) =>
        Path.GetFullPath(path.Trim());
}

public enum MediaStreamType
{
    Video,
    Audio,
    Subtitle,
    Attachment,
    Data,
    Unknown
}

public enum StreamRetentionAction
{
    Copy,
    EncodeAudio,
    ConvertSubtitle,
    Remove
}

/// <summary>
/// Stream-level metadata retained from ffprobe. The complete list is kept in
/// the probe cache so planning never has to guess which secondary streams exist.
/// </summary>
public sealed record MediaStreamInfo(
    int StreamIndex,
    MediaStreamType StreamType,
    string? Codec = null,
    string? Language = null,
    string? Title = null,
    bool Default = false,
    bool Forced = false,
    IReadOnlyDictionary<string, int>? Disposition = null,
    long? Bitrate = null,
    int? Channels = null,
    int? SampleRate = null,
    string? PixelFormat = null,
    int? BitDepth = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? Profile = null,
    string? ColorPrimaries = null,
    string? ColorTransfer = null,
    string? ColorSpace = null,
    string? ColorRange = null,
    IReadOnlyDictionary<string, string>? MasteringDisplayMetadata = null,
    IReadOnlyDictionary<string, string>? ContentLightMetadata = null)
{
    public bool IsAttachedPicture =>
        Disposition?.TryGetValue("attached_pic", out var value) == true && value != 0;

    public int EffectiveBitDepth => BitDepth is > 0
        ? BitDepth.Value
        : BitDepthPolicyResolver.DetectPixelFormatBitDepth(PixelFormat) ?? 8;

    public bool IsHdr =>
        EffectiveBitDepth >= 10 &&
        (string.Equals(ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorTransfer, "hlg", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase));

    public string StreamTypeDisplay => StreamType switch
    {
        MediaStreamType.Video => "视频",
        MediaStreamType.Audio => "音频",
        MediaStreamType.Subtitle => "字幕",
        MediaStreamType.Attachment => "附件",
        MediaStreamType.Data => "数据",
        _ => "未知流"
    };
}

public enum MediaHealthStatus
{
    [System.ComponentModel.Description("未检查")]
    NotChecked,
    [System.ComponentModel.Description("健康")]
    Healthy,
    [System.ComponentModel.Description("警告")]
    Warning,
    [System.ComponentModel.Description("损坏")]
    Corrupt
}

public enum HealthCheckLevel
{
    [System.ComponentModel.Description("关闭")]
    Disabled,
    [System.ComponentModel.Description("快速")]
    Quick,
    [System.ComponentModel.Description("深度")]
    Deep
}

public sealed record MediaHealthCheckResult(
    MediaHealthStatus Status,
    HealthCheckLevel Level,
    string Message,
    DateTimeOffset CheckedAt,
    MediaFileFingerprint Fingerprint,
    bool CacheHit = false)
{
    public bool IsUsable => Status is MediaHealthStatus.Healthy or MediaHealthStatus.Warning;
}

public sealed record StreamRetentionDecision(
    MediaStreamInfo Stream,
    StreamRetentionAction Action,
    string? Reason = null);

public sealed record ContainerCompatibilityAudit(
    string Container,
    IReadOnlyList<StreamRetentionDecision> Decisions,
    IReadOnlyList<string> Warnings,
    bool BlocksExecution)
{
    public IReadOnlyList<MediaStreamInfo> RetainedStreams => Decisions
        .Where(decision => decision.Action != StreamRetentionAction.Remove)
        .Select(decision => decision.Stream)
        .ToArray();

    public IReadOnlyList<MediaStreamInfo> RemovedStreams => Decisions
        .Where(decision => decision.Action == StreamRetentionAction.Remove)
        .Select(decision => decision.Stream)
        .ToArray();

    public IReadOnlyList<MediaStreamInfo> AttachedPictures => RetainedStreams
        .Where(stream => stream.IsAttachedPicture)
        .ToArray();

    public int AttachedPictureCount => AttachedPictures.Count;

    public int Count(MediaStreamType type) => RetainedStreams.Count(stream => stream.StreamType == type);

    public string SummaryDisplay
    {
        get
        {
            var parts = new List<string>();
            AddCount(parts, MediaStreamType.Audio, "音轨");
            AddCount(parts, MediaStreamType.Subtitle, "字幕");
            AddCount(parts, MediaStreamType.Attachment, "附件");
            AddCount(parts, MediaStreamType.Data, "数据流");
            if (AttachedPictureCount > 0)
            {
                parts.Add($"封面图 {AttachedPictureCount} 张 · 保留");
            }
            return parts.Count == 0 ? "无额外流" : string.Join(" · ", parts);
        }
    }

    private void AddCount(ICollection<string> parts, MediaStreamType type, string label)
    {
        var retained = Count(type);
        var removed = Decisions.Count(decision => decision.Stream.StreamType == type && decision.Action == StreamRetentionAction.Remove);
        if (retained > 0 || removed > 0)
        {
            parts.Add(removed == 0 ? $"{label} {retained} 条 · 保留" : $"{label} {retained} 条保留，{removed} 条无法保留");
        }
    }
}
