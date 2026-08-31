using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class SmartCompressionAndCapabilityTests
{
    private readonly RuleEngine _ruleEngine = new();

    [Theory]
    [InlineData(RuleComparison.GreaterThan, true)]
    [InlineData(RuleComparison.GreaterOrEqual, true)]
    [InlineData(RuleComparison.LessThan, false)]
    [InlineData(RuleComparison.LessOrEqual, false)]
    [InlineData(RuleComparison.Equal, false)]
    [InlineData(RuleComparison.NotEqual, true)]
    public void ConditionComparisons_ShowConcreteActualAndExpectedValues(RuleComparison comparison, bool expected)
    {
        var result = _ruleEngine.Evaluate(Media(totalBitrate: 13_710_000), [
            new CompressionRule
            {
                Field = RuleField.TotalBitrate,
                Comparison = comparison,
                Value = "10 Mbps"
            }
        ]);

        Assert.Equal(expected, result.IsMatch);
        Assert.Contains("13.71 Mbps", result.Details.Single());
        Assert.Contains("10 Mbps", result.Details.Single());
        Assert.Contains(expected ? "✓" : "✗", result.Details.Single());
    }

    [Fact]
    public void ConditionEvaluation_ReportsAndOrDetailsAndEmptyRules()
    {
        var media = Media(fileSizeBytes: 920L * 1024 * 1024, totalBitrate: 3_320_000);
        var andResult = _ruleEngine.Evaluate(media, [
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "500 MB" },
            new CompressionRule { JoinWithPrevious = RuleJoin.And, Field = RuleField.TotalBitrate, Comparison = RuleComparison.GreaterThan, Value = "10 Mbps" }
        ]);
        var orResult = _ruleEngine.Evaluate(media, [
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "500 MB" },
            new CompressionRule { JoinWithPrevious = RuleJoin.Or, Field = RuleField.TotalBitrate, Comparison = RuleComparison.GreaterThan, Value = "10 Mbps" }
        ]);
        var emptyResult = _ruleEngine.Evaluate(media, []);

        Assert.Equal(ConditionResultState.DoesNotMatch, andResult.State);
        Assert.Contains("✓", andResult.Details[0]);
        Assert.Contains("AND ✗", andResult.Details[1]);
        Assert.Contains("最终结果：不符合", andResult.Tooltip);
        Assert.True(orResult.IsMatch);
        Assert.Contains("OR ✗", orResult.Details[1]);
        Assert.Equal(ConditionResultState.AllAllowed, emptyResult.State);
        Assert.Equal("全部允许", emptyResult.Summary[..4]);
    }

    [Fact]
    public void ConditionEvaluation_RejectsAnUnavailableOperandEvenWhenOrWouldMatch()
    {
        var result = _ruleEngine.Evaluate(Media(), [
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "500 MB" },
            new CompressionRule { JoinWithPrevious = RuleJoin.Or, Field = RuleField.TotalBitrate, Comparison = RuleComparison.GreaterThan, Value = "not-a-bitrate" }
        ]);

        Assert.Equal(ConditionResultState.Failed, result.State);
        Assert.False(result.IsMatch);
        Assert.Contains("判断失败", result.Tooltip);
    }

    [Fact]
    public void SmartPlanner_SkipsReasonableH264AndEfficientCodecs()
    {
        var planner = new SmartCompressionPlanner();

        var low720p = planner.CreateDecision(Media(width: 1280, height: 720, frameRate: 30, videoBitrate: 3_000_000, totalBitrate: 3_192_000, fileSizeBytes: 225L * 1024 * 1024), new AppSettings());
        var balanced1080p = planner.CreateDecision(Media(width: 1920, height: 1080, frameRate: 30, videoBitrate: 6_000_000, totalBitrate: 6_192_000, fileSizeBytes: 450L * 1024 * 1024), new AppSettings());
        var hevc4k = planner.CreateDecision(Media(width: 3840, height: 2160, frameRate: 60, videoBitrate: 18_000_000, totalBitrate: 18_192_000, videoCodec: "hevc", fileSizeBytes: 1_350L * 1024 * 1024), new AppSettings());
        var av1 = planner.CreateDecision(Media(width: 1920, height: 1080, frameRate: 30, videoBitrate: 5_000_000, totalBitrate: 5_192_000, videoCodec: "av1", fileSizeBytes: 375L * 1024 * 1024), new AppSettings());

        Assert.False(low720p.ShouldCompress);
        Assert.False(balanced1080p.ShouldCompress);
        Assert.False(hevc4k.ShouldCompress);
        Assert.False(av1.ShouldCompress);
        Assert.Contains("跳过", hevc4k.Explanation);
        Assert.Contains("AV1", av1.Reason);
    }

    [Fact]
    public void SmartPlanner_CompressesHighRateH264AndRaisesBudgetFor60Fps()
    {
        var planner = new SmartCompressionPlanner();
        var h2641080p = planner.CreateDecision(Media(width: 1920, height: 1080, frameRate: 30, videoBitrate: 25_000_000, totalBitrate: 25_192_000, fileSizeBytes: 1_800L * 1024 * 1024), new AppSettings());
        var h2644k = planner.CreateDecision(Media(width: 3840, height: 2160, frameRate: 60, videoBitrate: 60_000_000, totalBitrate: 60_192_000, fileSizeBytes: 4_500L * 1024 * 1024), new AppSettings());
        var h2644k30 = planner.CreateDecision(Media(width: 3840, height: 2160, frameRate: 30, videoBitrate: 60_000_000, totalBitrate: 60_192_000, fileSizeBytes: 4_500L * 1024 * 1024), new AppSettings());

        Assert.True(h2641080p.ShouldCompress);
        Assert.True(h2644k.ShouldCompress);
        Assert.True(h2644k.TargetVideoBitrateBps > h2641080p.TargetVideoBitrateBps);
        Assert.True(h2644k.TargetVideoBitrateBps > h2644k30.TargetVideoBitrateBps);
        Assert.Contains("建议压缩", h2644k.Explanation);
    }

    [Fact]
    public void SmartPlanner_AppliesMaximumAndRemotePlaybackBudgets()
    {
        var planner = new SmartCompressionPlanner();
        var cappedSettings = new AppSettings
        {
            SmartMaximumVideoBitrateMbps = 10
        };
        var capped = planner.CreateDecision(Media(width: 3840, height: 2160, frameRate: 60, videoBitrate: 60_000_000, totalBitrate: 60_192_000, fileSizeBytes: 4_500L * 1024 * 1024), cappedSettings);

        var remoteSettings = new AppSettings
        {
            SmartPreset = SmartCompressionPreset.RemotePlayback,
            RemotePlaybackBandwidthMbps = 12,
            RemotePlaybackSafetyRatio = 0.70,
            AudioMode = AudioMode.Aac,
            AudioBitrateKbps = 192
        };
        var remote = planner.CreateDecision(Media(width: 3840, height: 2160, frameRate: 60, videoBitrate: 60_000_000, totalBitrate: 60_192_000, fileSizeBytes: 4_500L * 1024 * 1024), remoteSettings);

        Assert.True(capped.TargetVideoBitrateBps <= 10_000_000);
        Assert.True(remote.TargetVideoBitrateBps < 12_000_000);
        Assert.True(remote.TargetVideoBitrateBps + remote.TargetAudioBitrateBps < 12_000_000);
        Assert.Contains("安全系数", remote.Explanation);
    }

    [Fact]
    public void EncoderSelection_UsesNvidiaThenQsvThenAmfThenCpu()
    {
        var settings = new AppSettings
        {
            EncoderSelection = EncoderSelectionMode.HardwareAutomatic,
            TargetVideoCodec = VideoCodecKind.H265
        };
        var nvencAndQsv = Capabilities(VideoEncoder.HevcNvenc, VideoEncoder.HevcQsv);
        var qsvOnly = Capabilities(VideoEncoder.HevcQsv);
        var amfOnly = Capabilities(VideoEncoder.HevcAmf);
        var none = EncoderCapabilitySet.SoftwareDefaults;

        Assert.Equal(VideoEncoder.HevcNvenc, EncoderSelectionResolver.Resolve(settings, VideoCodecKind.H265, nvencAndQsv).SelectedEncoder);
        Assert.Equal(VideoEncoder.HevcQsv, EncoderSelectionResolver.Resolve(settings, VideoCodecKind.H265, qsvOnly).SelectedEncoder);
        Assert.Equal(VideoEncoder.HevcAmf, EncoderSelectionResolver.Resolve(settings, VideoCodecKind.H265, amfOnly).SelectedEncoder);
        Assert.Equal(VideoEncoder.Libx265, EncoderSelectionResolver.Resolve(settings, VideoCodecKind.H265, none).SelectedEncoder);

        var fallback = EncoderSelectionResolver.Resolve(settings, VideoCodecKind.H265, nvencAndQsv);
        Assert.Equal([VideoEncoder.HevcQsv, VideoEncoder.Libx265], fallback.FallbackEncoders);
    }

    [Theory]
    [InlineData(VideoCodecKind.H264, EncoderSelectionMode.NvidiaNvenc, VideoEncoder.H264Nvenc, VideoEncoder.Libx264)]
    [InlineData(VideoCodecKind.H265, EncoderSelectionMode.NvidiaNvenc, VideoEncoder.HevcNvenc, VideoEncoder.Libx265)]
    [InlineData(VideoCodecKind.H264, EncoderSelectionMode.IntelQsv, VideoEncoder.H264Qsv, VideoEncoder.Libx264)]
    [InlineData(VideoCodecKind.H265, EncoderSelectionMode.IntelQsv, VideoEncoder.HevcQsv, VideoEncoder.Libx265)]
    [InlineData(VideoCodecKind.H264, EncoderSelectionMode.AmdAmf, VideoEncoder.H264Amf, VideoEncoder.Libx264)]
    [InlineData(VideoCodecKind.H265, EncoderSelectionMode.AmdAmf, VideoEncoder.HevcAmf, VideoEncoder.Libx265)]
    public void UnavailableHardware_FallsBackToSoftwareWithoutChangingTargetCodec(
        VideoCodecKind targetCodec,
        EncoderSelectionMode selectionMode,
        VideoEncoder unavailableHardware,
        VideoEncoder expectedSoftwareEncoder)
    {
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            TargetVideoCodec = targetCodec,
            EncoderSelection = selectionMode,
            VideoEncoder = unavailableHardware
        };

        var plan = new CompressionPlanner().CreatePlan(
            Media(videoCodec: targetCodec == VideoCodecKind.H264 ? "h264" : "hevc"),
            settings,
            capabilities: EncoderCapabilitySet.SoftwareDefaults);

        Assert.Equal(expectedSoftwareEncoder, plan.Encoder);
        Assert.Equal(targetCodec, plan.TargetCodec);
        Assert.Equal(targetCodec, plan.EffectiveTargetCodec);
        Assert.Equal(targetCodec, EncoderCatalog.Get(plan.Encoder).Codec);
    }

    [Fact]
    public void EncoderArgumentBuilders_UseEncoderSpecificQualityControls()
    {
        var planner = new CompressionPlanner();

        var nvenc = planner.CreatePlan(Media(), new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 24,
            TargetVideoCodec = VideoCodecKind.H264,
            EncoderSelection = EncoderSelectionMode.NvidiaNvenc
        }, capabilities: Capabilities(VideoEncoder.H264Nvenc));
        var nvencArguments = nvenc.BuildArguments("input.mp4", "output.mp4", false);
        Assert.Contains("-cq", nvencArguments);
        Assert.DoesNotContain("-crf", nvencArguments);
        Assert.Contains("p4", nvencArguments);

        var qsv = planner.CreatePlan(Media(), new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 24,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.IntelQsv
        }, capabilities: Capabilities(VideoEncoder.HevcQsv));
        var qsvArguments = qsv.BuildArguments("input.mp4", "output.mp4", false);
        Assert.Contains("-global_quality", qsvArguments);
        Assert.DoesNotContain("-crf", qsvArguments);

        var amf = planner.CreatePlan(Media(), new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 24,
            TargetVideoCodec = VideoCodecKind.H264,
            EncoderSelection = EncoderSelectionMode.AmdAmf
        }, capabilities: Capabilities(VideoEncoder.H264Amf));
        var amfArguments = amf.BuildArguments("input.mp4", "output.mp4", false);
        Assert.Contains("-rc", amfArguments);
        Assert.Contains("cqp", amfArguments);
        Assert.Contains("-qp_i", amfArguments);
        Assert.DoesNotContain("-crf", amfArguments);
    }

    [Fact]
    public void VideoTaskItem_DisplaysDistinctConditionAndSmartSkipReasons()
    {
        var item = new VideoTaskItem(Media());
        var condition = _ruleEngine.Evaluate(item.Media, [
            new CompressionRule { Field = RuleField.TotalBitrate, Comparison = RuleComparison.GreaterThan, Value = "100 Mbps" }
        ]);
        item.ApplyConditionResult(condition);
        item.Status = VideoTaskStatus.Skipped;

        Assert.Equal("不符合", item.ConditionDisplay);
        Assert.Equal("条件跳过", item.StatusDisplay);

        var smartDecision = new SmartCompressionPlanner().CreateDecision(
            Media(width: 1280, height: 720, videoBitrate: 3_000_000, totalBitrate: 3_192_000),
            new AppSettings());
        item.ApplyConditionResult(new ConditionEvaluationResult(
            ConditionResultState.Matches,
            true,
            "已满足压缩条件。",
            [],
            "最终结果：符合"));
        item.ApplySmartDecision(smartDecision);
        Assert.Equal("智能跳过", item.StatusDisplay);
    }

    [Fact]
    public async Task SettingsService_LoadsLegacyJsonWithoutNewFields()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var legacyJson = """{"CompressionMode":0,"VideoEncoder":1,"Crf":28,"Rules":[]}""";
        await File.WriteAllTextAsync(settingsPath, legacyJson);

        var loaded = await new SettingsService(settingsPath).LoadAsync();

        Assert.Equal(CompressionMode.Crf, loaded.CompressionMode);
        Assert.Equal(VideoEncoder.Libx265, loaded.VideoEncoder);
        Assert.Null(loaded.EncoderSelection);
        Assert.Null(loaded.TargetVideoCodec);
        Assert.Equal(VideoCodecKind.H265, loaded.SelectedVideoCodec);
        Assert.Equal(SmartCompressionPreset.Balanced, loaded.SmartPreset);
        Assert.Equal(PerformanceMode.Automatic, loaded.PerformanceMode);
        Assert.True(loaded.PreventSleepDuringCompression);
    }

    [Fact]
    public async Task SettingsService_RoundTripsSmartAndEncoderChoices()
    {
        using var directory = new TemporaryDirectory();
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.SmartAutomatic,
            SmartPreset = SmartCompressionPreset.RemotePlayback,
            EncoderSelection = EncoderSelectionMode.HardwareAutomatic,
            TargetVideoCodec = VideoCodecKind.H264,
            RemotePlaybackBandwidthMbps = 12,
            RemotePlaybackSafetyRatio = 0.72,
            SmartMaximumVideoBitrateMbps = 10,
            SmartMinimumExpectedSavingRatio = 0.11,
            SmartQualityFactor = 1.08,
            PerformanceMode = PerformanceMode.LowEndStable,
            PreventSleepDuringCompression = false
        };

        var service = new SettingsService(Path.Combine(directory.Path, "settings.json"));
        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal(CompressionMode.SmartAutomatic, loaded.CompressionMode);
        Assert.Equal(SmartCompressionPreset.RemotePlayback, loaded.SmartPreset);
        Assert.Equal(EncoderSelectionMode.HardwareAutomatic, loaded.EncoderSelection);
        Assert.Equal(VideoCodecKind.H264, loaded.TargetVideoCodec);
        Assert.Equal(0.72, loaded.RemotePlaybackSafetyRatio);
        Assert.Equal(10, loaded.SmartMaximumVideoBitrateMbps);
        Assert.Equal(0.11, loaded.SmartMinimumExpectedSavingRatio);
        Assert.Equal(1.08, loaded.SmartQualityFactor);
        Assert.Equal(PerformanceMode.LowEndStable, loaded.PerformanceMode);
        Assert.False(loaded.PreventSleepDuringCompression);
    }

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
                usable ? null : "测试中不可用");
        }));

    private static VideoFileInfo Media(
        long fileSizeBytes = 2_000_000_000,
        double durationSeconds = 600,
        int width = 1920,
        int height = 1080,
        double frameRate = 30,
        long videoBitrate = 12_000_000,
        long totalBitrate = 12_192_000,
        string videoCodec = "h264") => new()
    {
        FileName = "movie.mp4",
        FullPath = Path.Combine(Path.GetTempPath(), "movie.mp4"),
        Extension = ".mp4",
        FileSizeBytes = fileSizeBytes,
        DurationSeconds = durationSeconds,
        VideoCodec = videoCodec,
        VideoBitrateBps = videoBitrate,
        TotalBitrateBps = totalBitrate,
        Width = width,
        Height = height,
        FrameRate = frameRate,
        AudioCodec = "aac",
        AudioBitrateBps = 192_000,
        AudioTrackCount = 1
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorTests", Guid.NewGuid().ToString("N"));
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
