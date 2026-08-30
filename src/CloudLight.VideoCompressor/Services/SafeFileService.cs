using CloudLight.VideoCompressor.Models;

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
