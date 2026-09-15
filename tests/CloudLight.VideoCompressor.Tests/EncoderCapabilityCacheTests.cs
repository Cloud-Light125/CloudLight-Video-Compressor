using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;
using System.Text.Json;

namespace CloudLight.VideoCompressor.Tests;

public sealed class EncoderCapabilityCacheTests
{
    private static readonly HardwareEnvironmentIdentity Rtx4070 = new("NVIDIA GeForce RTX 4070", "591.74");

    [Fact]
    public async Task Detector_MarksListedNvencUsableWhenSmokeEncodeSucceeds()
    {
        using var directory = new TemporaryDirectory();
        var smokeCalls = 0;
        var detector = CreateDetector(
            directory.CachePath,
            Rtx4070,
            (_, _, _, _) =>
            {
                smokeCalls++;
                return Task.FromResult(new HardwareEncoderProbeResult(true, null, false, "ffmpeg smoke", string.Empty, 0));
            });

        var result = await detector.DetectAsync(Tools(directory.Path), CancellationToken.None);

        Assert.True(result.Get(VideoEncoder.H264Nvenc)?.IsUsable);
        Assert.True(result.Get(VideoEncoder.HevcNvenc)?.IsUsable);
        Assert.Equal("NVIDIA GeForce RTX 4070", result.GpuName);
        Assert.Equal("591.74", result.NvidiaDriverVersion);
        Assert.True(smokeCalls >= 2);

        using var cacheJson = JsonDocument.Parse(await File.ReadAllTextAsync(directory.CachePath));
        var root = cacheJson.RootElement;
        Assert.Equal("NVIDIA GeForce RTX 4070", root.GetProperty("Identity").GetProperty("GpuName").GetString());
        Assert.Equal("591.74", root.GetProperty("Identity").GetProperty("NvidiaDriverVersion").GetString());
        Assert.Equal("7.1.1-full", root.GetProperty("Identity").GetProperty("FFmpegVersion").GetString());
        Assert.Equal(Path.GetFullPath(Tools(directory.Path).FFmpegPath), root.GetProperty("Identity").GetProperty("FFmpegPath").GetString());
        Assert.True(root.GetProperty("Timestamp").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("driver")]
    [InlineData("ffmpeg-version")]
    [InlineData("ffmpeg-path")]
    public async Task CacheIdentityChange_RunsDetectionAgain(string changedField)
    {
        using var directory = new TemporaryDirectory();
        var firstCalls = 0;
        var first = CreateDetector(
            directory.CachePath,
            Rtx4070,
            (_, _, _, _) =>
            {
                firstCalls++;
                return Task.FromResult(SmokeSuccess());
            });
        await first.DetectAsync(Tools(directory.Path), CancellationToken.None);
        Assert.True(firstCalls > 0);

        var identity = changedField == "gpu" ? new HardwareEnvironmentIdentity("NVIDIA GeForce RTX 4080", "591.74")
            : changedField == "driver" ? new HardwareEnvironmentIdentity("NVIDIA GeForce RTX 4070", "592.00")
            : Rtx4070;
        var version = changedField == "ffmpeg-version" ? "8.0-full" : "7.1.1-full";
        var tools = changedField == "ffmpeg-path" ? Tools(Path.Combine(directory.Path, "replacement")) : Tools(directory.Path);
        var secondCalls = 0;
        var second = CreateDetector(
            directory.CachePath,
            identity,
            (_, _, _, _) =>
            {
                secondCalls++;
                return Task.FromResult(SmokeSuccess());
            },
            version);

        var result = await second.DetectAsync(tools, CancellationToken.None);

        Assert.False(result.FromCache);
        Assert.True(secondCalls > 0);
    }

    [Fact]
    public async Task PreviousFailedNvencCache_CannotBlockSuccessfulProbeOnSameIdentity()
    {
        using var directory = new TemporaryDirectory();
        var failed = CreateDetector(
            directory.CachePath,
            Rtx4070,
            (_, encoder, _, _) => Task.FromResult(
                encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc
                    ? new HardwareEncoderProbeResult(false, "old machine initialization failure", false, "ffmpeg smoke", "failure", 1)
                    : SmokeSuccess()));
        var failedResult = await failed.DetectAsync(Tools(directory.Path), CancellationToken.None);
        Assert.False(failedResult.Get(VideoEncoder.H264Nvenc)?.IsUsable);

        var retryCalls = 0;
        var recovered = CreateDetector(
            directory.CachePath,
            Rtx4070,
            (_, _, _, _) =>
            {
                retryCalls++;
                return Task.FromResult(SmokeSuccess());
            });

        var recoveredResult = await recovered.DetectAsync(Tools(directory.Path), CancellationToken.None);

        Assert.False(recoveredResult.FromCache);
        Assert.True(retryCalls > 0);
        Assert.True(recoveredResult.Get(VideoEncoder.H264Nvenc)?.IsUsable);
        Assert.True(recoveredResult.Get(VideoEncoder.HevcNvenc)?.IsUsable);
    }

    private static EncoderCapabilityDetector CreateDetector(
        string cachePath,
        HardwareEnvironmentIdentity identity,
        Func<FFmpegTools, VideoEncoder, CancellationToken, int, Task<HardwareEncoderProbeResult>> smoke,
        string ffmpegVersion = "7.1.1-full")
    {
        var ids = EncoderCatalog.Definitions.Select(definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var encoders = EncoderCatalog.Definitions.Select(definition => definition.Encoder).ToHashSet();
        return new EncoderCapabilityDetector(
            cache: new EncoderCapabilityCache(cachePath),
            capabilitiesProvider: (_, _) => Task.FromResult(new FFmpegCapabilities(encoders, ffmpegVersion, ids, "ffmpeg -encoders", string.Empty, 0)),
            helpProvider: (_, encoder, _) => Task.FromResult(new EncoderHelpProbeResult(
                true,
                ["yuv420p"],
                EncoderStrategyCatalog.Get(encoder).SupportedPresets,
                [8],
                EncoderCatalog.Get(encoder).Codec == VideoCodecKind.H265 ? ["main"] : ["high"])),
            smokeProvider: smoke,
            hardwareIdentityProvider: _ => Task.FromResult(identity));
    }

    private static HardwareEncoderProbeResult SmokeSuccess() =>
        new(true, null, false, "ffmpeg smoke", string.Empty, 0);

    private static FFmpegTools Tools(string directory) =>
        new(Path.Combine(directory, "ffmpeg.exe"), Path.Combine(directory, "ffprobe.exe"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightEncoderCacheTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string CachePath => System.IO.Path.Combine(Path, "cache", EncoderCapabilityCache.CacheFileName);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
