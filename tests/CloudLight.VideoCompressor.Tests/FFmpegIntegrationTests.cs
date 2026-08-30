using System.Diagnostics;
using System.IO.Pipes;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class FFmpegIntegrationTests
{
    private const string FfmpegTestDirectoryVariable = "CLOUDLIGHT_FFMPEG_TEST_DIR";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_ValidatesCompressionSizingLimitsChinesePathsAndSafety()
    {
        var toolDirectory = ResolveTestToolDirectory();
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            // Keeps normal developer test runs self-contained. CI/manual smoke runs set this to a portable FFmpeg bin directory.
            return;
        }

        var tools = new FFmpegTools(Path.Combine(toolDirectory, "ffmpeg.exe"), Path.Combine(toolDirectory, "ffprobe.exe"));
        Assert.True(File.Exists(tools.FFmpegPath), $"{FfmpegTestDirectoryVariable} must contain ffmpeg.exe");
        Assert.True(File.Exists(tools.FFprobePath), $"{FfmpegTestDirectoryVariable} must contain ffprobe.exe");

        using var directory = new TemporaryDirectory();
        var workflow = CreateWorkflow();
        var probe = new FFprobeService();

        var source = Path.Combine(directory.Path, "source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, source, "640x360", 30, 12, "3M");

        // H.264 and video-bitrate rule: this forces ffprobe on only this current file.
        var h264 = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.Crf,
            Crf = 34,
            OutputSuffix = "_h264",
            DiscardIfLarger = false
        };
        h264.Rules.Add(new CompressionRule { Field = RuleField.VideoBitrate, Comparison = RuleComparison.GreaterThan, Value = "0.2 Mbps" });
        var h264Result = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(source), h264, tools, directory.Path, null, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, h264Result.Status);
        Assert.NotNull(h264Result.OutputPath);
        Assert.Equal("h264", (await probe.ProbeAsync(tools, h264Result.OutputPath!, CancellationToken.None)).VideoCodec);

        // H.265 uses the same protected temporary-file workflow.
        var h265 = h264.Clone();
        h265.Rules.Clear(); // Exercise direct processing without a pre-encoding ffprobe duration.
        h265.VideoEncoder = VideoEncoder.Libx265;
        h265.OutputSuffix = "_h265";
        var h265Progress = new CapturingProgress<WorkflowProgress>();
        var h265Result = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(source), h265, tools, directory.Path, h265Progress, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, h265Result.Status);
        Assert.Equal("hevc", (await probe.ProbeAsync(tools, h265Result.OutputPath!, CancellationToken.None)).VideoCodec);
        Assert.Contains(h265Progress.Items, update => update.Encoding?.Remaining is not null);

        // Target-size mode uses two passes and reserves audio/container space.
        var targetSize = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.TargetSize,
            TargetSize = "700 KB",
            OutputSuffix = "_target",
            DiscardIfLarger = false
        };
        var targetResult = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(source), targetSize, tools, directory.Path, null, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, targetResult.Status);
        Assert.True(new FileInfo(targetResult.OutputPath!).Length <= 700L * 1024 * 1.03, "Two-pass output exceeded the allowed 3% target-size tolerance.");

        // Actual resolution/FPS limits lower, never raise, source values.
        var highRateSource = Path.Combine(directory.Path, "high-rate.mp4");
        await CreateVideoAsync(tools.FFmpegPath, highRateSource, "1280x720", 120, 3, "4M");
        var limited = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.Crf,
            Crf = 33,
            ResolutionLimit = ResolutionLimitPreset.Custom,
            CustomMaxWidth = 640,
            CustomMaxHeight = 360,
            FpsLimit = FpsLimitPreset.Fps60,
            OutputSuffix = "_limited",
            DiscardIfLarger = false
        };
        var limitedResult = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(highRateSource), limited, tools, directory.Path, null, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, limitedResult.Status);
        var limitedInfo = await probe.ProbeAsync(tools, limitedResult.OutputPath!, CancellationToken.None);
        Assert.True(limitedInfo.Width <= 640 && limitedInfo.Height <= 360);
        Assert.True(limitedInfo.FrameRate <= 60.1);

        // Chinese path and moving the source to “高质量” after validation.
        var chineseDirectory = Path.Combine(directory.Path, "视频测试");
        Directory.CreateDirectory(chineseDirectory);
        var chineseSource = Path.Combine(chineseDirectory, "测试源文件.mp4");
        await CreateVideoAsync(tools.FFmpegPath, chineseSource, "480x270", 30, 3, "2M");
        var moveOriginal = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.Crf,
            Crf = 34,
            OutputPrefix = string.Empty,
            OutputSuffix = string.Empty,
            OriginalFileAction = OriginalFileAction.MoveToSiblingChildDirectory,
            OriginalFilesSubdirectory = "高质量",
            DiscardIfLarger = false
        };
        var movedResult = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(chineseSource), moveOriginal, tools, directory.Path, null, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Completed, movedResult.Status);
        Assert.True(File.Exists(chineseSource));
        Assert.True(File.Exists(Path.Combine(chineseDirectory, "高质量", "测试源文件.mp4")));

        // Deliberately point the FFmpeg executable at ffprobe. It fails before any source-file move.
        var protectedSource = Path.Combine(directory.Path, "protected.mp4");
        await CreateVideoAsync(tools.FFmpegPath, protectedSource, "320x180", 30, 2, "1M");
        var failureSettings = new AppSettings
        {
            OutputSuffix = "_should_not_exist",
            OriginalFileAction = OriginalFileAction.MoveToSiblingChildDirectory
        };
        var intentionalFailure = await workflow.ProcessFileAsync(
            VideoFileInfo.FromFile(protectedSource),
            failureSettings,
            new FFmpegTools(tools.FFprobePath, tools.FFprobePath),
            directory.Path,
            null,
            CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Failed, intentionalFailure.Status);
        Assert.True(File.Exists(protectedSource));
        Assert.False(File.Exists(Path.Combine(directory.Path, "protected_should_not_exist.mp4")));

        // Lossless CRF makes a larger result; the default safety option discards it and leaves the source.
        var largerSource = Path.Combine(directory.Path, "larger.mp4");
        await CreateVideoAsync(tools.FFmpegPath, largerSource, "320x180", 30, 4, "350k");
        var discardLarger = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.Crf,
            Crf = 0,
            OutputSuffix = "_lossless",
            DiscardIfLarger = true
        };
        var largerResult = await workflow.ProcessFileAsync(VideoFileInfo.FromFile(largerSource), discardLarger, tools, directory.Path, null, CancellationToken.None);
        Assert.Equal(VideoTaskStatus.Skipped, largerResult.Status);
        Assert.True(File.Exists(largerSource));
        Assert.False(File.Exists(Path.Combine(directory.Path, "larger_lossless.mp4")));

        // Concurrent jobs with the same source file name must reserve different names in a flattened output directory.
        var firstInputDirectory = Path.Combine(directory.Path, "parallel-a");
        var secondInputDirectory = Path.Combine(directory.Path, "parallel-b");
        var flattenedOutputDirectory = Path.Combine(directory.Path, "parallel-output");
        Directory.CreateDirectory(firstInputDirectory);
        Directory.CreateDirectory(secondInputDirectory);
        var firstParallelSource = Path.Combine(firstInputDirectory, "same-name.mp4");
        var secondParallelSource = Path.Combine(secondInputDirectory, "same-name.mp4");
        await CreateVideoAsync(tools.FFmpegPath, firstParallelSource, "320x180", 30, 2, "700k");
        await CreateVideoAsync(tools.FFmpegPath, secondParallelSource, "320x180", 30, 2, "700k");
        var parallelSettings = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx264,
            CompressionMode = CompressionMode.Crf,
            Crf = 34,
            OutputLocation = OutputLocationMode.SelectedDirectory,
            OutputDirectory = flattenedOutputDirectory,
            PreserveDirectoryStructure = false,
            OutputSuffix = "_parallel",
            DiscardIfLarger = false
        };
        var parallelResults = await Task.WhenAll(
            workflow.ProcessFileAsync(VideoFileInfo.FromFile(firstParallelSource), parallelSettings.Clone(), tools, directory.Path, null, CancellationToken.None),
            workflow.ProcessFileAsync(VideoFileInfo.FromFile(secondParallelSource), parallelSettings.Clone(), tools, directory.Path, null, CancellationToken.None));
        Assert.All(parallelResults, result => Assert.Equal(VideoTaskStatus.Completed, result.Status));
        Assert.NotEqual(parallelResults[0].OutputPath, parallelResults[1].OutputPath);
        Assert.All(parallelResults, result => Assert.True(File.Exists(result.OutputPath!)));

        // Cancellation terminates the encoder before returning and never changes the input file.
        var cancelledOutput = Path.Combine(directory.Path, "cancelled-temp.mp4");
        using var cancellation = new CancellationTokenSource();
        var cancellationTask = new FFmpegService().RunAsync(
            tools,
            ["-y", "-re", "-i", source, "-c:v", "libx264", "-c:a", "aac", cancelledOutput],
            12,
            null,
            cancellation.Token);
        await Task.Delay(300);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancellationTask);
        Assert.True(File.Exists(source));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealScanner_CancellationStopsQueuedFfprobeWork()
    {
        var toolDirectory = ResolveTestToolDirectory();
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            return;
        }

        var tools = new FFmpegTools(Path.Combine(toolDirectory, "ffmpeg.exe"), Path.Combine(toolDirectory, "ffprobe.exe"));
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "scan-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, source, "320x180", 30, 2, "1M");
        for (var index = 0; index < 24; index++)
        {
            File.Copy(source, Path.Combine(directory.Path, $"scan-{index:D2}.mp4"));
        }

        var scanned = 0;
        using var cancellation = new CancellationTokenSource();
        var scanner = new VideoScannerService(new FFprobeService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scanner.ScanAsync(
            directory.Path,
            recursive: false,
            maximumProbeConcurrency: 1,
            tools: tools,
            onVideo: _ =>
            {
                cancellation.Cancel();
                Interlocked.Increment(ref scanned);
                return Task.CompletedTask;
            },
            onProbeFailure: null,
            cancellationToken: cancellation.Token));

        Assert.Equal(1, Volatile.Read(ref scanned));
        Assert.True(File.Exists(source));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfprobe_CancellationTerminatesAnInFlightAnalysis()
    {
        var toolDirectory = ResolveTestToolDirectory();
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            return;
        }

        var tools = new FFmpegTools(Path.Combine(toolDirectory, "ffmpeg.exe"), Path.Combine(toolDirectory, "ffprobe.exe"));
        var pipeName = $"CloudLightVideoCompressorProbe-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        var probeTask = new FFprobeService().ProbeAsync(tools, $@"\\.\pipe\{pipeName}", cancellation.Token);

        await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await probeTask);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_CancellationKeepsSourceAndDeletesTemporaryOutput()
    {
        var toolDirectory = ResolveTestToolDirectory();
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            return;
        }

        var tools = new FFmpegTools(Path.Combine(toolDirectory, "ffmpeg.exe"), Path.Combine(toolDirectory, "ffprobe.exe"));
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "cancellation-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, source, "1920x1080", 60, 12, "12M");
        var settings = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx265,
            CompressionMode = CompressionMode.Crf,
            Crf = 28,
            OutputSuffix = "_cancelled",
            DiscardIfLarger = false,
            OriginalFileAction = OriginalFileAction.MoveToSiblingChildDirectory
        };
        var progress = new CapturingProgress<WorkflowProgress>();
        using var cancellation = new CancellationTokenSource();
        var task = CreateWorkflow().ProcessFileAsync(
            VideoFileInfo.FromFile(source),
            settings,
            tools,
            directory.Path,
            progress,
            cancellation.Token);

        await WaitUntilAsync(() => progress.Items.Any(item => item.Status == VideoTaskStatus.Compressing), TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await task;

        Assert.Equal(VideoTaskStatus.Cancelled, result.Status);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "高质量")));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".*.clvc-*", SearchOption.AllDirectories));
    }

    private static CompressionWorkflowService CreateWorkflow()
    {
        var probe = new FFprobeService();
        return new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));
    }

    private static string? ResolveTestToolDirectory()
    {
        var environmentValue = Environment.GetEnvironmentVariable(FfmpegTestDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim().Trim('"');
        }

        // A local, ignored text file is convenient for manual smoke testing when a runner does not propagate environment variables.
        var candidateDirectories = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(FFmpegIntegrationTests).Assembly.Location),
            Directory.GetCurrentDirectory()
        }.Where(directory => !string.IsNullOrWhiteSpace(directory)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in candidateDirectories)
        {
            var pathFile = Path.Combine(directory!, "cloudlight-ffmpeg-test.path");
            if (File.Exists(pathFile))
            {
                return File.ReadAllText(pathFile).Trim().Trim('"');
            }
        }

        return null;
    }

    private static async Task CreateVideoAsync(string ffmpeg, string outputPath, string size, int fps, int seconds, string bitrate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={fps}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", seconds.ToString(),
            "-c:v", "libx264", "-preset", "ultrafast", "-b:v", bitrate, "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-shortest", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await standardError;
        await standardOutput;
        Assert.True(process.ExitCode == 0, $"Unable to create test video: {error}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out while waiting for FFmpeg compression to begin.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorFfmpegTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly object _sync = new();
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_sync)
                {
                    return _items.ToArray();
                }
            }
        }

        public void Report(T value)
        {
            lock (_sync)
            {
                _items.Add(value);
            }
        }
    }
}
