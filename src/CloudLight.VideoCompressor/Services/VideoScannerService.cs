using System.Runtime.CompilerServices;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class VideoScannerService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".mts", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".vob"
    };

    private readonly FFprobeService _ffprobeService;
    private readonly MediaProbeCache _probeCache;
    private readonly MediaHealthCheckService? _healthCheckService;

    public VideoScannerService(
        FFprobeService ffprobeService,
        MediaProbeCache? probeCache = null,
        MediaHealthCheckService? healthCheckService = null)
    {
        _ffprobeService = ffprobeService;
        _probeCache = probeCache ?? new MediaProbeCache();
        _healthCheckService = healthCheckService ?? new MediaHealthCheckService(_probeCache, _ffprobeService);
    }

    public MediaProbeCache ProbeCache => _probeCache;

    public static bool IsSupportedVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public async IAsyncEnumerable<string> EnumerateVideoPathsAsync(
        string directory,
        bool recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupportedVideo(path))
            {
                yield return path;
            }

            await Task.Yield();
        }
    }

    public async Task ScanAsync(
        string directory,
        bool recursive,
        int maximumProbeConcurrency,
        FFmpegTools tools,
        Func<VideoFileInfo, Task> onVideo,
        Func<string, string, Task>? onProbeFailure,
        CancellationToken cancellationToken,
        Func<ScanProgress, Task>? onProgress = null,
        HealthCheckLevel healthCheckLevel = HealthCheckLevel.Quick)
    {
        // Path enumeration supplies a real denominator without probing files twice.
        var paths = new List<string>();
        await foreach (var path in EnumerateVideoPathsAsync(directory, recursive, cancellationToken))
        {
            paths.Add(path);
        }

        var total = paths.Count;
        var completed = 0;
        await _probeCache.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _probeCache.ResetStatistics();
        if (onProgress is not null)
        {
            await onProgress(new ScanProgress(0, total, null));
        }

        var concurrency = Math.Clamp(maximumProbeConcurrency, 1, 8);
        using var semaphore = new SemaphoreSlim(concurrency);
        var pending = new List<Task>();
        foreach (var path in paths)
        {
            await semaphore.WaitAsync(cancellationToken);
            pending.Add(Task.Run(async () =>
            {
                try
                {
                    if (onProgress is not null)
                    {
                        await onProgress(new ScanProgress(Volatile.Read(ref completed), total, path));
                    }

                    var info = (await _probeCache.GetOrProbeAsync(
                        tools,
                        path,
                        _ffprobeService,
                        cancellationToken).ConfigureAwait(false)).Info;
                    if (_healthCheckService is not null && healthCheckLevel != HealthCheckLevel.Disabled)
                    {
                        var health = await _healthCheckService.CheckAsync(
                            tools,
                            info,
                            healthCheckLevel,
                            cancellationToken).ConfigureAwait(false);
                        info = info.WithHealthCheck(health);
                    }
                    await onVideo(info);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (onProbeFailure is not null)
                    {
                        await onProbeFailure(path, exception.Message);
                    }
                }
                finally
                {
                    var finished = Interlocked.Increment(ref completed);
                    try
                    {
                        if (onProgress is not null)
                        {
                            var stats = _probeCache.GetStatistics();
                            await onProgress(new ScanProgress(
                                finished,
                                total,
                                path,
                                CacheHits: stats.CacheHits,
                                ActualProbes: stats.ActualProbes));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
            }, cancellationToken));

            if (pending.Count >= concurrency * 4)
            {
                var complete = await Task.WhenAny(pending);
                pending.Remove(complete);
                await complete;
            }
        }

        await Task.WhenAll(pending);
        _probeCache.PruneStalePaths(paths);
        await _probeCache.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (onProgress is not null)
        {
            var stats = _probeCache.GetStatistics();
            DiagnosticLog.Write(
                "probe-cache",
                $"CacheHit：扫描完成；命中 {stats.CacheHits}；实际 ffprobe {stats.ActualProbes}；缓存条目 {stats.EntryCount}");
            await onProgress(new ScanProgress(
                completed,
                total,
                null,
                true,
                stats.CacheHits,
                stats.ActualProbes));
        }
    }
}
