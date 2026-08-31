using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

[Collection("P0 FFmpeg")]
public sealed class P0FfmpegIntegrationTests
{
    private const string ToolDirectoryVariable = "CLOUDLIGHT_FFMPEG_TEST_DIR";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealProbeCache_OneHundredFilesSecondScanUsesNoProbeProcesses()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过 100 文件缓存实测。");
            return;
        }

        using var directory = new P0TemporaryDirectory();
        var source = Path.Combine(directory.Path, "cache-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, source, 1);
        for (var index = 1; index < 100; index++)
        {
            File.Copy(source, Path.Combine(directory.Path, $"cache-{index:D3}.mp4"));
        }

        var probe = new FFprobeService();
        using var cache = new MediaProbeCache(Path.Combine(directory.Path, "cache", MediaProbeCache.CacheFileName));
        var scanner = new VideoScannerService(probe, cache);
        var failures = new ConcurrentBag<string>();

        var firstWatch = Stopwatch.StartNew();
        await scanner.ScanAsync(
            directory.Path,
            recursive: false,
            maximumProbeConcurrency: 6,
            tools,
            _ => Task.CompletedTask,
            (path, message) =>
            {
                failures.Add($"{path}: {message}");
                return Task.CompletedTask;
            },
            CancellationToken.None,
            healthCheckLevel: HealthCheckLevel.Disabled);
        firstWatch.Stop();
        var first = cache.GetStatistics();

        var secondWatch = Stopwatch.StartNew();
        await scanner.ScanAsync(
            directory.Path,
            recursive: false,
            maximumProbeConcurrency: 6,
            tools,
            _ => Task.CompletedTask,
            (path, message) =>
            {
                failures.Add($"{path}: {message}");
                return Task.CompletedTask;
            },
            CancellationToken.None,
            healthCheckLevel: HealthCheckLevel.Disabled);
        secondWatch.Stop();
        var second = cache.GetStatistics();

        Assert.Empty(failures);
        Assert.Equal(100, first.ActualProbes);
        Assert.Equal(0, first.CacheHits);
        Assert.Equal(100, second.CacheHits);
        Assert.Equal(0, second.ActualProbes);
        Assert.True(secondWatch.Elapsed < firstWatch.Elapsed,
            $"第二次扫描没有明显更快：第一次 {firstWatch.Elapsed}, 第二次 {secondWatch.Elapsed}。");
        Console.WriteLine($"100 文件缓存实测：第一次 {firstWatch.Elapsed.TotalSeconds:0.00}s / ffprobe {first.ActualProbes}，第二次 {secondWatch.Elapsed.TotalSeconds:0.00}s / 命中 {second.CacheHits} / ffprobe {second.ActualProbes}。");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealHealthCheck_QuickDeepAndTruncatedSource()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过健康检查实测。");
            return;
        }

        using var directory = new P0TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "healthy.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath, 2);
        var probe = new FFprobeService();
        using var cache = new MediaProbeCache(Path.Combine(directory.Path, "cache", MediaProbeCache.CacheFileName));
        var health = new MediaHealthCheckService(cache, probe, new FFmpegService());
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);

        var quick = await health.CheckAsync(tools, source, HealthCheckLevel.Quick, CancellationToken.None);
        Assert.Equal(MediaHealthStatus.Healthy, quick.Status);
        var deep = await health.CheckAsync(tools, source, HealthCheckLevel.Deep, CancellationToken.None);
        Assert.Equal(MediaHealthStatus.Healthy, deep.Status);
        var cachedDeep = await health.CheckAsync(tools, source, HealthCheckLevel.Deep, CancellationToken.None);
        Assert.True(cachedDeep.CacheHit);

        var truncatedPath = Path.Combine(directory.Path, "truncated.mp4");
        File.Copy(sourcePath, truncatedPath);
        using (var stream = new FileStream(truncatedPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(Math.Max(1, stream.Length / 3));
        }

        var truncated = VideoFileInfo.FromFile(truncatedPath);
        var truncatedQuick = await health.CheckAsync(tools, truncated, HealthCheckLevel.Quick, CancellationToken.None);
        var truncatedDeep = await health.CheckAsync(tools, truncated, HealthCheckLevel.Deep, CancellationToken.None);
        Assert.True(
            truncatedQuick.Status == MediaHealthStatus.Corrupt || truncatedDeep.Status == MediaHealthStatus.Corrupt,
            $"截断文件没有被识别为损坏：Quick={truncatedQuick.Status}, Deep={truncatedDeep.Status}。");
        Assert.Equal(MediaHealthStatus.Corrupt, truncatedDeep.Status);
        Console.WriteLine($"健康检查实测：Quick={quick.Status}，Deep={deep.Status}，再次 Deep CacheHit={cachedDeep.CacheHit}，截断文件={truncatedDeep.Status}。");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_MkvRetainsMultipleStreamsMetadataChaptersAndCleansTempFiles()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过多流 MKV 实测。");
            return;
        }

        using var directory = new P0TemporaryDirectory();
        var subtitlePath = Path.Combine(directory.Path, "subtitle.srt");
        var chaptersPath = Path.Combine(directory.Path, "chapters.ffmeta");
        var attachmentPath = Path.Combine(directory.Path, "font.ttf");
        await File.WriteAllTextAsync(subtitlePath, "1\n00:00:00,000 --> 00:00:01,500\n你好\n\n2\n00:00:01,500 --> 00:00:03,000\nHello\n");
        await File.WriteAllTextAsync(chaptersPath, ";FFMETADATA1\ntitle=CloudLight P0 Movie\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=Intro\n\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=3000\ntitle=Main\n");
        await File.WriteAllBytesAsync(attachmentPath, [0, 1, 2, 3, 4, 5]);

        var sourcePath = Path.Combine(directory.Path, "multi-stream.mkv");
        await RunCheckedAsync(tools.FFmpegPath, [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-f", "srt", "-i", subtitlePath,
            "-f", "ffmetadata", "-i", chaptersPath,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-map", "3:0",
            "-map_metadata", "4", "-map_chapters", "4", "-t", "3",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English",
            "-disposition:a:0", "default",
            "-metadata:s:a:1", "language=jpn", "-metadata:s:a:1", "title=Japanese",
            "-disposition:a:1", "0",
            "-metadata:s:s:0", "language=chi", "-metadata:s:s:0", "title=Chinese",
            "-disposition:s:0", "forced",
            "-attach", attachmentPath, "-metadata:s:t:0", "mimetype=application/x-truetype",
            "-metadata:s:t:0", "filename=font.ttf", sourcePath
        ]);

        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        Assert.Equal(2, source.AudioTrackCount);
        Assert.Equal(1, source.SubtitleTrackCount);
        Assert.Equal(2, source.ChapterCount);
        Assert.Equal("CloudLight P0 Movie", source.Metadata["title"]);
        Assert.Contains(source.Streams, stream => stream.StreamType == MediaStreamType.Attachment);

        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            VideoEncoder = VideoEncoder.Libx264,
            TargetVideoCodec = VideoCodecKind.H264,
            EncoderSelection = EncoderSelectionMode.CpuSoftware,
            EncodingPreset = "ultrafast",
            Crf = 30,
            AudioMode = AudioMode.Copy,
            OutputContainer = OutputContainerMode.Mkv,
            HealthCheckLevel = HealthCheckLevel.Quick,
            DiscardIfLarger = false
        };
        var cacheDirectory = Path.Combine(directory.Path, "cache");
        using var probeCache = new MediaProbeCache(Path.Combine(cacheDirectory, MediaProbeCache.CacheFileName));
        using var resultCache = new CompressionResultCache(Path.Combine(cacheDirectory, CompressionResultCache.CacheFileName));
        var ffmpeg = new FFmpegService();
        var plan = new CompressionPlanner(resultCache: resultCache).CreatePlan(source, settings) with
        {
            SourcePath = sourcePath,
            TargetPath = Path.Combine(directory.Path, "multi-stream-compressed.mkv")
        };
        Assert.False(plan.StreamAudit!.BlocksExecution);
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            ffmpeg,
            new CompressionPlanner(resultCache: resultCache),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe),
            probeCache: probeCache,
            healthCheckService: new MediaHealthCheckService(probeCache, probe, ffmpeg));
        var condition = new ConditionEvaluationResult(
            ConditionResultState.AllAllowed,
            true,
            "全部允许",
            [],
            "全部允许");

        var result = await workflow.ProcessPlannedFileAsync(
            source,
            settings,
            plan,
            condition,
            tools,
            directory.Path,
            progress: null,
            CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.True(File.Exists(result.OutputPath), result.Message);

        var output = await probe.ProbeAsync(tools, result.OutputPath!, CancellationToken.None);
        Assert.Equal(2, output.AudioTrackCount);
        Assert.Equal(1, output.SubtitleTrackCount);
        Assert.Equal(2, output.ChapterCount);
        Assert.Equal("CloudLight P0 Movie", output.Metadata["title"]);
        var outputAudio = output.Streams.Where(stream => stream.StreamType == MediaStreamType.Audio).ToArray();
        Assert.Equal(["eng", "jpn"], outputAudio.Select(stream => stream.Language).ToArray());
        Assert.Equal(["English", "Japanese"], outputAudio.Select(stream => stream.Title).ToArray());
        Assert.True(outputAudio[0].Default);
        Assert.False(outputAudio[1].Default);
        var outputSubtitle = Assert.Single(output.Streams.Where(stream => stream.StreamType == MediaStreamType.Subtitle));
        Assert.Equal("chi", outputSubtitle.Language);
        Assert.Equal("Chinese", outputSubtitle.Title);
        Assert.True(outputSubtitle.Forced);
        Assert.Contains(output.Streams, stream => stream.StreamType == MediaStreamType.Attachment);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories), path =>
            Path.GetFileName(path).Contains(".clvc-", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("多流 MKV 实测：2 音轨、1 字幕、2 章节、1 附件、全局 metadata 与语言/default/forced 标记均通过最终 ffprobe 验证。");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealQueuePause_CurrentFfmpegFinishesBeforeNextTaskStarts()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过真实队列暂停实测。");
            return;
        }

        using var directory = new P0TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "queue-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath, 2);
        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            VideoEncoder = VideoEncoder.Libx264,
            TargetVideoCodec = VideoCodecKind.H264,
            EncodingPreset = "ultrafast",
            Crf = 32,
            AudioMode = AudioMode.Copy,
            DiscardIfLarger = false
        };
        var planner = new CompressionPlanner();
        var condition = new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许");
        var entries = Enumerable.Range(1, 2)
            .Select(index =>
            {
                var plan = planner.CreatePlan(source, settings) with
                {
                    SourcePath = sourcePath,
                    TargetPath = Path.Combine(directory.Path, $"queue-output-{index}.mp4")
                };
                return new CompressionTaskEntry(source, plan, condition, new CompressionPlanComparison([]));
            })
            .ToArray();
        var policy = new LongRunningTaskPolicy(
            PerformanceMode.Balanced,
            1,
            1,
            1,
            1,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            5,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(75),
            TimeSpan.FromSeconds(45),
            ProcessPriorityMode.Normal,
            SoftwareThreadPolicy.EncoderDefault,
            null);
        var started = new ConcurrentQueue<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = false;
        var pool = new CompressionWorkerPool();
        var run = pool.ExecuteAsync(
            entries,
            policy,
            async entry =>
            {
                started.Enqueue(entry.FileName);
                if (started.Count == 1)
                {
                    firstStarted.SetResult();
                }

                var arguments = entry.Plan.BuildArguments(entry.Source.FullPath, entry.Plan.TargetPath!, false).ToList();
                arguments.Insert(1, "-re");
                var result = await new FFmpegService().RunAsync(
                    tools,
                    arguments,
                    entry.Source.DurationSeconds,
                    progress: null,
                    CancellationToken.None);
                Assert.True(result.Succeeded, result.ErrorOutput);
                entry.ExecutionState = CompressionExecutionState.Completed;
                if (started.Count == 1)
                {
                    firstCompleted.SetResult();
                }
            },
            CancellationToken.None,
            () => paused);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        paused = true;
        await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(300);
        Assert.Single(started);
        paused = false;
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, started.Count);
        Assert.Equal(entries.Select(entry => entry.FileName), started);
        Console.WriteLine("真实队列暂停实测：当前 FFmpeg 完成后队列仍保持暂停，继续队列后才启动第二个视频。");
    }

    private static async Task CreateVideoAsync(string ffmpeg, string path, int seconds)
    {
        await RunCheckedAsync(ffmpeg, [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k", "-shortest", path
        ]);
    }

    private static async Task RunCheckedAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(executable, arguments);
        Assert.True(result.ExitCode == 0, $"FFmpeg command failed ({result.ExitCode}): {result.Error}");
    }

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static FFmpegTools? ResolveTools()
    {
        var directory = Environment.GetEnvironmentVariable(ToolDirectoryVariable)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        var ffprobe = Path.Combine(directory, "ffprobe.exe");
        return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FFmpegTools(ffmpeg, ffprobe) : null;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class P0TemporaryDirectory : IDisposable
    {
        public P0TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorP0Ffmpeg", Guid.NewGuid().ToString("N"));
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
}

[CollectionDefinition("P0 FFmpeg", DisableParallelization = true)]
public sealed class P0FfmpegCollectionDefinition
{
}
