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
