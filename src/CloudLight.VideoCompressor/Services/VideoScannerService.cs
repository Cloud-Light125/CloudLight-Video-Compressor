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

    public VideoScannerService(FFprobeService ffprobeService) => _ffprobeService = ffprobeService;

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
        Func<ScanProgress, Task>? onProgress = null)
    {
        // Path enumeration supplies a real denominator without probing files twice.
        var paths = new List<string>();
        await foreach (var path in EnumerateVideoPathsAsync(directory, recursive, cancellationToken))
        {
            paths.Add(path);
        }

        var total = paths.Count;
        var completed = 0;
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

                    var info = await _ffprobeService.ProbeAsync(tools, path, cancellationToken);
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
                            await onProgress(new ScanProgress(finished, total, path));
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
        if (onProgress is not null)
        {
            await onProgress(new ScanProgress(completed, total, null, true));
        }
    }
}
