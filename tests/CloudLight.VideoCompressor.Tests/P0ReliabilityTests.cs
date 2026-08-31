using System.Diagnostics;
using System.Text.Json;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class ProbeCacheTests
{
    [Fact]
    public async Task CacheHitRequiresSamePathSizeAndLastWriteTime()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        using var cache = new MediaProbeCache(Path.Combine(directory.Path, "media-probe-cache.json"));
        var probes = 0;

        var first = await cache.GetOrProbeAsync(source, "9.0.1", _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(ProbeInfo(source));
        }, CancellationToken.None);
        var second = await cache.GetOrProbeAsync(source, "9.0.1", _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(ProbeInfo(source));
        }, CancellationToken.None);

        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, probes);

        await File.AppendAllTextAsync(source, "changed");
        var sizeChanged = await cache.GetOrProbeAsync(source, "9.0.1", _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(ProbeInfo(source));
        }, CancellationToken.None);
        Assert.False(sizeChanged.CacheHit);

        var changedTime = File.GetLastWriteTimeUtc(source).AddMinutes(1);
        File.SetLastWriteTimeUtc(source, changedTime);
        var timeChanged = await cache.GetOrProbeAsync(source, "9.0.1", _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(ProbeInfo(source));
        }, CancellationToken.None);
        Assert.False(timeChanged.CacheHit);
        Assert.Equal(3, probes);
    }

    [Fact]
    public async Task CachePersistsAtomicallyAndRecoversFromSchemaOrCorruption()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "movie.mkv");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var cachePath = Path.Combine(directory.Path, "media-probe-cache.json");
        var cache = new MediaProbeCache(cachePath);
        await cache.GetOrProbeAsync(source, "9.0.1", _ => Task.FromResult(ProbeInfo(source)), CancellationToken.None);
        await cache.FlushAsync();
        Assert.True(File.Exists(cachePath));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));

        var persisted = await File.ReadAllTextAsync(cachePath);
        await File.WriteAllTextAsync(cachePath, persisted.Replace("\"CacheSchemaVersion\": 1", "\"CacheSchemaVersion\": 99", StringComparison.Ordinal));
        using (var schemaChanged = new MediaProbeCache(cachePath))
        {
            var result = await schemaChanged.GetOrProbeAsync(source, "9.0.1", _ => Task.FromResult(ProbeInfo(source)), CancellationToken.None);
            Assert.False(result.CacheHit);
        }

        await File.WriteAllTextAsync(cachePath, "{ this is not valid json");
        using var corrupted = new MediaProbeCache(cachePath);
        var recovered = await corrupted.GetOrProbeAsync(source, "9.0.1", _ => Task.FromResult(ProbeInfo(source)), CancellationToken.None);
        Assert.False(recovered.CacheHit);
        Assert.Contains("损坏", corrupted.LastLoadWarning);
    }

    [Fact]
    public async Task ConcurrentRequestsProbeOneTimePerFingerprint()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        using var cache = new MediaProbeCache(Path.Combine(directory.Path, "cache.json"));
        var probes = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            cache.GetOrProbeAsync(source, "9.0.1", async _ =>
            {
                Interlocked.Increment(ref probes);
                await Task.Delay(10);
                return ProbeInfo(source);
            }, CancellationToken.None)));

        Assert.Equal(1, probes);
        Assert.Single(results.Where(result => !result.CacheHit));
        Assert.Equal(7, results.Count(result => result.CacheHit));
    }

    private static VideoFileInfo ProbeInfo(string path) => new()
    {
        FileName = Path.GetFileName(path),
        FullPath = Path.GetFullPath(path),
        Extension = Path.GetExtension(path),
        FileSizeBytes = new FileInfo(path).Length,
        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
        DurationSeconds = 10,
        VideoCodec = "h264",
        VideoBitrateBps = 1_000_000,
        TotalBitrateBps = 1_100_000,
        Width = 320,
        Height = 180,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrateBps = 100_000,
        AudioTrackCount = 1,
        Container = "matroska"
    };
}

public sealed class CompressionResultCacheTests
{
    [Fact]
    public async Task RelevantSettingsAndSourceFingerprintChangeCacheKey()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var source = ProbeCacheTestsHelper.ProbeInfo(sourcePath);
        var settings = new AppSettings { CompressionMode = CompressionMode.SmartAutomatic };
        var decision = new SmartCompressionDecision(
            true,
            "compress",
            SmartCompressionPreset.Balanced,
            VideoCodecKind.H265,
            VideoEncoder.Libx265,
            1_000_000,
            1_200_000,
            128_000,
            320,
            180,
            30,
            SmartRateControlMode.AverageWithPeak,
            100_000,
            0.5,
            "explanation");
        var path = Path.Combine(directory.Path, "result-cache.json");

        using (var cache = new CompressionResultCache(path))
        {
            cache.Set(source, settings, decision);
            await cache.FlushAsync();
        }

        using var reloaded = new CompressionResultCache(path);
        Assert.True(reloaded.TryGet(source, settings, out var entry));
        Assert.Equal(VideoCodecKind.H265, entry.RecommendedCodec);
        Assert.True(entry.ShouldCompress);

        var changedSettings = settings.Clone();
        changedSettings.VmafTarget = 96;
        Assert.False(reloaded.TryGet(source, changedSettings, out _));

        await File.AppendAllTextAsync(sourcePath, "x");
        var changedSource = ProbeCacheTestsHelper.ProbeInfo(sourcePath);
        Assert.False(reloaded.TryGet(changedSource, settings, out _));
    }

    [Fact]
    public async Task CorruptResultCacheIsIgnored()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "result-cache.json");
        await File.WriteAllTextAsync(path, "not-json");
        using var cache = new CompressionResultCache(path);
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1]);
        Assert.False(cache.TryGet(ProbeCacheTestsHelper.ProbeInfo(sourcePath), new AppSettings(), out _));
        Assert.Contains("损坏", cache.LastLoadWarning);
    }
}

public sealed class HealthCheckTests
{
    [Fact]
    public async Task QuickHealthCheckValidatesProbeFieldsAndCachesDeepResults()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var source = ProbeCacheTestsHelper.ProbeInfo(sourcePath);
        var probeCache = new MediaProbeCache(Path.Combine(directory.Path, "probe.json"));
        var service = new MediaHealthCheckService(probeCache, new FFprobeService());
        var invalid = ProbeCacheTestsHelper.Clone(source, duration: 0, width: 0);

        var invalidResult = await service.CheckAsync(
            new FFmpegTools("missing-ffmpeg.exe", "missing-ffprobe.exe"),
            invalid,
            HealthCheckLevel.Quick,
            CancellationToken.None);
        Assert.Equal(MediaHealthStatus.Corrupt, invalidResult.Status);

        var fingerprint = MediaFileFingerprint.FromFile(sourcePath);
        var cachedHealth = new MediaHealthCheckResult(
            MediaHealthStatus.Healthy,
            HealthCheckLevel.Deep,
            "deep passed",
            DateTimeOffset.UtcNow,
            fingerprint);
        probeCache.SetHealth(fingerprint, null, source, cachedHealth);
        var cached = await service.CheckAsync(
            new FFmpegTools("missing-ffmpeg.exe", "missing-ffprobe.exe"),
            source,
            HealthCheckLevel.Deep,
            CancellationToken.None);
        Assert.True(cached.CacheHit);
        Assert.Equal(MediaHealthStatus.Healthy, cached.Status);
    }
}

public sealed class StreamPreservationTests
{
    [Fact]
    public void PlannerMapsAllRetainedStreamsAndWarnsBeforeIncompatibleMp4()
    {
        var source = StreamMedia();
        var mkvSettings = new AppSettings
        {
            OutputContainer = OutputContainerMode.Mkv,
            VideoEncoder = VideoEncoder.Libx264,
            AudioMode = AudioMode.Copy
        };
        var mkv = new CompressionPlanner().CreatePlan(source, mkvSettings);
        var mkvArguments = mkv.BuildArguments(source.FullPath, "output.mkv", false);
        Assert.Equal(".mkv", mkv.OutputExtension);
        Assert.Equal(5, mkv.StreamAudit!.RetainedStreams.Count);
        Assert.Equal(5, mkvArguments.Count(argument => argument == "-map"));
        Assert.Contains("0:0", mkvArguments);
        Assert.Contains("0:4", mkvArguments);
        Assert.Contains("-map_chapters", mkvArguments);
        Assert.Contains("-map_metadata", mkvArguments);
        Assert.Contains("copy", mkvArguments);
        Assert.Contains("language=eng", mkvArguments);

        var mp4Settings = mkvSettings.Clone();
        mp4Settings.OutputContainer = OutputContainerMode.Mp4;
        var mp4 = new CompressionPlanner().CreatePlan(source, mp4Settings);
        Assert.True(mp4.StreamAudit!.BlocksExecution);
        Assert.Contains(mp4.Warnings, warning => warning.Contains("附件", StringComparison.Ordinal));
        Assert.Contains(mp4.Warnings, warning => warning.Contains("字幕", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeParserRetainsStreamTagsDispositionChaptersAndContainer()
    {
        var json = """
        {
          "format": {"format_name":"matroska,webm","duration":"12.5","bit_rate":"1000000","tags":{"title":"Movie"}},
          "streams": [
            {"index":0,"codec_type":"video","codec_name":"h264","width":320,"height":180,"avg_frame_rate":"30/1","pix_fmt":"yuv420p","bits_per_raw_sample":"8","disposition":{"default":1},"tags":{"language":"und"}},
            {"index":1,"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","bit_rate":"128000","disposition":{"default":1},"tags":{"language":"eng","title":"English"}},
            {"index":2,"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","bit_rate":"128000","disposition":{"default":0},"tags":{"language":"jpn","title":"Japanese"}},
            {"index":3,"codec_type":"subtitle","codec_name":"subrip","disposition":{"forced":1},"tags":{"language":"chi","title":"Chinese"}},
            {"index":4,"codec_type":"attachment","codec_name":"ttf","tags":{"filename":"font.ttf"}}
          ],
          "chapters": [{"id":0,"start_time":"0","end_time":"5","tags":{"title":"Intro"}}]
        }
        """;

        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "movie.mkv");
        File.WriteAllText(path, "placeholder");
        var info = new FFprobeService().ParseProbeJson(path, json);
        Assert.Equal("matroska", info.Container);
        Assert.Equal(2, info.AudioTrackCount);
        Assert.Equal(1, info.SubtitleTrackCount);
        Assert.Equal(1, info.ChapterCount);
        Assert.Equal("Movie", info.Metadata["title"]);
        Assert.Equal("jpn", info.Streams[2].Language);
        Assert.Equal("Japanese", info.Streams[2].Title);
        Assert.True(info.Streams[3].Forced);
        Assert.True(info.Streams[1].Default);
    }

    private static VideoFileInfo StreamMedia() => new()
    {
        FileName = "movie.mkv",
        FullPath = Path.Combine(Path.GetTempPath(), "movie.mkv"),
        Extension = ".mkv",
        FileSizeBytes = 20_000_000,
        LastWriteTimeUtc = DateTime.UtcNow,
        DurationSeconds = 120,
        VideoCodec = "h264",
        VideoBitrateBps = 4_000_000,
        TotalBitrateBps = 4_300_000,
        Width = 320,
        Height = 180,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrateBps = 256_000,
        AudioTrackCount = 2,
        SubtitleCodecs = ["subrip"],
        SubtitleTrackCount = 1,
        ChapterCount = 1,
        Container = "matroska",
        PrimaryVideoStreamIndex = 0,
        Streams =
        [
            new MediaStreamInfo(0, MediaStreamType.Video, "h264", "und", "Video", true),
            new MediaStreamInfo(1, MediaStreamType.Audio, "aac", "eng", "English", true, false),
            new MediaStreamInfo(2, MediaStreamType.Audio, "aac", "jpn", "Japanese"),
            new MediaStreamInfo(3, MediaStreamType.Subtitle, "subrip", "chi", "Chinese", false, true),
            new MediaStreamInfo(4, MediaStreamType.Attachment, "ttf", null, "font.ttf")
        ],
        Metadata = new Dictionary<string, string> { ["title"] = "Movie" }
    };
}

public sealed class SessionRecoveryTests
{
    [Fact]
    public async Task RunningStateReloadsAsInterruptedAndResetsProgress()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var source = ProbeCacheTestsHelper.ProbeInfo(sourcePath);
        var settings = new AppSettings();
        var condition = ConditionEvaluationResult.Pending;
        var plan = new CompressionPlanner().CreatePlan(source, settings);
        var entry = new CompressionTaskEntry(source, plan, condition, new CompressionPlanComparison([]));
        entry.ExecutionState = CompressionExecutionState.Compressing;
        entry.ProgressPercent = 47;
        var session = new CompressionTaskSession([entry], settings, directory.Path, queuePaused: true);
        var path = Path.Combine(directory.Path, "session.json");

        var store = new CompressionTaskSessionStore(path);
        await store.SaveAsync(session);
        var loaded = await new CompressionTaskSessionStore(path).LoadAsync();

        var recovered = Assert.Single(loaded.Session!.Entries);
        Assert.Equal(CompressionExecutionState.Interrupted, recovered.ExecutionState);
        Assert.Equal(0, recovered.ProgressPercent);
        Assert.True(loaded.Session.QueuePaused);
    }

    [Fact]
    public async Task SourceFingerprintChangeReloadsAsSourceChanged()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var source = ProbeCacheTestsHelper.ProbeInfo(sourcePath);
        var plan = new CompressionPlanner().CreatePlan(source, new AppSettings());
        var entry = new CompressionTaskEntry(source, plan, ConditionEvaluationResult.Pending, new CompressionPlanComparison([]));
        var session = new CompressionTaskSession([entry], new AppSettings(), directory.Path);
        var path = Path.Combine(directory.Path, "session.json");
        await new CompressionTaskSessionStore(path).SaveAsync(session);

        await File.AppendAllTextAsync(sourcePath, "changed");
        var loaded = await new CompressionTaskSessionStore(path).LoadAsync();

        Assert.Equal(CompressionExecutionState.SourceChanged, Assert.Single(loaded.Session!.Entries).ExecutionState);
    }
}

public sealed class QueuePauseTests
{
    [Fact]
    public async Task PausePreventsNewWorkButDoesNotCancelCurrentWork()
    {
        var entries = Enumerable.Range(0, 3)
            .Select(_ => new CompressionTaskEntry(
                ProbeCacheTestsHelper.InMemoryInfo(),
                new CompressionPlan(false, VideoEncoder.Libx264, CompressionMode.Crf, 28, "medium", null, null, null, AudioMode.Copy, 192, ".mp4", []),
                ConditionEvaluationResult.Pending,
                new CompressionPlanComparison([])))
            .ToArray();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = 0;
        var paused = true;
        var pool = new CompressionWorkerPool();
        var run = pool.ExecuteAsync(
            entries,
            new LongRunningTaskPolicy(PerformanceMode.LowEndStable, 1, 1, 1, 1, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30), 5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(45), ProcessPriorityMode.Normal, SoftwareThreadPolicy.EncoderDefault, null),
            async _ =>
            {
                if (Interlocked.Increment(ref executed) == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            },
            CancellationToken.None,
            () => paused);

        await Task.Delay(120);
        Assert.Equal(0, executed);
        paused = false;
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        paused = true;
        releaseFirst.SetResult();
        await Task.Delay(120);
        Assert.Equal(1, executed);
        paused = false;
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, executed);
    }
}

public sealed class CompletionActionTests
{
    [Fact]
    public void FailedQueueDoesNotPowerOffByDefault()
    {
        var settings = new AppSettings
        {
            CompletionAction = CompletionAction.Shutdown,
            CompletionActionConfirmed = true,
            DoNotPowerOffOnFailure = true
        };
        var decision = CompletionActionPolicy.Evaluate(settings,
            [CompressionExecutionState.Completed, CompressionExecutionState.Failed]);
        Assert.False(decision.ShouldExecute);
        Assert.Contains("失败", decision.Message);
    }

    [Fact]
    public void CompletedQueueCanScheduleExplicitAction()
    {
        var settings = new AppSettings
        {
            CompletionAction = CompletionAction.Sleep,
            CompletionActionConfirmed = true
        };
        var decision = CompletionActionPolicy.Evaluate(settings, [CompressionExecutionState.Completed]);
        Assert.True(decision.ShouldExecute);
        Assert.Equal(CompletionAction.Sleep, decision.Action);
    }
}

internal static class ProbeCacheTestsHelper
{
    public static VideoFileInfo ProbeInfo(string path) => new()
    {
        FileName = Path.GetFileName(path),
        FullPath = Path.GetFullPath(path),
        Extension = Path.GetExtension(path),
        FileSizeBytes = File.Exists(path) ? new FileInfo(path).Length : 1_000,
        LastWriteTimeUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow,
        DurationSeconds = 10,
        VideoCodec = "h264",
        VideoBitrateBps = 1_000_000,
        TotalBitrateBps = 1_100_000,
        Width = 320,
        Height = 180,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrateBps = 100_000,
        AudioTrackCount = 1,
        Container = "mp4"
    };

    public static VideoFileInfo InMemoryInfo() => new()
    {
        FileName = "memory.mp4",
        FullPath = Path.Combine(Path.GetTempPath(), "memory.mp4"),
        Extension = ".mp4",
        FileSizeBytes = 1_000_000,
        DurationSeconds = 10,
        VideoCodec = "h264",
        VideoBitrateBps = 1_000_000,
        TotalBitrateBps = 1_100_000,
        Width = 320,
        Height = 180,
        FrameRate = 30,
        AudioTrackCount = 0
    };

    public static VideoFileInfo Clone(VideoFileInfo source, double? duration = null, int? width = null) => new()
    {
        FileName = source.FileName,
        FullPath = source.FullPath,
        Extension = source.Extension,
        FileSizeBytes = source.FileSizeBytes,
        LastWriteTimeUtc = source.LastWriteTimeUtc,
        DurationSeconds = duration ?? source.DurationSeconds,
        VideoCodec = source.VideoCodec,
        VideoBitrateBps = source.VideoBitrateBps,
        TotalBitrateBps = source.TotalBitrateBps,
        Width = width ?? source.Width,
        Height = source.Height,
        FrameRate = source.FrameRate,
        PixelFormat = source.PixelFormat,
        BitDepth = source.BitDepth,
        AudioCodec = source.AudioCodec,
        AudioBitrateBps = source.AudioBitrateBps,
        AudioTrackCount = source.AudioTrackCount,
        SubtitleCodecs = [.. source.SubtitleCodecs],
        SubtitleTrackCount = source.SubtitleTrackCount,
        ChapterCount = source.ChapterCount,
        Container = source.Container,
        PrimaryVideoStreamIndex = source.PrimaryVideoStreamIndex,
        Streams = [.. source.Streams],
        Metadata = new Dictionary<string, string>(source.Metadata),
        HealthStatus = source.HealthStatus,
        HealthCheckLevel = source.HealthCheckLevel,
        HealthCheckMessage = source.HealthCheckMessage,
        HealthCheckedAt = source.HealthCheckedAt
    };
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorP0", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
