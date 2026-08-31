using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class P1EncoderPlanningTests
{
    [Theory]
    [InlineData(VideoEncoder.Libx264, "slow", "medium", "fast", "veryfast")]
    [InlineData(VideoEncoder.Libx265, "slow", "medium", "fast", "veryfast")]
    [InlineData(VideoEncoder.H264Qsv, "1", "3", "5", "7")]
    [InlineData(VideoEncoder.HevcQsv, "1", "3", "5", "7")]
    [InlineData(VideoEncoder.H264Nvenc, "p7", "p4", "p2", "p1")]
    [InlineData(VideoEncoder.HevcNvenc, "p7", "p4", "p2", "p1")]
    [InlineData(VideoEncoder.H264Amf, "high_quality", "quality", "quality", "quality")]
    [InlineData(VideoEncoder.HevcAmf, "high_quality", "quality", "quality", "quality")]
    [InlineData(VideoEncoder.LibsvtAv1, "4", "6", "10", "13")]
    public void TuningPresetMapsToEncoderNativeVocabulary(
        VideoEncoder encoder,
        string highQuality,
        string balanced,
        string fast,
        string veryFast)
    {
        Assert.Equal(highQuality, EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.HighQuality));
        Assert.Equal(balanced, EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.Balanced));
        Assert.Equal(fast, EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.Fast));
        Assert.Equal(veryFast, EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.VeryFast));
    }

    [Fact]
    public void BalancedTuningKeepsKnownLegacyPresetForCompatibility()
    {
        Assert.Equal("veryslow", EncoderTuningCatalog.Resolve(
            VideoEncoder.Libx265,
            EncoderTuningPreset.Balanced,
            "veryslow"));
        Assert.Equal("medium", EncoderTuningCatalog.Resolve(
            VideoEncoder.Libx265,
            EncoderTuningPreset.Balanced,
            "not-a-preset"));
    }

    [Fact]
    public void AutoBitDepthPreservesHdrAndBuildsMain10Arguments()
    {
        var source = Media(
            extension: ".mkv",
            videoCodec: "hevc",
            pixelFormat: "yuv420p10le",
            bitDepth: 10,
            profile: "Main 10",
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084",
            colorSpace: "bt2020nc",
            mastering: new Dictionary<string, string> { ["max_luminance"] = "1000" });
        var settings = new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx265,
            CompressionMode = CompressionMode.Crf,
            BitDepthPolicy = BitDepthPolicy.Auto,
            EncoderTuningPreset = EncoderTuningPreset.HighQuality
        };

        var plan = new CompressionPlanner().CreatePlan(source, settings);
        var arguments = plan.BuildArguments("input.mkv", "output.mkv", false).ToArray();

        Assert.Equal(10, plan.TargetBitDepth);
        Assert.Equal("yuv420p10le", plan.TargetPixelFormat);
        Assert.Equal("main10", plan.TargetProfile);
        Assert.False(plan.BlocksExecution);
        Assert.Contains("yuv420p10le", arguments);
        Assert.Contains("main10", arguments);
        Assert.Contains("smpte2084", arguments);
        Assert.Contains("bt2020", arguments);
        Assert.Contains(plan.Warnings, warning => warning.Contains("HDR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitEightBitOverrideWarnsBeforeDownConversion()
    {
        var source = Media(
            extension: ".mkv",
            videoCodec: "hevc",
            pixelFormat: "yuv420p10le",
            bitDepth: 10,
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084");
        var plan = new CompressionPlanner().CreatePlan(source, new AppSettings
        {
            VideoEncoder = VideoEncoder.Libx265,
            BitDepthPolicy = BitDepthPolicy.EightBit
        });

        Assert.Equal(8, plan.TargetBitDepth);
        Assert.Equal("yuv420p", plan.TargetPixelFormat);
        Assert.Contains(plan.Warnings, warning => warning.Contains("转换为 8-bit", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoSelectionUsesMatchingLocalBenchmarkAndExplainsChoice()
    {
        var capabilities = new EncoderCapabilitySet(
        [
            Capability(VideoEncoder.Libx264, false, ["yuv420p", "yuv420p10le"], [8, 10]),
            Capability(VideoEncoder.H264Nvenc, true, ["nv12"], [8])
        ],
        "ffmpeg-test");
        var fingerprint = MachineFingerprintService.Create(capabilities);
        var benchmark = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            fingerprint,
            "ffmpeg-test",
            [
                Benchmark(VideoEncoder.Libx264, "libx264", false, 0.20, 6),
                Benchmark(VideoEncoder.H264Nvenc, "h264_nvenc", true, 2.20, 66)
            ],
            DateTimeOffset.UtcNow);

        var decision = new AutoEncoderSelectionService().Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H264,
            CompressionProfile.RemotePlayback,
            EncoderTuningPreset.Balanced,
            capabilities,
            benchmark,
            1920,
            1080,
            30,
            "h264"));

        Assert.Equal(VideoEncoder.H264Nvenc, decision.SelectedEncoder);
        Assert.Equal(BenchmarkConfidence.High, decision.Confidence);
        Assert.True(decision.UsedBenchmark);
        Assert.Contains("本机基准", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(VideoEncoder.Libx264, decision.FallbackChain);
    }

    [Fact]
    public void HighQualityAutoUsesCpuAt1080pButAvoidsVerySlowCpuAt4K60()
    {
        var capabilities = new EncoderCapabilitySet(
        [
            Capability(VideoEncoder.Libx265, false, ["yuv420p"], [8]),
            Capability(VideoEncoder.HevcQsv, true, ["nv12"], [8])
        ],
        "ffmpeg-test");
        var fingerprint = MachineFingerprintService.Create(capabilities);
        var benchmark = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            fingerprint,
            "ffmpeg-test",
            [
                Benchmark(VideoEncoder.Libx265, "libx265", false, 1.30, 39),
                Benchmark(VideoEncoder.HevcQsv, "hevc_qsv", true, 3.00, 90)
            ],
            DateTimeOffset.UtcNow);
        var service = new AutoEncoderSelectionService();

        var at1080p = service.Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H265,
            CompressionProfile.HighQuality,
            EncoderTuningPreset.HighQuality,
            capabilities,
            benchmark,
            1920,
            1080,
            30));
        var at4K = service.Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H265,
            CompressionProfile.HighQuality,
            EncoderTuningPreset.HighQuality,
            capabilities,
            benchmark with
            {
                Results =
                [
                    Benchmark(VideoEncoder.Libx265, "libx265", false, 0.08, 4.8) with
                    {
                        Width = 3840,
                        Height = 2160,
                        Fps = 60,
                        WorkloadId = "4k60"
                    },
                    Benchmark(VideoEncoder.HevcQsv, "hevc_qsv", true, 1.10, 66) with
                    {
                        Width = 3840,
                        Height = 2160,
                        Fps = 60,
                        WorkloadId = "4k60"
                    }
                ]
            },
            3840,
            2160,
            60));

        Assert.Equal(VideoEncoder.Libx265, at1080p.SelectedEncoder);
        Assert.Equal(VideoEncoder.HevcQsv, at4K.SelectedEncoder);
        Assert.Contains("软件编码预计耗时过长", at4K.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedPriorityUsesFastestHardwareRatherThanCatalogOrder()
    {
        var capabilities = new EncoderCapabilitySet(
        [
            Capability(VideoEncoder.H264Nvenc, true, ["nv12"], [8]),
            Capability(VideoEncoder.H264Qsv, true, ["nv12"], [8])
        ],
        "ffmpeg-test");
        var fingerprint = MachineFingerprintService.Create(capabilities);
        var benchmark = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            fingerprint,
            "ffmpeg-test",
            [
                Benchmark(VideoEncoder.H264Nvenc, "h264_nvenc", true, 1.80, 54),
                Benchmark(VideoEncoder.H264Qsv, "h264_qsv", true, 3.00, 90)
            ],
            DateTimeOffset.UtcNow);

        var decision = new AutoEncoderSelectionService().Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H264,
            CompressionProfile.Balanced,
            EncoderTuningPreset.Balanced,
            capabilities,
            benchmark,
            1920,
            1080,
            30,
            PerformanceMode: PerformanceMode.SpeedPriority));

        Assert.Equal(VideoEncoder.H264Qsv, decision.SelectedEncoder);
        Assert.Contains("本机基准", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoWithoutBenchmarkUsesHeuristicAndStaleBenchmarkIsLowConfidence()
    {
        var capabilities = new EncoderCapabilitySet(
        [
            Capability(VideoEncoder.Libx265, false, ["yuv420p"], [8]),
            Capability(VideoEncoder.HevcQsv, true, ["nv12"], [8])
        ],
        "ffmpeg-current");
        var service = new AutoEncoderSelectionService();
        var withoutBenchmark = service.Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H265,
            CompressionProfile.Balanced,
            EncoderTuningPreset.Balanced,
            capabilities,
            null,
            3840,
            2160,
            60));
        var stale = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            new MachineFingerprint(Environment.ProcessorCount, "ffmpeg-old", ["libx265", "hevc_qsv"], "old"),
            "ffmpeg-old",
            [Benchmark(VideoEncoder.Libx265, "libx265", false, 2, 120)],
            DateTimeOffset.UtcNow.AddDays(-30));
        var staleDecision = service.Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H265,
            CompressionProfile.Balanced,
            EncoderTuningPreset.Balanced,
            capabilities,
            stale,
            1920,
            1080,
            30));

        Assert.Equal(VideoEncoder.HevcQsv, withoutBenchmark.SelectedEncoder);
        Assert.Equal(BenchmarkConfidence.Unknown, withoutBenchmark.Confidence);
        Assert.Contains("尚未进行本机性能测试", withoutBenchmark.Reason, StringComparison.Ordinal);
        Assert.Equal(BenchmarkConfidence.Low, staleDecision.Confidence);

        var oldButSameMachine = stale with
        {
            Machine = MachineFingerprintService.Create(capabilities),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-31)
        };
        var oldDecision = service.Select(new AutoEncoderSelectionRequest(
            VideoCodecKind.H265,
            CompressionProfile.Balanced,
            EncoderTuningPreset.Balanced,
            capabilities,
            oldButSameMachine,
            1920,
            1080,
            30));
        Assert.Equal(BenchmarkConfidence.Low, oldDecision.Confidence);
    }

    [Fact]
    public void ComplexityAwareSamplingPrefersInformativeInteriorWindows()
    {
        var signals = new[]
        {
            new VmafComplexitySignal(0, 1, 3, "scene-cut", true),
            new VmafComplexitySignal(8, 1, 18, "motion", true),
            new VmafComplexitySignal(18, 1, 55, "motion", true),
            new VmafComplexitySignal(28, 1, 120, "grain", true),
            new VmafComplexitySignal(39, 1, 1, "black", false)
        };

        var samples = VmafSampleSelector.SelectComplexityAware(45, 5, 3, signals);

        Assert.Equal(3, samples.Count);
        Assert.DoesNotContain(samples, sample => sample.StartSeconds == 0);
        Assert.DoesNotContain(samples, sample => sample.StartSeconds == 39);
        Assert.Contains(samples, sample => sample.Complexity == VmafComplexityClass.Low);
        Assert.Contains(samples, sample => sample.Complexity == VmafComplexityClass.Medium);
        Assert.Contains(samples, sample => sample.Complexity == VmafComplexityClass.High);
        Assert.All(samples, sample => Assert.NotNull(sample.ComplexityScore));
    }

    [Fact]
    public void ContainerPolicyValidatesCoverArtByContainer()
    {
        var source = Media(
            extension: ".mkv",
            streams:
            [
                new MediaStreamInfo(0, MediaStreamType.Video, "hevc"),
                new MediaStreamInfo(1, MediaStreamType.Video, "mjpeg", Disposition: new Dictionary<string, int> { ["attached_pic"] = 1 })
            ]);
        var policy = new ContainerCompatibilityPolicy();

        var mkv = policy.Audit(source, ".mkv", AudioMode.Copy);
        var mp4 = policy.Audit(source, ".mp4", AudioMode.Copy);

        Assert.True(mkv.BlocksExecution);
        Assert.Contains(mkv.Warnings, warning => warning.Contains("attached_pic", StringComparison.Ordinal));
        Assert.False(mp4.BlocksExecution);
        Assert.Equal(1, mp4.AttachedPictureCount);
        Assert.Contains("封面图", mp4.SummaryDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteBenchmarkCachePublishesOnlyAfterDurableWrite()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "benchmark.json");
        var capabilities = new EncoderCapabilitySet(
        [Capability(VideoEncoder.Libx264, false, ["yuv420p"], [8])],
        "ffmpeg-test");
        var machine = MachineFingerprintService.Create(capabilities);
        var first = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            machine,
            "ffmpeg-test",
            [Benchmark(VideoEncoder.Libx264, "libx264", false, 1.0, 30)],
            DateTimeOffset.UtcNow);
        var second = first with { CompletedAt = first.CompletedAt.AddMinutes(1) };

        using var cache = new EncoderBenchmarkCache(path);
        await cache.SaveCompleteAsync(first);
        Assert.Equal(first.CompletedAt, cache.GetBest(machine)!.CompletedAt);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SaveCompleteAsync(second, cancelled.Token));
        Assert.Equal(first.CompletedAt, cache.GetBest(machine)!.CompletedAt);
    }

    [Fact]
    public void HelpParserDoesNotInventBitDepthWhenResponseHasNoFormatSection()
    {
        var result = EncoderHelpParser.Parse(VideoEncoder.Libx265, "Encoder libx265 help without format details");

        Assert.True(result.Succeeded);
        Assert.Empty(result.SupportedPixelFormats);
        Assert.Empty(result.SupportedBitDepths);
    }

    [Fact]
    public void HelpParserKeepsTenBitOnlyFormatEvidenceExact()
    {
        var result = EncoderHelpParser.Parse(
            VideoEncoder.HevcNvenc,
            "Supported pixel formats: p010le");

        Assert.Equal([10], result.SupportedBitDepths);
        Assert.DoesNotContain(8, result.SupportedBitDepths);
    }

    [Fact]
    public void PixelFormatIsSufficientToDetectTenBitHdrWhenRawBitDepthIsMissing()
    {
        var source = Media(
            pixelFormat: "yuv420p10le",
            bitDepth: null,
            colorPrimaries: "bt2020",
            colorTransfer: "smpte2084");

        Assert.Equal(10, source.EffectiveBitDepth);
        Assert.True(source.IsHdr);
        Assert.Contains("10-bit", source.HdrSummaryDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityDoesNotTreatTenBitOnlyEvidenceAsEightBitEvidence()
    {
        var capability = Capability(VideoEncoder.HevcNvenc, true, ["p010le"], [10]);

        Assert.False(capability.SupportsBitDepth(8));
        Assert.True(capability.SupportsBitDepth(10));
    }

    private static EncoderCapability Capability(
        VideoEncoder encoder,
        bool hardware,
        IReadOnlyList<string> formats,
        IReadOnlyList<int> depths) =>
        new(
            CompressionPlan.FfmpegEncoderName(encoder),
            EncoderCatalog.Get(encoder).DisplayName,
            encoder,
            EncoderCatalog.Get(encoder).Codec,
            EncoderCatalog.Get(encoder).Vendor,
            hardware,
            true,
            true,
            null)
        {
            InitializationTestPassed = true,
            SupportedPixelFormats = formats,
            SupportedBitDepths = depths,
            SupportedPresets = EncoderStrategyCatalog.Get(encoder).SupportedPresets,
            SupportedRateControls = EncoderStrategyCatalog.Get(encoder).SupportedRateControls
        };

    private static EncoderBenchmarkResult Benchmark(
        VideoEncoder encoder,
        string id,
        bool hardware,
        double speed,
        double fps) =>
        new(
            id,
            EncoderCatalog.Get(encoder).Codec,
            EncoderCatalog.Get(encoder).Vendor,
            hardware,
            1920,
            1080,
            30,
            EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.Balanced),
            4,
            4 / Math.Max(speed, 0.01),
            speed,
            fps,
            true,
            null,
            DateTimeOffset.UtcNow,
            "ffmpeg-test",
            BenchmarkConfidence.High,
            "1080p30",
            encoder);

    private static VideoFileInfo Media(
        string extension = ".mp4",
        string videoCodec = "h264",
        string? pixelFormat = "yuv420p",
        int? bitDepth = 8,
        string? profile = null,
        string? colorPrimaries = null,
        string? colorTransfer = null,
        string? colorSpace = null,
        IReadOnlyDictionary<string, string>? mastering = null,
        IReadOnlyList<MediaStreamInfo>? streams = null) => new()
    {
        FileName = $"source{extension}",
        FullPath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}{extension}"),
        Extension = extension,
        FileSizeBytes = 20_000_000,
        DurationSeconds = 45,
        VideoCodec = videoCodec,
        VideoBitrateBps = 6_000_000,
        TotalBitrateBps = 6_200_000,
        Width = 1920,
        Height = 1080,
        FrameRate = 30,
        AudioCodec = "aac",
        AudioBitrateBps = 192_000,
        AudioTrackCount = 1,
        PixelFormat = pixelFormat,
        BitDepth = bitDepth,
        VideoProfile = profile,
        ColorPrimaries = colorPrimaries,
        ColorTransfer = colorTransfer,
        ColorSpace = colorSpace,
        MasteringDisplayMetadata = mastering ?? new Dictionary<string, string>(),
        Streams = streams ?? []
    };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CloudLightVideoCompressorP1Tests",
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
