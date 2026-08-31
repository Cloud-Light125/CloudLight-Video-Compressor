using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Infrastructure;

namespace CloudLight.VideoCompressor.Services;

public sealed record OutputValidationResult(bool IsValid, string? Error, VideoFileInfo? OutputInfo);
public sealed record OriginalMoveResult(string SourcePath, string DestinationPath);
public sealed record OriginalMoveRollbackResult(bool Succeeded, string? Error)
{
    public static OriginalMoveRollbackResult NotRequired { get; } = new(true, null);
}

public sealed class SafeFileService
{
    private readonly FFprobeService _ffprobeService;

    public SafeFileService(FFprobeService ffprobeService) => _ffprobeService = ffprobeService;

    public string CreateTemporaryOutputPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("输出目录无效。");
        var extension = Path.GetExtension(finalPath);
        var stem = Path.GetFileNameWithoutExtension(finalPath);
        return Path.Combine(directory, $".{stem}.clvc-{Guid.NewGuid():N}{extension}");
    }

    public async Task<OutputValidationResult> ValidateOutputAsync(FFmpegTools tools, VideoFileInfo source, string temporaryPath, CancellationToken cancellationToken)
        => await ValidateOutputAsync(tools, source, temporaryPath, plan: null, cancellationToken).ConfigureAwait(false);

    public async Task<OutputValidationResult> ValidateOutputAsync(
        FFmpegTools tools,
        VideoFileInfo source,
        string temporaryPath,
        CompressionPlan? plan,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(temporaryPath))
        {
            return new OutputValidationResult(false, "FFmpeg 未产生输出文件。", null);
        }

        var file = new FileInfo(temporaryPath);
        if (file.Length < 1_024)
        {
            return new OutputValidationResult(false, "输出文件异常小，拒绝替换或移动源文件。", null);
        }

        try
        {
            var output = await _ffprobeService.ProbeAsync(tools, temporaryPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(output.VideoCodec))
            {
                return new OutputValidationResult(false, "输出文件中未检测到视频流。", output);
            }

            if (plan is not null)
            {
                var outputCodec = NormalizeCodec(output.VideoCodec);
                if (outputCodec is null || outputCodec != plan.EffectiveTargetCodec)
                {
                    return new OutputValidationResult(
                        false,
                        $"输出视频编码为 {output.VideoCodec ?? "未知"}，与计划的 {plan.EffectiveTargetCodec.GetDescription()} 不符。",
                        output);
                }

                if (plan.InputInfo?.Width is { } sourceWidth && plan.InputInfo.Height is { } sourceHeight &&
                    output.Width is { } outputWidth && output.Height is { } outputHeight)
                {
                    if (plan.ResolutionLimit is { } limit && (outputWidth > limit.Width || outputHeight > limit.Height))
                    {
                        return new OutputValidationResult(false, "输出分辨率超过计划上限。", output);
                    }
                    if (plan.ResolutionLimit is null && (outputWidth != sourceWidth || outputHeight != sourceHeight))
                    {
                        return new OutputValidationResult(false, "输出分辨率与计划不符。", output);
                    }
                }

                if (plan.InputInfo?.FrameRate is > 0 && output.FrameRate is > 0)
                {
                    if (plan.FpsLimit is { } fpsLimit && output.FrameRate > fpsLimit + 0.5)
                    {
                        return new OutputValidationResult(false, "输出 FPS 超过计划上限。", output);
                    }
                    if (plan.FpsLimit is null && Math.Abs(output.FrameRate.Value - plan.InputInfo.FrameRate.Value) > 1.0)
                    {
                        return new OutputValidationResult(false, "输出 FPS 与计划不符。", output);
                    }
                }

                if (plan.TargetBitDepth >= 10)
                {
                    if (output.BitDepth is not >= 10)
                    {
                        return new OutputValidationResult(
                            false,
                            $"输出位深为 {output.BitDepth?.ToString() ?? "未知"}，低于计划的 {plan.TargetBitDepth}-bit，拒绝提交以保护色阶细节。",
                            output);
                    }

                    if (plan.EffectiveTargetCodec == VideoCodecKind.H265 &&
                        (string.IsNullOrWhiteSpace(output.VideoProfile) ||
                         (!output.VideoProfile.Contains("main 10", StringComparison.OrdinalIgnoreCase) &&
                          !output.VideoProfile.Contains("main10", StringComparison.OrdinalIgnoreCase))))
                    {
                        return new OutputValidationResult(false, "输出 HEVC profile 不是 Main10，拒绝提交以避免 10-bit 计划被错误编码。", output);
                    }
                }

                if (plan.IsHdrSource && plan.TargetBitDepth >= 10)
                {
                    var sourceHdr = plan.InputInfo!;
                    if (!output.IsHdr)
                    {
                        return new OutputValidationResult(false, "源文件为 HDR，但输出未保留可识别的 HDR/BT.2020 信息，拒绝提交。", output);
                    }
                    if (!TagsMatch(sourceHdr.ColorPrimaries, output.ColorPrimaries) ||
                        !TagsMatch(sourceHdr.ColorTransfer, output.ColorTransfer) ||
                        !TagsMatch(sourceHdr.ColorSpace, output.ColorSpace))
                    {
                        return new OutputValidationResult(false, "输出 HDR 色彩原色、传输特性或色彩空间与源文件不符，拒绝提交。", output);
                    }
                    if (sourceHdr.MasteringDisplayMetadata.Count > 0 &&
                        output.MasteringDisplayMetadata.Count == 0)
                    {
                        return new OutputValidationResult(false, "源文件包含 mastering display metadata，但输出未检测到，拒绝提交。", output);
                    }
                    if (sourceHdr.ContentLightMetadata.Count > 0 &&
                        output.ContentLightMetadata.Count == 0)
                    {
                        return new OutputValidationResult(false, "源文件包含 content light level metadata，但输出未检测到，拒绝提交。", output);
                    }
                }

                var plannedSource = plan.InputInfo;
                if (plannedSource is { HasProbeData: true } && output.AudioTrackCount != plannedSource.AudioTrackCount)
                {
                    return new OutputValidationResult(false, "输出音轨数量与源文件不符。", output);
                }

                if (plan.StreamAudit is { Decisions.Count: > 0 } audit && output.Streams.Count > 0)
                {
                    foreach (var group in audit.Decisions
                                 .Where(decision => decision.Action != StreamRetentionAction.Remove)
                                 .GroupBy(decision => decision.Stream.StreamType))
                    {
                        var expected = group.Count();
                        var actual = output.Streams.Count(stream => stream.StreamType == group.Key);
                        if (actual < expected)
                        {
                            return new OutputValidationResult(
                                false,
                                $"输出文件缺少应保留的{group.Key switch
                                {
                                    MediaStreamType.Audio => "音轨",
                                    MediaStreamType.Subtitle => "字幕",
                                    MediaStreamType.Attachment => "附件",
                                    MediaStreamType.Data => "数据流",
                                    _ => "媒体流"
                                }}：计划 {expected} 条，实际 {actual} 条。",
                                output);
                        }
                    }

                    var expectedCoverArt = audit.Decisions.Count(decision =>
                        decision.Action != StreamRetentionAction.Remove &&
                        decision.Stream.StreamType == MediaStreamType.Video &&
                        decision.Stream.IsAttachedPicture);
                    var actualCoverArt = output.Streams.Count(stream =>
                        stream.StreamType == MediaStreamType.Video &&
                        stream.IsAttachedPicture);
                    if (actualCoverArt != expectedCoverArt)
                    {
                        return new OutputValidationResult(
                            false,
                            $"输出封面图数量为 {actualCoverArt}，计划保留 {expectedCoverArt} 张；拒绝提交以避免封面图丢失。",
                            output);
                    }

                    foreach (var group in audit.Decisions
                                 .Where(decision => decision.Action != StreamRetentionAction.Remove)
                                 .GroupBy(decision => decision.Stream.StreamType))
                    {
                        var expectedStreams = group.Select(decision => decision.Stream).ToArray();
                        var actualStreams = output.Streams
                            .Where(stream => stream.StreamType == group.Key)
                            .ToArray();
                        for (var index = 0; index < expectedStreams.Length && index < actualStreams.Length; index++)
                        {
                            var expected = expectedStreams[index];
                            var actual = actualStreams[index];
                            if (!TagsMatch(expected.Language, actual.Language) ||
                                !TagsMatch(expected.Title, actual.Title) ||
                                expected.Default != actual.Default ||
                                expected.Forced != actual.Forced ||
                                expected.IsAttachedPicture != actual.IsAttachedPicture)
                            {
                                return new OutputValidationResult(
                                    false,
                                    $"输出的{expected.StreamTypeDisplay} {index + 1} 条语言、标题或 default/forced 标记与源文件不符，拒绝提交。",
                                    output);
                            }

                            foreach (var metadata in expected.Metadata ?? new Dictionary<string, string>())
                            {
                                if (actual.Metadata is null ||
                                    !actual.Metadata.TryGetValue(metadata.Key, out var actualValue) ||
                                    !string.Equals(metadata.Value, actualValue, StringComparison.Ordinal))
                                {
                                    return new OutputValidationResult(
                                        false,
                                        $"输出的{expected.StreamTypeDisplay} {index + 1} 条 stream metadata“{metadata.Key}”与源文件不符，拒绝提交。",
                                        output);
                                }
                            }
                        }
                    }

                    if (plannedSource is { HasProbeData: true } && plannedSource.ChapterCount > 0 && output.ChapterCount < plannedSource.ChapterCount)
                    {
                        return new OutputValidationResult(false, "输出文件缺少源文件章节，拒绝提交以避免静默丢失章节。", output);
                    }

                    var sourceTaggedStreams = (plannedSource?.Streams ?? Array.Empty<MediaStreamInfo>())
                        .Where(stream => !string.IsNullOrWhiteSpace(stream.Language) || !string.IsNullOrWhiteSpace(stream.Title))
                        .ToArray();
                    var outputTaggedStreams = output.Streams
                        .Where(stream => !string.IsNullOrWhiteSpace(stream.Language) || !string.IsNullOrWhiteSpace(stream.Title))
                        .ToArray();
                    if (outputTaggedStreams.Length < sourceTaggedStreams.Length)
                    {
                        return new OutputValidationResult(false, "输出文件的流语言或标题 metadata 少于源文件，拒绝提交以保护流信息。", output);
                    }
                }
            }

            var sourceInfo = source.DurationSeconds is > 0 ? source : await _ffprobeService.ProbeAsync(tools, source.FullPath, cancellationToken);
            if (sourceInfo.DurationSeconds is not > 0)
            {
                return new OutputValidationResult(false, "无法读取源文件有效时长，拒绝提交压缩结果以保护原文件。", output);
            }
            if (output.DurationSeconds is not > 0)
            {
                return new OutputValidationResult(false, "无法读取输出文件有效时长，拒绝提交压缩结果以保护原文件。", output);
            }

            var allowedDifference = Math.Max(2, sourceInfo.DurationSeconds.Value * 0.03);
            if (Math.Abs(sourceInfo.DurationSeconds.Value - output.DurationSeconds.Value) > allowedDifference)
            {
                return new OutputValidationResult(false, $"输出时长与源文件相差超过允许范围（{allowedDifference:0.0} 秒）。", output);
            }

            return new OutputValidationResult(true, null, output);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OutputValidationResult(false, $"无法验证输出文件：{exception.Message}", null);
        }
    }

    private static VideoCodecKind? NormalizeCodec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "h264" or "avc" => VideoCodecKind.H264,
        "hevc" or "h265" or "x265" => VideoCodecKind.H265,
        "av1" => VideoCodecKind.Av1,
        _ => null
    };

    private static bool TagsMatch(string? expected, string? actual) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    public OriginalMoveResult MoveOriginal(VideoFileInfo source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            throw new IOException("原文件移动目标已存在，已取消移动以避免覆盖。");
        }

        File.Move(source.FullPath, destination);
        return new OriginalMoveResult(source.FullPath, destination);
    }

    public void FinalizeTemporaryOutput(string temporaryPath, string finalPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath))
        {
            throw new IOException("最终输出文件已存在，已取消写入以避免覆盖。");
        }

        File.Move(temporaryPath, finalPath);
    }

    public OriginalMoveRollbackResult TryRollbackOriginalMove(OriginalMoveResult? move)
    {
        if (move is null)
        {
            return OriginalMoveRollbackResult.NotRequired;
        }
        if (!File.Exists(move.DestinationPath))
        {
            return new OriginalMoveRollbackResult(false, $"找不到已移动的原文件：{move.DestinationPath}");
        }
        if (File.Exists(move.SourcePath))
        {
            return new OriginalMoveRollbackResult(false, $"源路径已被重新占用；原文件仍安全保留在：{move.DestinationPath}");
        }

        try
        {
            File.Move(move.DestinationPath, move.SourcePath);
            return new OriginalMoveRollbackResult(true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OriginalMoveRollbackResult(false, $"自动回滚失败；原文件仍安全保留在：{move.DestinationPath}。原因：{exception.Message}");
        }
    }

    public void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void DeletePassLogs(string? passLogPrefix)
    {
        if (string.IsNullOrWhiteSpace(passLogPrefix))
        {
            return;
        }

        var directory = Path.GetDirectoryName(passLogPrefix);
        var name = Path.GetFileName(passLogPrefix);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, $"{name}*"))
            {
                DeleteTemporaryFile(file);
            }
        }
        catch (IOException)
        {
            // Temporary pass logs are best-effort cleanup and must not change an already committed job result.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary pass logs are best-effort cleanup and must not change an already committed job result.
        }
    }
}
