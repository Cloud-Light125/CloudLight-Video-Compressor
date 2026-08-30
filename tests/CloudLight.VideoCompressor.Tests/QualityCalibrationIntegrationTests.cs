using System.Diagnostics;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class QualityCalibrationIntegrationTests
{
    private const string ToolDirectoryVariable = "CLOUDLIGHT_FFMPEG_TEST_DIR";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealVmaf_CalibratesRepresentativeSamplesWhenFilterExists()
    {
        var toolDirectory = Environment.GetEnvironmentVariable(ToolDirectoryVariable)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable}，跳过真实 VMAF 集成测试。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath) || !File.Exists(tools.FFprobePath))
        {
            Console.WriteLine($"SKIP: {ToolDirectoryVariable} 未包含 ffmpeg.exe 与 ffprobe.exe。");
            return;
        }

        var capability = await new VmafCapabilityService().DetectAsync(tools, CancellationToken.None);
        if (!capability.IsAvailable)
        {
            Console.WriteLine($"SKIP: {capability.Message}");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "vmaf-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);
        var source = await new FFprobeService().ProbeAsync(tools, sourcePath, CancellationToken.None);
        var settings = new AppSettings
        {
            CompressionProfile = CompressionProfile.Balanced,
            VmafTarget = 94,
            QualityCalibrationSampleSeconds = 5,
            QualityCalibrationCandidateCount = 3
        };

        var result = await new VmafQualityCalibrationService().CalibrateAsync(
            source,
            settings,
            VideoEncoder.Libx264,
            tools,
            CancellationToken.None);

        Assert.True(result.IsAvailable, result.Message);
        Assert.NotEmpty(result.Samples);
        Assert.NotEmpty(result.Measurements);
        Assert.NotNull(result.SelectedQuality);
        Assert.All(result.Measurements, measurement => Assert.InRange(measurement.Score, 0, 100));
    }

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
            "-t", "4", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
            "-pix_fmt", "yuv420p", outputPath
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
        Assert.True(process.ExitCode == 0, $"无法生成 VMAF 测试视频：{error}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CloudLightVideoCompressorVmafTests",
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
