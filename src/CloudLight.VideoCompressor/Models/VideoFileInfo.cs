namespace CloudLight.VideoCompressor.Models;

public sealed class VideoFileInfo
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string Extension { get; init; }
    public long FileSizeBytes { get; init; }
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

    public bool HasProbeData => DurationSeconds is not null || VideoCodec is not null || Width is not null;

    public static VideoFileInfo FromFile(string path)
    {
        var file = new FileInfo(path);
        return new VideoFileInfo
        {
            FileName = file.Name,
            FullPath = file.FullName,
            Extension = file.Extension,
            FileSizeBytes = file.Length
        };
    }

    public VideoFileInfo WithProbeData(VideoFileInfo probe) => new()
    {
        FileName = FileName,
        FullPath = FullPath,
        Extension = Extension,
        FileSizeBytes = FileSizeBytes,
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
        SubtitleCodecs = probe.SubtitleCodecs
    };
}
