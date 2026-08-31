using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Maps the four product-level tuning choices to the vocabulary accepted by
/// each encoder in the bundled FFmpeg build. The mapping is intentionally
/// encoder-specific; x264's "slow" is not a valid QSV or NVENC preset.
/// </summary>
public static class EncoderTuningCatalog
{
    public static IReadOnlyList<EncoderTuningPreset> Presets { get; } =
        Enum.GetValues<EncoderTuningPreset>();

    public static string Resolve(
        VideoEncoder encoder,
        EncoderTuningPreset tuning,
        string? legacyPreset = null)
    {
        // Keep a non-default 1.2.0 EncodingPreset working when an old settings
        // file is loaded. New selections always use the product-level mapping.
        if (tuning == EncoderTuningPreset.Balanced && IsKnownLegacyPreset(encoder, legacyPreset) &&
            !string.Equals(legacyPreset, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return legacyPreset!.Trim();
        }

        return encoder switch
        {
            VideoEncoder.Libx264 or VideoEncoder.Libx265 => tuning switch
            {
                EncoderTuningPreset.HighQuality => "slow",
                EncoderTuningPreset.Fast => "fast",
                EncoderTuningPreset.VeryFast => "veryfast",
                _ => "medium"
            },
            VideoEncoder.H264Qsv or VideoEncoder.HevcQsv => tuning switch
            {
                EncoderTuningPreset.HighQuality => "1",
                EncoderTuningPreset.Fast => "5",
                EncoderTuningPreset.VeryFast => "7",
                _ => "3"
            },
            VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc => tuning switch
            {
                EncoderTuningPreset.HighQuality => "p7",
                EncoderTuningPreset.Fast => "p2",
                EncoderTuningPreset.VeryFast => "p1",
                _ => "p4"
            },
            VideoEncoder.H264Amf or VideoEncoder.HevcAmf => tuning == EncoderTuningPreset.HighQuality
                ? "high_quality"
                : "quality",
            VideoEncoder.LibsvtAv1 => tuning switch
            {
                EncoderTuningPreset.HighQuality => "4",
                EncoderTuningPreset.Fast => "10",
                EncoderTuningPreset.VeryFast => "13",
                _ => "6"
            },
            _ => "medium"
        };
    }

    public static string Display(EncoderTuningPreset preset) => preset switch
    {
        EncoderTuningPreset.HighQuality => "高质量",
        EncoderTuningPreset.Fast => "快速",
        EncoderTuningPreset.VeryFast => "极速",
        _ => "平衡"
    };

    private static bool IsKnownLegacyPreset(VideoEncoder encoder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return encoder switch
        {
            VideoEncoder.H264Qsv or VideoEncoder.HevcQsv => normalized is "1" or "2" or "3" or "4" or "5" or "6" or "7",
            VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc => normalized is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7",
            VideoEncoder.H264Amf or VideoEncoder.HevcAmf => normalized is "quality" or "high_quality" or "2" or "3",
            VideoEncoder.LibsvtAv1 => normalized is "0" or "2" or "4" or "6" or "8" or "10" or "12" or "13",
            _ => normalized is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow"
        };
    }
}
