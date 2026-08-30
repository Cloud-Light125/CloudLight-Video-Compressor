using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class TargetSizeCalculator
{
    // 3% is held back for container/index overhead and bitrate-control variance.
    private const double SafetyFactor = 0.97;
    private const long MinimumVideoBitrateBps = 120_000;

    public TargetSizeCalculation Calculate(string targetSizeText, VideoFileInfo media, AudioMode audioMode, int configuredAudioKbps)
    {
        if (!ValueParser.TryParseFileSize(targetSizeText, out var targetBytes, out var parseError))
        {
            return TargetSizeCalculation.Invalid(parseError);
        }

        if (media.DurationSeconds is not > 0)
        {
            return TargetSizeCalculation.Invalid("目标文件大小模式需要有效的视频时长。请先让 ffprobe 正常读取该文件。");
        }

        var audioTrackCount = Math.Max(0, media.AudioTrackCount);
        var audioBitrate = audioTrackCount == 0
            ? 0
            : audioMode == AudioMode.Aac
                ? (long)configuredAudioKbps * 1_000 * audioTrackCount
                : media.AudioBitrateBps ?? 192_000L * audioTrackCount;
        var targetTotalBitrate = (long)Math.Floor(targetBytes * 8d / media.DurationSeconds.Value);
        var targetVideoBitrate = (long)Math.Floor(targetTotalBitrate * SafetyFactor - audioBitrate);
        if (targetVideoBitrate < MinimumVideoBitrateBps)
        {
            return TargetSizeCalculation.Invalid(
                $"扣除音频和 3% 容器余量后，目标视频码率仅为 {Math.Max(0, targetVideoBitrate) / 1_000d:0} Kbps，低于安全下限 {MinimumVideoBitrateBps / 1_000} Kbps。请提高目标大小或降低音频码率。");
        }

        return new TargetSizeCalculation(
            true,
            null,
            targetBytes,
            targetTotalBitrate,
            audioBitrate,
            targetVideoBitrate,
            targetBytes >= media.FileSizeBytes);
    }
}
