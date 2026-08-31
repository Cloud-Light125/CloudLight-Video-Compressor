using System.Diagnostics;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class P1FfmpegAcceptanceTests
{
    private const string ToolDirectoryVariable = "CLOUDLIGHT_FFMPEG_TEST_DIR";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_PreservesHevcMain10AndHdrTags()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 HEVC 10-bit 验收。 ");
            return;
        }

        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "hdr-source.mkv");
        await CreateHdrSourceAsync(tools.FFmpegPath, sourcePath);

        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        Assert.True(source.IsHdr, source.HdrSummaryDisplay);
        Assert.True(source.BitDepth is >= 10, $"源位深：{source.BitDepth}");
        var complexity = await new VmafComplexityAnalyzer().AnalyzeAsync(source, tools, CancellationToken.None);
        Assert.NotEmpty(complexity);

        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));
        var settings = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx265,
            CompressionMode = CompressionMode.Crf,
            Crf = 30,
            BitDepthPolicy = BitDepthPolicy.Auto,
            OutputContainer = OutputContainerMode.PreserveSource,
            OutputSuffix = "_main10",
            DiscardIfLarger = false
        };

        var result = await workflow.ProcessFileAsync(
            VideoFileInfo.FromFile(sourcePath),
            settings,
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.True(result.Status == VideoTaskStatus.Completed, result.Message);
        Assert.NotNull(result.OutputPath);
        var output = await probe.ProbeAsync(tools, result.OutputPath!, CancellationToken.None);
        Assert.Equal("hevc", output.VideoCodec);
        Assert.True(output.BitDepth is >= 10, $"输出位深：{output.BitDepth}");
        Assert.Contains(output.VideoProfile ?? string.Empty, ["Main 10", "Main10"], StringComparer.OrdinalIgnoreCase);
        Assert.True(output.IsHdr, output.HdrSummaryDisplay);
        Assert.Equal(source.ColorPrimaries, output.ColorPrimaries, ignoreCase: true);
        Assert.Equal(source.ColorTransfer, output.ColorTransfer, ignoreCase: true);
        Assert.Equal(source.ColorSpace, output.ColorSpace, ignoreCase: true);
        Assert.True(File.Exists(sourcePath), "验证失败时源文件必须保留。");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_UsesHevcQsvMain10WhenCapabilityProbeConfirmsIt()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 QSV Main10 验收。 ");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        var qsv = capabilities.Get(VideoEncoder.HevcQsv);
        if (qsv?.IsUsable != true || !qsv.SupportsBitDepth(10))
        {
            Console.WriteLine("SKIP: 当前 hevc_qsv 未通过 10-bit 能力检测。 ");
            return;
        }

        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "qsv-hdr-source.mkv");
        await CreateHdrSourceAsync(tools.FFmpegPath, sourcePath);
        var probe = new FFprobeService();
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));

        var result = await workflow.ProcessFileAsync(
            VideoFileInfo.FromFile(sourcePath),
            new AppSettings
            {
                VideoEncoder = VideoEncoder.HevcQsv,
                CompressionMode = CompressionMode.Crf,
                Crf = 30,
                BitDepthPolicy = BitDepthPolicy.Auto,
                OutputContainer = OutputContainerMode.Mkv,
                OutputSuffix = "_qsv_main10",
                DiscardIfLarger = false
            },
            tools,
            directory.Path,
            null,
            CancellationToken.None,
            capabilities);

        Assert.True(result.Status == VideoTaskStatus.Completed, result.Message);
        var output = await probe.ProbeAsync(tools, result.OutputPath!, CancellationToken.None);
        Assert.Equal("hevc", output.VideoCodec);
        Assert.True(output.BitDepth is >= 10, $"QSV 输出位深：{output.BitDepth}");
        Assert.Contains(output.VideoProfile ?? string.Empty, ["Main 10", "Main10"], StringComparer.OrdinalIgnoreCase);
        Assert.True(output.IsHdr, output.HdrSummaryDisplay);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_HelpProbeReportsX265TenBitAndMain10()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 encoder help 验收。 ");
            return;
        }

        var result = await new EncoderHelpProbe().ProbeAsync(tools, VideoEncoder.Libx265, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("yuv420p10le", result.SupportedPixelFormats, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(10, result.SupportedBitDepths);
        Assert.Contains("main10", result.SupportedProfiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_BenchmarkUsesSyntheticInputAndPersistsCompleteSnapshot()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 Benchmark 验收。 ");
            return;
        }

        using var directory = new TestDirectory();
        using var cache = new EncoderBenchmarkCache(Path.Combine(directory.Path, "benchmark.json"));
        var capability = new EncoderCapability(
            "libx264",
            "CPU 软件编码 · H.264",
            VideoEncoder.Libx264,
            VideoCodecKind.H264,
            EncoderVendor.Cpu,
            false,
            true,
            true,
            null)
        {
            SupportedPixelFormats = ["yuv420p", "yuv420p10le"],
            SupportedBitDepths = [8, 10],
            SupportedPresets = EncoderStrategyCatalog.Get(VideoEncoder.Libx264).SupportedPresets,
            SupportedRateControls = EncoderStrategyCatalog.Get(VideoEncoder.Libx264).SupportedRateControls
        };
        var capabilities = new EncoderCapabilitySet([capability], "bundled-ffmpeg");
        var benchmark = new EncoderBenchmarkService(new FFmpegService(), cache);
        var result = await benchmark.RunAsync(
            tools,
            capabilities,
            CancellationToken.None,
            options: new EncoderBenchmarkOptions(
                [new EncoderBenchmarkWorkload("smoke", "Smoke 160p", 160, 90, 24, 1)],
                TimeSpan.FromSeconds(15)));

        Assert.True(result.Completed, result.Message);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Snapshot);
        var snapshot = result.Snapshot!;
        var measured = Assert.Single(snapshot.Results);
        Assert.True(measured.Success, measured.FailureReason);
        Assert.True(File.Exists(cache.CachePath));
        Assert.Equal(snapshot.CompletedAt, cache.GetBest(snapshot.Machine)!.CompletedAt);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_PreservesAttachedPictureInMp4()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 attached_pic 验收。 ");
            return;
        }

        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "cover-source.mp4");
        await CreateCoverArtSourceAsync(tools.FFmpegPath, sourcePath);

        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var sourceCover = Assert.Single(source.Streams.Where(stream => stream.IsAttachedPicture));
        Assert.Equal(1, source.AudioTrackCount);
        Assert.Equal("mjpeg", sourceCover.Codec, ignoreCase: true);

        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));
        var settings = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx265,
            CompressionMode = CompressionMode.Crf,
            Crf = 30,
            OutputContainer = OutputContainerMode.Mp4,
            OutputSuffix = "_cover",
            AudioMode = AudioMode.Copy,
            DiscardIfLarger = false
        };

        var result = await workflow.ProcessFileAsync(
            VideoFileInfo.FromFile(sourcePath),
            settings,
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.True(result.Status == VideoTaskStatus.Completed, result.Message);
        var output = await probe.ProbeAsync(tools, result.OutputPath!, CancellationToken.None);
        Assert.Equal("hevc", output.VideoCodec);
        Assert.Equal(1, output.AudioTrackCount);
        var outputCover = Assert.Single(output.Streams.Where(stream => stream.IsAttachedPicture));
        Assert.Equal("mjpeg", outputCover.Codec, ignoreCase: true);
        Assert.Equal(sourceCover.Title, outputCover.Title);
        Assert.True(outputCover.IsAttachedPicture);
        Assert.True(File.Exists(sourcePath), "保留封面图验收失败时源文件必须保留。 ");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_BlocksAttachedPictureWhenTargetMkvCannotPreserveDisposition()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 MKV attached_pic 兼容性验收。 ");
            return;
        }

        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "cover-source.mp4");
        await CreateCoverArtSourceAsync(tools.FFmpegPath, sourcePath);

        var probe = new FFprobeService();
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));
        var result = await workflow.ProcessFileAsync(
            VideoFileInfo.FromFile(sourcePath),
            new AppSettings
            {
                VideoEncoder = VideoEncoder.Libx265,
                OutputContainer = OutputContainerMode.Mkv,
                OutputSuffix = "_mkv",
                DiscardIfLarger = false
            },
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.Equal(VideoTaskStatus.Failed, result.Status);
        Assert.Contains("attached_pic", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sourcePath), "MKV 兼容性阻止后源文件必须保留。 ");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_BenchmarksDetectedEncodersAcrossRequiredWorkloads()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实多编码器 Benchmark 验收。 ");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        var available = capabilities.Capabilities
            .Where(capability => capability.Available && capability.Codec is VideoCodecKind.H264 or VideoCodecKind.H265)
            .ToArray();
        if (available.Length == 0)
        {
            Console.WriteLine("SKIP: 当前 FFmpeg 没有可用的 H.264/H.265 编码器。 ");
            return;
        }

        using var directory = new TestDirectory();
        using var cache = new EncoderBenchmarkCache(Path.Combine(directory.Path, "benchmark.json"));
        var result = await new EncoderBenchmarkService(new FFmpegService(), cache).RunAsync(
            tools,
            capabilities,
            CancellationToken.None,
            options: new EncoderBenchmarkOptions(
            [
                new EncoderBenchmarkWorkload("1080p30", "1080p30", 1920, 1080, 30, 1),
                new EncoderBenchmarkWorkload("4k60", "4K60", 3840, 2160, 60, 0.5)
            ],
            TimeSpan.FromSeconds(20)));

        Assert.True(result.Completed, result.Message);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(available.Length * 2, result.Snapshot!.Results.Count);
        foreach (var measured in result.Snapshot.Results)
        {
            Console.WriteLine($"{measured.EncoderId} {measured.WorkloadDisplay}: {(measured.Success ? $"{measured.AverageSpeed:0.##}x / {measured.AverageFps:0.##} fps" : measured.FailureReason)}");
        }
        var planner = new CompressionPlanner();
        var highQualitySettings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.Automatic,
            CompressionProfile = CompressionProfile.HighQuality,
            EncoderTuningPreset = EncoderTuningPreset.HighQuality,
            BitDepthPolicy = BitDepthPolicy.Auto,
            OutputContainer = OutputContainerMode.Mkv,
            DiscardIfLarger = false
        };
        var auto1080p = planner.CreatePlan(
            CreateBenchmarkMedia(1920, 1080, 30),
            highQualitySettings,
            capabilities: capabilities,
            benchmark: result.Snapshot);
        var auto4K = planner.CreatePlan(
            CreateBenchmarkMedia(3840, 2160, 60),
            highQualitySettings,
            capabilities: capabilities,
            benchmark: result.Snapshot);
        Console.WriteLine($"Auto 1080p: {auto1080p.AutoEncoderDecision?.SelectedEncoder} · {auto1080p.AutoEncoderDecision?.Reason}");
        Console.WriteLine($"Auto 4K60: {auto4K.AutoEncoderDecision?.SelectedEncoder} · {auto4K.AutoEncoderDecision?.Reason}");
        Assert.Equal(VideoEncoder.Libx265, auto1080p.AutoEncoderDecision?.SelectedEncoder);
        Assert.Equal(VideoEncoder.HevcQsv, auto4K.AutoEncoderDecision?.SelectedEncoder);
        Assert.Equal(8, auto1080p.TargetBitDepth);
        Assert.Equal(8, auto4K.TargetBitDepth);
        Assert.True(File.Exists(cache.CachePath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_BenchmarkCancellationKeepsPreviousSnapshotAndCleansTempState()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 Benchmark 取消验收。 ");
            return;
        }

        var capability = new EncoderCapability(
            "libx264",
            "CPU 软件编码 · H.264",
            VideoEncoder.Libx264,
            VideoCodecKind.H264,
            EncoderVendor.Cpu,
            false,
            true,
            true,
            null)
        {
            SupportedPixelFormats = ["yuv420p"],
            SupportedBitDepths = [8],
            SupportedPresets = EncoderStrategyCatalog.Get(VideoEncoder.Libx264).SupportedPresets,
            SupportedRateControls = EncoderStrategyCatalog.Get(VideoEncoder.Libx264).SupportedRateControls
        };
        var capabilities = new EncoderCapabilitySet([capability], "bundled-ffmpeg");
        using var directory = new TestDirectory();
        using var cache = new EncoderBenchmarkCache(Path.Combine(directory.Path, "benchmark.json"));
        var machine = MachineFingerprintService.Create(capabilities);
        var previous = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            machine,
            "bundled-ffmpeg",
            [],
            DateTimeOffset.UtcNow.AddMinutes(-1));
        await cache.SaveCompleteAsync(previous);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await new EncoderBenchmarkService(new FFmpegService(), cache).RunAsync(
            tools,
            capabilities,
            cancellation.Token,
            options: new EncoderBenchmarkOptions(
            [new EncoderBenchmarkWorkload("cancel", "取消测试", 3840, 2160, 60, 10)],
            TimeSpan.FromSeconds(20)));

        Assert.True(result.Cancelled, result.Message);
        Assert.False(result.Completed);
        Assert.Equal(previous.CompletedAt, cache.GetBest(machine)!.CompletedAt);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    private static FFmpegTools? ResolveTools()
    {
        var directory = Environment.GetEnvironmentVariable(ToolDirectoryVariable)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var tools = new FFmpegTools(
            Path.Combine(directory, "ffmpeg.exe"),
            Path.Combine(directory, "ffprobe.exe"));
        return File.Exists(tools.FFmpegPath) && File.Exists(tools.FFprobePath) ? tools : null;
    }

    private static VideoFileInfo CreateBenchmarkMedia(int width, int height, double fps) => new()
    {
        FileName = $"benchmark-{width}x{height}.mkv",
        FullPath = Path.Combine(Path.GetTempPath(), $"benchmark-{Guid.NewGuid():N}.mkv"),
        Extension = ".mkv",
        FileSizeBytes = 100_000_000,
        DurationSeconds = 60,
        VideoCodec = "h264",
        VideoBitrateBps = 8_000_000,
        TotalBitrateBps = 8_000_000,
        Width = width,
        Height = height,
        FrameRate = fps,
        PixelFormat = "yuv420p",
        BitDepth = 8,
        Streams = []
    };

    private static async Task CreateHdrSourceAsync(string ffmpeg, string outputPath)
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
            "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
            "-t", "2", "-an", "-vf", "format=yuv420p10le,setparams=colorspace=bt2020nc:color_primaries=bt2020:color_trc=smpte2084:range=tv",
            "-c:v", "libx265", "-preset", "ultrafast", "-crf", "28",
            "-pix_fmt", "yuv420p10le", "-profile:v", "main10",
            outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"无法生成 HEVC 10-bit 测试视频：{error}");
    }

    private static async Task CreateCoverArtSourceAsync(string ffmpeg, string outputPath)
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
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "color=c=red:s=64x64:duration=0.1",
            "-map", "0:v", "-map", "1:a", "-map", "2:v",
            "-c:v:0", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-c:v:1", "mjpeg",
            "-disposition:v:1", "attached_pic",
            "-metadata:s:v:1", "title=Cover Art",
            "-t", "2",
            outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"无法生成 attached_pic 测试视频：{error}");
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CloudLightVideoCompressorP1FfmpegTests",
                Guid.NewGuid().ToString("N"));
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
}
