using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class UserScenarioIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealScenario_TotalBitrateRule_ProducesExpectedEligibleTasks()
    {
        var sampleRoot = Environment.GetEnvironmentVariable(
            "CLOUDLIGHT_VIDEO_COMPRESSOR_SCENARIO_ROOT")?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(sampleRoot))
        {
            Console.WriteLine("SKIP: 未配置 CLOUDLIGHT_VIDEO_COMPRESSOR_SCENARIO_ROOT，跳过真实用户场景验收。");
            return;
        }

        var toolDirectory = Environment.GetEnvironmentVariable("CLOUDLIGHT_FFMPEG_TEST_DIR")?.Trim().Trim('"');
        if (!Directory.Exists(sampleRoot))
        {
            Console.WriteLine("SKIP: CLOUDLIGHT_VIDEO_COMPRESSOR_SCENARIO_ROOT 指定的目录不存在，跳过真实用户场景验收。");
            return;
        }

        if (string.IsNullOrWhiteSpace(toolDirectory))
        {
            Console.WriteLine("SKIP: 未设置 CLOUDLIGHT_FFMPEG_TEST_DIR，跳过真实用户场景验收。");
            return;
        }

        var tools = new FFmpegTools(
            Path.Combine(toolDirectory, "ffmpeg.exe"),
            Path.Combine(toolDirectory, "ffprobe.exe"));
        if (!File.Exists(tools.FFmpegPath) || !File.Exists(tools.FFprobePath))
        {
            Console.WriteLine("SKIP: 真实用户场景的 FFmpeg/ffprobe 不存在。");
            return;
        }

        var rule = new CompressionRule
        {
            Field = RuleField.TotalBitrate,
            Comparison = RuleComparison.GreaterThan,
            Value = "10 Mbps"
        };
        var ruleEngine = new RuleEngine();
        var scanner = new VideoScannerService(new FFprobeService());
        var discovered = new List<VideoFileInfo>();
        await scanner.ScanAsync(
            sampleRoot,
            recursive: false,
            // This is a user-acceptance scan rather than a throughput benchmark.
            // Serial probing keeps the fixed 47-file assertion deterministic when
            // the full integration suite is running other FFmpeg processes.
            maximumProbeConcurrency: 1,
            tools,
            info =>
            {
                discovered.Add(info);
                return Task.CompletedTask;
            },
            (path, message) => throw new InvalidOperationException($"ffprobe 失败：{path}：{message}"),
            CancellationToken.None);

        var items = discovered
            .Select(info =>
            {
                var item = new VideoTaskItem(info);
                item.ApplyConditionResult(ruleEngine.Evaluate(info, [rule]));
                return item;
            })
            .ToList();
        var matchingItems = items.Where(item => item.ConditionResult.IsMatch).ToList();

        Assert.Equal(47, items.Count);
        Assert.Single(matchingItems);

        var planner = new CompressionTaskPlanner(
            ruleEngine,
            new FFprobeService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService());
        var manualSettings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 28,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.CpuSoftware
        };
        manualSettings.Rules.Add(rule);
        var manualSession = await planner.CreateSessionAsync(
            matchingItems,
            manualSettings,
            sampleRoot,
            tools,
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        Assert.Single(manualSession.Entries);
        Assert.Equal(VideoCodecKind.H265, manualSession.Entries[0].Plan.TargetCodec);
        Assert.Equal(CompressionMode.Crf, manualSession.Entries[0].Plan.Mode);

        var smartItem = new VideoTaskItem(matchingItems[0].Media);
        smartItem.ApplyConditionResult(ruleEngine.Evaluate(smartItem.Media, [rule]));
        var smartSettings = new AppSettings
        {
            CompressionMode = CompressionMode.SmartAutomatic,
            SmartPreset = SmartCompressionPreset.Balanced,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.CpuSoftware
        };
        smartSettings.Rules.Add(rule);
        var smartSession = await planner.CreateSessionAsync(
            [smartItem],
            smartSettings,
            sampleRoot,
            tools,
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        Assert.True(smartSession.Entries.Count is 0 or 1);
        if (smartSession.Entries.Count == 0)
        {
            Assert.Equal(VideoTaskStatus.Skipped, smartItem.Status);
            Assert.Contains("智能跳过", smartItem.StatusDetail);
        }

        Console.WriteLine($"真实场景：{items.Count} 个视频，符合 {matchingItems.Count} 个，手动计划 {manualSession.Entries.Count} 个，智能计划 {smartSession.Entries.Count} 个。");
    }
}
