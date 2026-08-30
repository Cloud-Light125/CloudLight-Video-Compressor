using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class HardwareAcceptanceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealQsv_4K60_ProgressCompletesOrReportsDeviceUnavailable()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("CLOUDLIGHT_FFMPEG_TEST_DIR")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine("SKIP: 未设置 CLOUDLIGHT_FFMPEG_TEST_DIR，跳过 QSV 4K60 实测。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath))
        {
            Console.WriteLine("SKIP: 测试 FFmpeg 不存在，跳过 QSV 4K60 实测。");
            return;
        }

        var progress = new List<EncodingProgress>();
        var run = await new FFmpegService().RunAsync(
            tools,
            [
                "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=60",
                "-t", "12", "-an", "-c:v", "hevc_qsv", "-global_quality", "26",
                "-f", "null", "-"
            ],
            12,
            new Progress<EncodingProgress>(progress.Add),
            CancellationToken.None);

        if (!run.Succeeded && run.FailureKind is CompressionFailureKind.DeviceInitializationFailure or CompressionFailureKind.EncoderUnavailable or CompressionFailureKind.HardwareSessionFailure)
        {
            Console.WriteLine($"SKIP: hevc_qsv 4K60 当前设备不可用：{run.ErrorOutput}");
            return;
        }

        Assert.True(run.Succeeded, run.ErrorOutput);
        Assert.Contains(progress, item => item.ProcessedDurationSeconds > 0);
        Assert.DoesNotContain(progress, item => item.IsStalled);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_Short1080p_HevcQsvCompletesWhenAvailable()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("CLOUDLIGHT_FFMPEG_TEST_DIR")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine("SKIP: 未设置 CLOUDLIGHT_FFMPEG_TEST_DIR，跳过 1080p QSV 工作流实测。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath) || !File.Exists(tools.FFprobePath))
        {
            Console.WriteLine("SKIP: 测试 FFmpeg/ffprobe 不存在，跳过 1080p QSV 工作流实测。");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        if (capabilities.Get(VideoEncoder.HevcQsv)?.IsUsable != true)
        {
            Console.WriteLine("SKIP: 当前设备 hevc_qsv 不可用，跳过 1080p QSV 工作流实测。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "short-1080p-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath, "1920x1080", 12, "8M");
        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var outputPath = Path.Combine(directory.Path, "short-1080p-output.mp4");
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 28,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.IntelQsv,
            DiscardIfLarger = false
        };
        var plan = new CompressionPlanner().CreatePlan(source, settings, capabilities: capabilities) with
        {
            SourcePath = sourcePath,
            TargetPath = outputPath
        };
        Assert.Equal(VideoEncoder.HevcQsv, plan.Encoder);
        var condition = new ConditionEvaluationResult(
            ConditionResultState.AllAllowed,
            true,
            "全部允许",
            [],
            "全部允许");
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));

        var result = await workflow.ProcessPlannedFileAsync(
            source,
            settings,
            plan,
            condition,
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.HevcQsv, result.PlannedEncoder);
        Assert.Equal(VideoEncoder.HevcQsv, result.Encoder);
        Assert.Equal(VideoCodecKind.H265, plan.EffectiveTargetCodec);
        Assert.Equal("hevc", result.OutputInfo?.VideoCodec);
        Assert.Equal(1920, result.OutputInfo?.Width);
        Assert.Equal(1080, result.OutputInfo?.Height);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_RuntimeFallbackRecordsHardwareFailureAndCommitsCpuResult()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("CLOUDLIGHT_FFMPEG_TEST_DIR")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine("SKIP: 未设置 CLOUDLIGHT_FFMPEG_TEST_DIR，跳过真实回退测试。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath) || !File.Exists(tools.FFprobePath))
        {
            Console.WriteLine("SKIP: 测试 FFmpeg/ffprobe 不存在，跳过真实回退测试。");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        if (capabilities.Get(VideoEncoder.H264Nvenc)?.IsUsable == true)
        {
            Console.WriteLine("SKIP: 当前 NVENC 可用，本测试只验证 NVENC 不可用时的 CPU 回退路径。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "fallback-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);
        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var outputPath = Path.Combine(directory.Path, "fallback-output.mp4");
        var plan = new CompressionPlan(
            IsTwoPass: false,
            Encoder: VideoEncoder.H264Nvenc,
            Mode: CompressionMode.Crf,
            Crf: 28,
            EncodingPreset: "medium",
            TargetVideoBitrateBps: null,
            ResolutionLimit: null,
            FpsLimit: null,
            AudioMode: AudioMode.Copy,
            AudioBitrateKbps: 192,
            OutputExtension: ".mp4",
            Warnings: [],
            FallbackEncoders: [VideoEncoder.Libx264],
            TargetCodec: VideoCodecKind.H264,
            SourcePath: sourcePath,
            TargetPath: outputPath)
        {
            InputInfo = source
        };
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            VideoEncoder = VideoEncoder.H264Nvenc,
            TargetVideoCodec = VideoCodecKind.H264,
            EncoderSelection = EncoderSelectionMode.NvidiaNvenc,
            DiscardIfLarger = false
        };
        var condition = new ConditionEvaluationResult(
            ConditionResultState.AllAllowed,
            true,
            "全部允许",
            [],
            "全部允许");
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));

        var result = await workflow.ProcessPlannedFileAsync(
            source,
            settings,
            plan,
            condition,
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.H264Nvenc, result.PlannedEncoder);
        Assert.Equal(VideoEncoder.Libx264, result.Encoder);
        Assert.Equal(2, result.Attempts?.Count);
        Assert.Equal(VideoEncoder.H264Nvenc, result.Attempts![0].Encoder);
        Assert.Equal(CompressionAttemptStatus.Failed, result.Attempts[0].Status);
        Assert.Equal(CompressionFailureKind.DeviceInitializationFailure, result.Attempts[0].FailureKind);
        Assert.Equal(CompressionAttemptStatus.Completed, result.Attempts[1].Status);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_H265NvencFallbackKeepsH265TargetAndCommitsLibx265()
    {
        var toolDirectory = Environment.GetEnvironmentVariable("CLOUDLIGHT_FFMPEG_TEST_DIR")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine("SKIP: 未设置 CLOUDLIGHT_FFMPEG_TEST_DIR，跳过 H.265 NVENC 回退测试。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath) || !File.Exists(tools.FFprobePath))
        {
            Console.WriteLine("SKIP: 测试 FFmpeg/ffprobe 不存在，跳过 H.265 NVENC 回退测试。");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        if (capabilities.Get(VideoEncoder.HevcNvenc)?.IsUsable == true)
        {
            Console.WriteLine("SKIP: 当前 H.265 NVENC 可用，本测试只验证不可用时的 H.265 CPU 回退路径。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "fallback-h265-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);
        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var outputPath = Path.Combine(directory.Path, "fallback-h265-output.mp4");
        var plan = new CompressionPlan(
            IsTwoPass: false,
            Encoder: VideoEncoder.HevcNvenc,
            Mode: CompressionMode.Crf,
            Crf: 28,
            EncodingPreset: "medium",
            TargetVideoBitrateBps: null,
            ResolutionLimit: null,
            FpsLimit: null,
            AudioMode: AudioMode.Copy,
            AudioBitrateKbps: 192,
            OutputExtension: ".mp4",
            Warnings: [],
            FallbackEncoders: [VideoEncoder.Libx265],
            TargetCodec: VideoCodecKind.H265,
            SourcePath: sourcePath,
            TargetPath: outputPath)
        {
            InputInfo = source
        };
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            VideoEncoder = VideoEncoder.HevcNvenc,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.NvidiaNvenc,
            DiscardIfLarger = false
        };
        var condition = new ConditionEvaluationResult(
            ConditionResultState.AllAllowed,
            true,
            "全部允许",
            [],
            "全部允许");
        var workflow = new CompressionWorkflowService(
            new RuleEngine(),
            probe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(probe));

        var result = await workflow.ProcessPlannedFileAsync(
            source,
            settings,
            plan,
            condition,
            tools,
            directory.Path,
            null,
            CancellationToken.None);

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.HevcNvenc, result.PlannedEncoder);
        Assert.Equal(VideoEncoder.Libx265, result.Encoder);
        Assert.Equal(VideoCodecKind.H265, plan.TargetCodec);
        Assert.Equal(2, result.Attempts?.Count);
        Assert.Equal(VideoEncoder.HevcNvenc, result.Attempts![0].Encoder);
        Assert.Equal(CompressionAttemptStatus.Failed, result.Attempts[0].Status);
        Assert.Equal(VideoEncoder.Libx265, result.Attempts[1].Encoder);
        Assert.Equal(CompressionAttemptStatus.Completed, result.Attempts[1].Status);
        Assert.Equal("hevc", result.OutputInfo?.VideoCodec);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(result.OutputPath));
    }

    private static async Task CreateVideoAsync(
        string ffmpeg,
        string outputPath,
        string size = "320x180",
        int durationSeconds = 3,
        string bitrate = "800k")
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30",
            "-t", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-an", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", bitrate,
            "-pix_fmt", "yuv420p", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        var errorText = await error;
        Assert.True(process.ExitCode == 0, $"Unable to create fallback test video: {errorText}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorHardwareTests", Guid.NewGuid().ToString("N"));
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
