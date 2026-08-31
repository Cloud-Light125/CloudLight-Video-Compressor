using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Performs the inexpensive probe-based health check by default and an actual
/// decoder pass only when the user selects Deep. Deep results are stored beside
/// the probe entry and are valid only for the same file fingerprint.
/// </summary>
public sealed class MediaHealthCheckService
{
    private readonly MediaProbeCache _probeCache;
    private readonly FFprobeService _ffprobeService;
    private readonly FFmpegService _ffmpegService;

    public MediaHealthCheckService(
        MediaProbeCache probeCache,
        FFprobeService ffprobeService,
        FFmpegService? ffmpegService = null)
    {
        _probeCache = probeCache;
        _ffprobeService = ffprobeService;
        _ffmpegService = ffmpegService ?? new FFmpegService();
    }

    public async Task<MediaHealthCheckResult> CheckAsync(
        FFmpegTools tools,
        VideoFileInfo source,
        HealthCheckLevel level,
        CancellationToken cancellationToken)
    {
        var fingerprint = GetCurrentFingerprint(source);
        if (level == HealthCheckLevel.Disabled)
        {
            return new MediaHealthCheckResult(
                MediaHealthStatus.NotChecked,
                HealthCheckLevel.Disabled,
                "健康检查已关闭。",
                DateTimeOffset.UtcNow,
                fingerprint);
        }

        var toolVersion = await GetToolVersionSafeAsync(tools, cancellationToken).ConfigureAwait(false);
        await _probeCache.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (_probeCache.TryGetCachedHealth(fingerprint, toolVersion, level, out var cachedHealth))
        {
            DiagnosticLog.Write(
                "health-check",
                $"HealthCheck CacheHit：{fingerprint.NormalizedFullPath}；级别：{cachedHealth.Level}；状态：{cachedHealth.Status}");
            return cachedHealth;
        }

        var sourceIdentity = new MediaFileFingerprint(
            MediaFileFingerprint.NormalizePath(source.FullPath),
            source.FileSizeBytes,
            source.LastWriteTimeUtc);
        var probeInfo = source;
        if (!source.HasProbeData || !sourceIdentity.Matches(fingerprint))
        {
            try
            {
                probeInfo = (await _probeCache.GetOrProbeAsync(
                    tools,
                    source.FullPath,
                    _ffprobeService,
                    cancellationToken).ConfigureAwait(false)).Info;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failedProbe = new MediaHealthCheckResult(
                    MediaHealthStatus.Corrupt,
                    level,
                    $"健康检查无法读取媒体：{TrimError(exception.Message)}",
                    DateTimeOffset.UtcNow,
                    fingerprint);
                _probeCache.SetHealth(fingerprint, toolVersion, source, failedProbe);
                return failedProbe;
            }
            fingerprint = MediaFileFingerprint.FromVideoInfo(probeInfo);
        }

        var quick = ValidateQuickProbe(probeInfo, fingerprint);
        if (!quick.IsUsable || level == HealthCheckLevel.Quick)
        {
            _probeCache.SetHealth(fingerprint, toolVersion, probeInfo, quick);
            DiagnosticLog.Write(
                "health-check",
                $"HealthCheck：{fingerprint.NormalizedFullPath}；级别：{quick.Level}；状态：{quick.Status}");
            return quick;
        }

        var decoderArguments = BuildDeepCheckArguments(probeInfo);
        FFmpegRunResult decode;
        try
        {
            decode = await _ffmpegService.RunAsync(
                tools,
                decoderArguments,
                probeInfo.DurationSeconds,
                progress: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            decode = new FFmpegRunResult(false, -1, exception.Message, CompressionFailureKind.SourceCorrupt);
        }

        var result = decode.Succeeded
            ? new MediaHealthCheckResult(
                MediaHealthStatus.Healthy,
                HealthCheckLevel.Deep,
                "视频和音频流已完成实际解码检查。",
                DateTimeOffset.UtcNow,
                fingerprint)
            : new MediaHealthCheckResult(
                MediaHealthStatus.Corrupt,
                HealthCheckLevel.Deep,
                $"检测到视频解码错误，不建议直接压缩。{TrimError(decode.ErrorOutput)}",
                DateTimeOffset.UtcNow,
                fingerprint);
        _probeCache.SetHealth(fingerprint, toolVersion, probeInfo, result);
        DiagnosticLog.Write(
            "health-check",
            $"HealthCheck：{fingerprint.NormalizedFullPath}；级别：{result.Level}；状态：{result.Status}");
        return result;
    }

    private static MediaHealthCheckResult ValidateQuickProbe(
        VideoFileInfo source,
        MediaFileFingerprint fingerprint)
    {
        var videoStreams = source.Streams.Count == 0
            ? source.VideoCodec is null ? 0 : 1
            : source.Streams.Count(stream => stream.StreamType == MediaStreamType.Video && !stream.IsAttachedPicture);
        var errors = new List<string>();
        if (!source.HasProbeData || videoStreams == 0 || string.IsNullOrWhiteSpace(source.VideoCodec))
        {
            errors.Add("没有有效的视频流");
        }

        if (source.DurationSeconds is not > 0)
        {
            errors.Add("时长无效");
        }

        if (source.Width is not (>= 2 and <= 16_384) || source.Height is not (>= 2 and <= 16_384))
        {
            errors.Add("分辨率无效");
        }

        return errors.Count == 0
            ? new MediaHealthCheckResult(
                MediaHealthStatus.Healthy,
                HealthCheckLevel.Quick,
                "ffprobe 可读取，视频流、时长和分辨率有效。",
                DateTimeOffset.UtcNow,
                fingerprint)
            : new MediaHealthCheckResult(
                MediaHealthStatus.Corrupt,
                HealthCheckLevel.Quick,
                $"快速健康检查失败：{string.Join("、", errors)}。",
                DateTimeOffset.UtcNow,
                fingerprint);
    }

    private static IReadOnlyList<string> BuildDeepCheckArguments(VideoFileInfo source)
    {
        var arguments = new List<string> { "-v", "error", "-i", source.FullPath };
        if (source.Streams.Count > 0)
        {
            arguments.AddRange(["-map", "0:v?", "-map", "0:a?", "-sn", "-dn"]);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0", "-map", "0:a?", "-sn", "-dn"]);
        }

        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }

    private async Task<string?> GetToolVersionSafeAsync(FFmpegTools tools, CancellationToken cancellationToken)
    {
        try
        {
            return await _ffprobeService.GetToolVersionAsync(tools, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("health-check", $"无法读取 ffprobe 版本：{exception.Message}");
            return null;
        }
    }

    private static MediaFileFingerprint GetCurrentFingerprint(VideoFileInfo source)
    {
        try
        {
            return MediaFileFingerprint.FromFile(source.FullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return MediaFileFingerprint.FromVideoInfo(source);
        }
    }

    private static string TrimError(string? error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "FFmpeg 未返回详细错误。" : error.Trim();
        return value.Length <= 1_000 ? value : value[^1_000..];
    }
}
