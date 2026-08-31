using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Decides which probed streams can be written to the requested container.
/// The policy is intentionally conservative: a stream that cannot be retained
/// is surfaced before encoding instead of being silently dropped by FFmpeg.
/// </summary>
public sealed class ContainerCompatibilityPolicy
{
    public ContainerCompatibilityAudit Audit(VideoFileInfo media, string outputExtension, AudioMode audioMode)
    {
        var container = Normalize(outputExtension);
        var streams = media.Streams;
        var decisions = new List<StreamRetentionDecision>();
        var warnings = new List<string>();
        var blocks = false;
        var regularVideoCount = streams.Count(stream =>
            stream.StreamType == MediaStreamType.Video && !stream.IsAttachedPicture);
        if (regularVideoCount > 1)
        {
            warnings.Add($"检测到 {regularVideoCount} 条普通视频流；当前计划只支持一个主视频流，已阻止执行以避免静默改写或丢失备用视频。");
            blocks = true;
        }

        if (streams.Count == 0)
        {
            var unknownOrIncompatibleAudio = media.AudioTrackCount > 0 && !IsMp4AudioCodec(media.AudioCodec);
            if (container == "mp4" &&
                (media.SubtitleTrackCount > 0 || media.AudioTrackCount > 1 || unknownOrIncompatibleAudio))
            {
                warnings.Add("无法完成 MP4 的完整流审计；为避免静默丢失字幕或多音轨，已阻止执行。请重新探测源文件。");
                blocks = true;
            }

            return new ContainerCompatibilityAudit(container, decisions, warnings, blocks);
        }

        foreach (var stream in streams)
        {
            var decision = EvaluateStream(stream, container, audioMode, out var warning, out var shouldBlock);
            decisions.Add(new StreamRetentionDecision(stream, decision, warning));
            if (!string.IsNullOrWhiteSpace(warning))
            {
                warnings.Add(warning);
            }

            blocks |= shouldBlock;
        }

        var retainedVideo = decisions.Count(item => item.Stream.StreamType == MediaStreamType.Video && item.Action != StreamRetentionAction.Remove);
        if (retainedVideo == 0)
        {
            warnings.Add("目标容器没有可保留的视频流；已阻止执行。");
            blocks = true;
        }
        var retainedCoverArt = decisions.Count(item =>
            item.Stream.StreamType == MediaStreamType.Video &&
            item.Stream.IsAttachedPicture &&
            item.Action != StreamRetentionAction.Remove);
        if (retainedCoverArt > 0 && container != "mp4")
        {
            warnings.Add($"检测到 {retainedCoverArt} 张封面图视频流，计划会按 attached_pic 原流复制并在输出后复核。");
        }

        return new ContainerCompatibilityAudit(container, decisions, warnings, blocks);
    }

    private static StreamRetentionAction EvaluateStream(
        MediaStreamInfo stream,
        string container,
        AudioMode audioMode,
        out string? warning,
        out bool blocks)
    {
        warning = null;
        blocks = false;

        if (stream.StreamType == MediaStreamType.Audio)
        {
            if (audioMode == AudioMode.Aac)
            {
                return StreamRetentionAction.EncodeAudio;
            }

            if (container == "mp4" && !IsMp4AudioCodec(stream.Codec))
            {
                warning = $"音轨 {stream.StreamIndex}（{stream.Codec ?? "未知编码"}）不能在 MP4 中按“复制音频”安全保留；已阻止执行，请选择 AAC 或 MKV。";
                blocks = true;
            }

            return StreamRetentionAction.Copy;
        }

        if (stream.StreamType == MediaStreamType.Subtitle)
        {
            if (container != "mp4" || IsMp4TextSubtitle(stream.Codec))
            {
                return StreamRetentionAction.Copy;
            }

            if (IsConvertibleToMovText(stream.Codec))
            {
                warning = $"字幕流 {stream.StreamIndex}（{stream.Codec ?? "未知格式"}）将转换为 mov_text 以写入 MP4。";
                return StreamRetentionAction.ConvertSubtitle;
            }

            warning = $"目标 MP4 不支持安全保留字幕流 {stream.StreamIndex}（{stream.Codec ?? "未知格式"}）；已阻止执行，避免静默丢字幕。";
            blocks = true;
            return StreamRetentionAction.Remove;
        }

        if (stream.StreamType == MediaStreamType.Attachment)
        {
            if (container == "mp4")
            {
                warning = $"目标 MP4 容器不支持附件流，流 {stream.StreamIndex} 将无法写入；已阻止执行，请选择 MKV。";
                blocks = true;
                return StreamRetentionAction.Remove;
            }

            return StreamRetentionAction.Copy;
        }

        if (stream.StreamType == MediaStreamType.Video && stream.IsAttachedPicture)
        {
            if (container == "mp4" && IsMp4AttachedPictureCodec(stream.Codec))
            {
                warning = $"检测到 MP4 封面图视频流 {stream.StreamIndex}（{stream.Codec}），计划按原编码复制并在输出后复核 attached_pic。";
                return StreamRetentionAction.Copy;
            }

            warning = container == "mp4"
                ? $"目标 MP4 不能可靠保留封面图视频流 {stream.StreamIndex}（{stream.Codec ?? "未知编码"}）；已阻止执行，避免静默丢失封面图。"
                : $"当前输出容器 {container.ToUpperInvariant()} 不能在本编码路径可靠保留 attached_pic 视频流 {stream.StreamIndex}；已阻止执行，避免静默丢失封面图。"
                  + " 请输出为 MP4 或先提取封面图。";
            blocks = true;
            return StreamRetentionAction.Remove;
        }

        if (stream.StreamType == MediaStreamType.Data)
        {
            if (container == "mp4")
            {
                warning = $"目标 MP4 容器不支持数据流，流 {stream.StreamIndex} 将无法写入；已阻止执行，请选择 MKV。";
                blocks = true;
                return StreamRetentionAction.Remove;
            }

            return StreamRetentionAction.Copy;
        }

        if (stream.StreamType == MediaStreamType.Unknown)
        {
            warning = $"无法识别流 {stream.StreamIndex}（{stream.Codec ?? "未知编码"}）；已阻止执行，避免静默丢失媒体内容。";
            blocks = true;
            return StreamRetentionAction.Remove;
        }

        // All video streams, including attached pictures, remain mapped. The
        // primary video is encoded by the plan; attached pictures are copied
        // by CompressionPlan.BuildArguments.
        return StreamRetentionAction.Copy;
    }

    private static bool IsMp4AudioCodec(string? codec) => Normalize(codec) is
        "aac" or "mp3" or "ac3" or "eac3" or "alac";

    private static bool IsMp4TextSubtitle(string? codec) => Normalize(codec) is "mov_text" or "tx3g";

    private static bool IsConvertibleToMovText(string? codec) => Normalize(codec) is
        "subrip" or "srt" or "webvtt" or "text";

    private static bool IsMp4AttachedPictureCodec(string? codec) => Normalize(codec) is
        "mjpeg" or "jpeg" or "png";

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "m4v" or "mov" => "mp4",
            "matroska" => "mkv",
            var normalized => normalized
        };
}
