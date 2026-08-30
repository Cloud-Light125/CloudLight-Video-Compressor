using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class RefactorArchitectureTests
{
    [Fact]
    public void EncoderStrategies_DoNotCrossApplyQualityControls()
    {
        var x265 = Plan(VideoEncoder.Libx265).BuildArguments("input.mp4", "output.mp4", false);
        var qsv = Plan(VideoEncoder.HevcQsv).BuildArguments("input.mp4", "output.mp4", false);
        var nvenc = Plan(VideoEncoder.HevcNvenc).BuildArguments("input.mp4", "output.mp4", false);
        var amf = Plan(VideoEncoder.HevcAmf).BuildArguments("input.mp4", "output.mp4", false);

        Assert.Contains("-crf", x265);
        Assert.DoesNotContain("-global_quality", x265);
        Assert.Contains("-global_quality", qsv);
        Assert.DoesNotContain("-crf", qsv);
        Assert.Contains("-cq", nvenc);
        Assert.Contains("-rc", nvenc);
        Assert.DoesNotContain("medium", nvenc);
        Assert.Contains("-qp_i", amf);
        Assert.Contains("cqp", amf);
        Assert.DoesNotContain("-crf", amf);
    }

    [Fact]
    public void PlanSnapshot_PreservesIdentityAndTargetCodecAcrossFallback()
    {
        var plan = Plan(VideoEncoder.HevcQsv, fallback: [VideoEncoder.Libx265]);
        var fallbackPlan = plan.WithEncoder(VideoEncoder.Libx265);
        var job = CompressionJob.Create(Media(), new AppSettings(), ConditionEvaluationResult.Pending, Path.GetTempPath()).WithPlan(plan);

        Assert.Equal(plan.PlanId, fallbackPlan.PlanId);
        Assert.Equal(VideoCodecKind.H265, fallbackPlan.EffectiveTargetCodec);
        Assert.Equal(VideoEncoder.HevcQsv, job.Plan!.Encoder);
        Assert.Equal(plan.PlanId, job.Plan.PlanId);
        Assert.Equal(VideoEncoder.Libx265, fallbackPlan.Encoder);
    }

    [Fact]
    public void SmartPlanner_ExposesBppfAndUsesHigherBudgetForHigherFps()
    {
        var planner = new SmartCompressionPlanner();
        var thirty = planner.CreateDecision(Media(frameRate: 30, videoBitrate: 25_000_000), new AppSettings());
        var sixty = planner.CreateDecision(Media(frameRate: 60, videoBitrate: 25_000_000), new AppSettings());

        Assert.NotNull(thirty.SourceBitsPerPixelPerFrame);
        Assert.NotNull(thirty.TargetBitsPerPixelPerFrame);
        Assert.True(sixty.TargetVideoBitrateBps > thirty.TargetVideoBitrateBps);
        Assert.InRange(sixty.CompressionBenefitScore, 0, 100);
    }

    [Fact]
    public void RuleEngine_ReturnsStructuredRuleValues()
    {
        var result = new RuleEngine().Evaluate(Media(totalBitrate: 13_710_000), [
            new CompressionRule
            {
                Field = RuleField.TotalBitrate,
                Comparison = RuleComparison.GreaterThan,
                Value = "10 Mbps"
            }
        ]);

        var rule = Assert.Single(result.Rules);
        Assert.Equal(RuleField.TotalBitrate, rule.Field);
        Assert.Equal("13.71 Mbps", rule.ActualValue);
        Assert.Equal("10 Mbps", rule.ExpectedValue);
        Assert.True(rule.Passed);
        Assert.True(rule.IsAvailable);
    }

    [Fact]
    public void ProgressTools_ExposeStableEtaOnlyAfterEnoughSamples()
    {
        var start = DateTimeOffset.UtcNow;
        var eta = new EtaCalculator(minimumSamples: 3, window: TimeSpan.FromSeconds(10));
        Assert.False(eta.Update(1, 10, start).IsStable);
        Assert.False(eta.Update(2, 10, start.AddSeconds(1)).IsStable);
        var stable = eta.Update(4, 10, start.AddSeconds(3));

        Assert.True(stable.IsStable);
        Assert.NotNull(stable.Remaining);
        Assert.InRange(stable.Remaining!.Value.TotalSeconds, 5.5, 6.5);
    }

    [Fact]
    public void ProgressWatchdog_AllowsStartupThenDetectsActualStall()
    {
        var start = DateTimeOffset.UtcNow;
        var watchdog = new EncoderProgressWatchdog(
            new EncoderProgressWatchdogOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), 1),
            start);
        var progress = new EncodingProgress(10, 30, "1x", 100, null, 1, 10, 30);

        watchdog.Observe(progress, start.AddSeconds(5.5));
        Assert.False(watchdog.Check(start.AddSeconds(6)).IsStalled);
        var decision = watchdog.Check(start.AddSeconds(9));

        Assert.True(decision.IsStalled);
        Assert.Contains("未观察到", decision.Reason);
    }

    [Fact]
    public void VmafSampling_UsesRepresentativeBeginningMiddleAndEnd()
    {
        var samples = VmafSampleSelector.Select(120, 8, 3);

        Assert.Equal(3, samples.Count);
        Assert.Equal(0, samples[0].StartSeconds);
        Assert.InRange(samples[1].StartSeconds, 55, 57);
        Assert.Equal(112, samples[2].StartSeconds);
        Assert.All(samples, sample => Assert.Equal(8, sample.DurationSeconds));
    }

    [Fact]
    public async Task WorkerPool_UsesConservativeCpuAndHardwareGates()
    {
        var cpuEntries = Enumerable.Range(0, 4).Select(_ => Entry(VideoEncoder.Libx265)).ToArray();
        var maxCpu = await MeasureMaximumConcurrencyAsync(cpuEntries, 4);
        var hardwareEntries = Enumerable.Range(0, 4).Select(_ => Entry(VideoEncoder.HevcQsv)).ToArray();
        var maxHardware = await MeasureMaximumConcurrencyAsync(hardwareEntries, 4);

        Assert.Equal(1, maxCpu);
        Assert.Equal(2, maxHardware);
    }

    [Fact]
    public void HistoryEntry_StoresMetadataWithoutVideoContent()
    {
        var result = new CompressionJobResult(
            VideoTaskStatus.Completed,
            "done",
            OutputPath: "output.mp4",
            SourceInfo: Media(fileSizeBytes: 1_000),
            OutputInfo: Media(fileSizeBytes: 600),
            Encoder: VideoEncoder.Libx265,
            PlannedEncoder: VideoEncoder.HevcQsv,
            PlanId: Guid.NewGuid());

        var entry = CompressionHistoryEntry.From(result);

        Assert.Equal(VideoEncoder.HevcQsv, entry.PlannedEncoder);
        Assert.Equal(VideoEncoder.Libx265, entry.ActualEncoder);
        Assert.Equal(0.4, entry.SavingRatio);
        Assert.DoesNotContain("output", entry.SourceFile, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> MeasureMaximumConcurrencyAsync(
        IReadOnlyList<CompressionTaskEntry> entries,
        int configuredConcurrency)
    {
        var active = 0;
        var maximum = 0;
        await new CompressionWorkerPool().ExecuteAsync(
            entries,
            configuredConcurrency,
            async _ =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maximum, now);
                await Task.Delay(40);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);
        return maximum;
    }

    private static void InterlockedMax(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (value <= current || Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }

    private static CompressionTaskEntry Entry(VideoEncoder encoder) =>
        new(Media(), Plan(encoder), ConditionEvaluationResult.Pending,
            new CompressionPlanComparison([]));

    private static CompressionPlan Plan(VideoEncoder encoder, IReadOnlyList<VideoEncoder>? fallback = null) =>
        new(
            false,
            encoder,
            CompressionMode.Crf,
            24,
            "medium",
            null,
            null,
            null,
            AudioMode.Copy,
            192,
            ".mp4",
            [],
            FallbackEncoders: fallback,
            TargetCodec: EncoderCatalog.Get(encoder).Codec);

    private static VideoFileInfo Media(
        long fileSizeBytes = 2_000_000_000,
        double durationSeconds = 600,
        int width = 1920,
        int height = 1080,
        double frameRate = 30,
        long videoBitrate = 12_000_000,
        long totalBitrate = 12_192_000)
        => new()
        {
            FileName = "movie.mp4",
            FullPath = Path.Combine(Path.GetTempPath(), "movie.mp4"),
            Extension = ".mp4",
            FileSizeBytes = fileSizeBytes,
            DurationSeconds = durationSeconds,
            VideoCodec = "h264",
            VideoBitrateBps = videoBitrate,
            TotalBitrateBps = totalBitrate,
            Width = width,
            Height = height,
            FrameRate = frameRate,
            AudioCodec = "aac",
            AudioBitrateBps = 192_000,
            AudioTrackCount = 1
        };
}
