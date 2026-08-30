using System.Diagnostics;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class EncoderCapabilityIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealDetector_UsesEncoderListAndSmokeTests()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        var hardware = capabilities.Capabilities.Where(capability => capability.IsHardware).ToArray();

        Assert.Equal(6, hardware.Length);
        Assert.All(hardware, capability => Assert.True(capability.IsSupportedByFfmpeg, capability.Id));

        foreach (var capability in hardware)
        {
            Console.WriteLine($"{capability.Id}: {(capability.IsUsable ? "usable" : capability.UnavailableReason)}");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealWorkflow_FallsBackFromInitializationFailureToQsv()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            return;
        }

        var detected = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        if (detected.Get(VideoEncoder.H264Qsv)?.IsUsable != true)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "fallback-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);

        var capabilities = Capabilities(VideoEncoder.H264Nvenc, VideoEncoder.H264Qsv);
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Bitrate,
            TargetVideoBitrateMbps = 1,
            TargetVideoCodec = VideoCodecKind.H264,
            EncoderSelection = EncoderSelectionMode.HardwareAutomatic,
            OutputSuffix = "_fallback",
            DiscardIfLarger = false
        };
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
            settings,
            tools,
            directory.Path,
            null,
            CancellationToken.None,
            capabilities);

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.H264Qsv, result.Encoder);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(File.Exists(sourcePath));
    }

    private static FFmpegTools? ResolveTools() =>
        new FFmpegLocator(pathProvider: () => Environment.GetEnvironmentVariable("PATH")).Locate(null);

    private static EncoderCapabilitySet Capabilities(params VideoEncoder[] usableHardware) =>
        new(EncoderCatalog.Definitions.Select(definition =>
        {
            var usable = !definition.IsHardware || usableHardware.Contains(definition.Encoder);
            return new EncoderCapability(
                definition.Id,
                definition.DisplayName,
                definition.Encoder,
                definition.Codec,
                definition.Vendor,
                definition.IsHardware,
                true,
                usable,
                usable ? null : "集成测试中不可用");
        }));

    private static async Task CreateVideoAsync(string ffmpeg, string outputPath)
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
            "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30",
            "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "700k",
            "-pix_fmt", "yuv420p", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        await outputTask;
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorEncoderTests", Guid.NewGuid().ToString("N"));
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
