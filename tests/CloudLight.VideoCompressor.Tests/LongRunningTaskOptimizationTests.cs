using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class LongRunningTaskOptimizationTests
{
    [Fact]
    public void AutomaticPolicy_UsesConservativeLimitsOnlyForConstrainedSignals()
    {
        var constrained = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { CompressionConcurrency = 4, ProbeConcurrency = 8 },
            logicalProcessorCount: 4,
            availableMemoryBytes: 4L * 1024 * 1024 * 1024);
        var capableDesktop = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { CompressionConcurrency = 4, ProbeConcurrency = 8 },
            new EncoderCapabilitySet([
                new EncoderCapability("hevc_qsv", "Intel Quick Sync · H.265", VideoEncoder.HevcQsv,
                    VideoCodecKind.H265, EncoderVendor.Intel, true, true, true, null),
                new EncoderCapability("hevc_nvenc", "NVIDIA NVENC · H.265", VideoEncoder.HevcNvenc,
                    VideoCodecKind.H265, EncoderVendor.Nvidia, true, true, true, null)
            ]),
            [VideoEncoder.HevcQsv],
            logicalProcessorCount: 12,
            availableMemoryBytes: 32L * 1024 * 1024 * 1024);

        Assert.Equal(PerformanceMode.LowEndStable, constrained.Mode);
        Assert.Equal(1, constrained.MaxCpuWorkers);
        Assert.Equal(1, constrained.MaxHardwareWorkers);
        Assert.Equal(1, constrained.MaxTotalWorkers);
        Assert.Equal(1, constrained.ProbeConcurrency);
        Assert.Equal(ProcessPriorityMode.BelowNormal, constrained.ProcessPriority);
        Assert.Equal(SoftwareThreadPolicy.ReserveSystemCores, constrained.SoftwareThreadPolicy);
        Assert.Equal(3, constrained.SoftwareThreadCount);

        Assert.Equal(PerformanceMode.Balanced, capableDesktop.Mode);
        Assert.Equal(1, capableDesktop.MaxCpuWorkers);
        Assert.Equal(2, capableDesktop.MaxHardwareWorkers);
        Assert.Equal(3, capableDesktop.MaxTotalWorkers);
        Assert.Equal(2, capableDesktop.ProbeConcurrency);
        Assert.Equal(ProcessPriorityMode.Normal, capableDesktop.ProcessPriority);
    }

    [Fact]
    public async Task LowEndWorkerPolicy_AllowsOnlyOneCpuAndOneHardwareLane()
    {
        var policy = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { PerformanceMode = PerformanceMode.LowEndStable, CompressionConcurrency = 4 },
            logicalProcessorCount: 12,
            availableMemoryBytes: 32L * 1024 * 1024 * 1024);
        var entries = Enumerable.Range(0, 4).Select(_ => Entry(VideoEncoder.Libx265)).ToArray();
        var maximum = 0;
        var active = 0;

        await new CompressionWorkerPool().ExecuteAsync(
            entries,
            policy,
            async _ =>
            {
                var current = Interlocked.Increment(ref active);
                InterlockedMax(ref maximum, current);
                await Task.Delay(25);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        Assert.Equal(1, maximum);
        Assert.Equal(1, policy.MaxCpuWorkers);
        Assert.Equal(1, policy.MaxHardwareWorkers);
    }

    [Fact]
    public void Eta_UsesSmoothedSpeedAndDoesNotExposeStartupGuess()
    {
        var start = DateTimeOffset.UtcNow;
        var calculator = new EtaCalculator(5, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));
        var speeds = new[] { 0.50, 0.52, 0.49, 0.51, 0.50 };
        EtaEstimate estimate = new(null, false);
        for (var index = 0; index < speeds.Length; index++)
        {
            estimate = calculator.Update(
                index * 30,
                3_600,
                start.AddSeconds(index * 3),
                speeds[index]);
            if (index < 4)
            {
                Assert.False(estimate.IsStable);
                Assert.Null(estimate.Remaining);
            }
        }

        Assert.True(estimate.IsStable);
        Assert.InRange(estimate.SmoothedSpeed!.Value, 0.49, 0.52);
        Assert.InRange(estimate.Remaining!.Value.TotalSeconds, 6_700, 7_300);
        Assert.Equal(EtaConfidence.Low, estimate.Confidence);
    }

    [Fact]
    public void Eta_UsesProcessedDurationAndSmoothedHalfSpeed()
    {
        var start = DateTimeOffset.UtcNow;
        var calculator = new EtaCalculator(5, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));
        EtaEstimate estimate = new(null, false);
        for (var index = 0; index < 5; index++)
        {
            estimate = calculator.Update(index * 450, 3_600, start.AddSeconds(index * 3), 0.5);
        }

        Assert.True(estimate.IsStable);
        Assert.Equal(3_600, estimate.Remaining!.Value.TotalSeconds, precision: 3);
        Assert.Equal(0.5, estimate.SmoothedSpeed!.Value, precision: 3);
    }

    [Fact]
    public void Eta_IgnoresInvalidSpeedAndKeepsBoundedSamples()
    {
        var start = DateTimeOffset.UtcNow;
        var calculator = new EtaCalculator(5, TimeSpan.FromSeconds(120));
        calculator.Update(0, 100, start, 0);
        calculator.Update(1, 100, start.AddSeconds(1), double.NaN);
        calculator.Update(2, 100, start.AddSeconds(2), double.PositiveInfinity);
        calculator.Update(3, 100, start.AddSeconds(3), -1);
        calculator.Update(4, 100, start.AddSeconds(4), null);
        var estimate = calculator.Update(5, 100, start.AddSeconds(5), 0);

        Assert.True(estimate.IsStable);
        Assert.True(double.IsFinite(estimate.SmoothedSpeed!.Value));
        Assert.True(estimate.SmoothedSpeed > 0);

        for (var index = 6; index < 100_006; index++)
        {
            calculator.Update(index, 100_000, start.AddMilliseconds(index * 10d), 0.5);
        }

        Assert.InRange(calculator.SampleCount, 1, 512);
    }

    [Fact]
    public void Watchdog_AllowsVerySlowButMonotonicProgress()
    {
        var start = DateTimeOffset.UtcNow;
        var watchdog = new EncoderProgressWatchdog(
            new EncoderProgressWatchdogOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(45), 1),
            start);

        watchdog.Observe(Progress(1, 30, 0.05), start.AddSeconds(5.5));
        Assert.False(watchdog.Check(start.AddSeconds(40)).IsStalled);
        watchdog.Observe(Progress(2, 60, 0.05), start.AddSeconds(40.5));
        Assert.False(watchdog.Check(start.AddSeconds(84)).IsStalled);
        Assert.True(watchdog.Check(start.AddSeconds(86)).IsStalled);
    }

    [Fact]
    public void FallbackProgress_ClearsPreviousEtaSamples()
    {
        var entry = Entry(VideoEncoder.HevcQsv);
        entry.ApplyProgress(new WorkflowProgress(
            VideoTaskStatus.Compressing,
            "编码中",
            new EncodingProgress(
                45,
                18,
                "2x",
                null,
                TimeSpan.FromSeconds(30),
                ProcessedDurationSeconds: 90,
                TotalDurationSeconds: 200,
                IsEtaStable: true,
                SmoothedSpeed: 2,
                EtaSampleCount: 8,
                EtaConfidence: EtaConfidence.Medium),
            Encoder: VideoEncoder.HevcQsv));
        Assert.Contains("约", entry.ProgressEtaDisplay);

        entry.ApplyProgress(new WorkflowProgress(
            VideoTaskStatus.Compressing,
            "正在回退",
            Encoder: VideoEncoder.Libx265,
            ResetEta: true));

        Assert.Equal("计算中", entry.ProgressEtaDisplay);
        Assert.Null(entry.LatestEncoding);
    }

    [Fact]
    public void QueueEta_UsesPerTaskDurationAndWorkerLanes()
    {
        var history = new EncodingPerformanceHistory();
        var estimator = new CompressionDurationEstimator();
        var cpuEntries = new[]
        {
            Entry(VideoEncoder.Libx265, "cpu-10.mp4", 10),
            Entry(VideoEncoder.Libx265, "cpu-20.mp4", 20),
            Entry(VideoEncoder.Libx265, "cpu-30.mp4", 30)
        };
        foreach (var entry in cpuEntries)
        {
            history.Record(entry.Source, entry.Plan, VideoEncoder.Libx265,
                TimeSpan.FromSeconds(entry.Source.DurationSeconds!.Value));
        }

        var oneWorker = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { PerformanceMode = PerformanceMode.LowEndStable },
            logicalProcessorCount: 4,
            availableMemoryBytes: 4L * 1024 * 1024 * 1024);
        var single = estimator.EstimateQueue(cpuEntries, oneWorker, history);
        Assert.Equal(60, single.Remaining!.Value.TotalSeconds, precision: 3);

        var hardwareEntries = new[]
        {
            Entry(VideoEncoder.HevcQsv, "qsv-10.mp4", 10),
            Entry(VideoEncoder.HevcQsv, "qsv-20.mp4", 20),
            Entry(VideoEncoder.HevcQsv, "qsv-30.mp4", 30)
        };
        foreach (var entry in hardwareEntries)
        {
            history.Record(entry.Source, entry.Plan, VideoEncoder.HevcQsv,
                TimeSpan.FromSeconds(entry.Source.DurationSeconds!.Value));
        }

        var twoHardwareWorkers = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { PerformanceMode = PerformanceMode.SpeedPriority, CompressionConcurrency = 2 },
            logicalProcessorCount: 12,
            availableMemoryBytes: 32L * 1024 * 1024 * 1024);
        var parallel = estimator.EstimateQueue(hardwareEntries, twoHardwareWorkers, history);
        Assert.Equal(40, parallel.Remaining!.Value.TotalSeconds, precision: 3);

        var mixed = new[]
        {
            Entry(VideoEncoder.Libx265, "mixed-cpu.mp4", 10),
            Entry(VideoEncoder.HevcQsv, "mixed-qsv.mp4", 20)
        };
        var mixedEstimate = estimator.EstimateQueue(mixed, twoHardwareWorkers, history);
        Assert.Equal(20, mixedEstimate.Remaining!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void QueueEta_UsesStableActiveSpeedForSimilarWaitingTask()
    {
        var active = Entry(VideoEncoder.Libx265, "active.mp4", 600);
        active.ApplyProgress(new WorkflowProgress(
            VideoTaskStatus.Compressing,
            "编码中",
            new EncodingProgress(
                20,
                18,
                "2x",
                null,
                TimeSpan.FromSeconds(240),
                ProcessedDurationSeconds: 120,
                TotalDurationSeconds: 600,
                IsEtaStable: true,
                SmoothedSpeed: 2,
                EtaSampleCount: 10,
                EtaConfidence: EtaConfidence.Medium),
            Encoder: VideoEncoder.Libx265));
        var waiting = Entry(VideoEncoder.Libx265, "waiting.mp4", 600);
        var policy = LongRunningTaskPolicyResolver.Resolve(
            new AppSettings { PerformanceMode = PerformanceMode.LowEndStable },
            logicalProcessorCount: 12,
            availableMemoryBytes: 32L * 1024 * 1024 * 1024);

        var estimated = new CompressionDurationEstimator().TryEstimateEntry(
            waiting,
            policy,
            new EncodingPerformanceHistory(),
            [active, waiting],
            out var duration,
            out var confidence);

        Assert.True(estimated);
        Assert.Equal(300, duration.TotalSeconds, precision: 3);
        Assert.Equal(EtaConfidence.Medium, confidence);
    }

    [Fact]
    public void PerformanceHistory_CalibratesNextSimilarTaskWithoutCrossMachinePersistence()
    {
        var source = Media("observed.mp4", 600, 1920, 1080, 30);
        var plan = Plan(VideoEncoder.Libx265, source);
        var history = new EncodingPerformanceHistory();
        history.Record(source, plan, VideoEncoder.Libx265, TimeSpan.FromSeconds(1_200));
        Assert.Equal(1, history.Count);

        var estimator = new CompressionDurationEstimator();
        Assert.True(estimator.TryEstimateEncodingDuration(source, plan, history, out var first, out var firstConfidence));
        Assert.Equal(EtaConfidence.Medium, firstConfidence);
        Assert.Equal(1_200, first.TotalSeconds, precision: 3);

        history.Record(source, plan, VideoEncoder.Libx265, TimeSpan.FromSeconds(1_800));
        Assert.True(estimator.TryEstimateEncodingDuration(source, plan, history, out var recalibrated, out var confidence));
        Assert.Equal(EtaConfidence.Medium, confidence);
        Assert.InRange(recalibrated.TotalSeconds, 1_200, 1_800);
    }

    private static EncodingProgress Progress(long frame, double processed, double speed) =>
        new(0, null, $"{speed:0.00}x", null, null, processed, 600, frame, null);

    private static CompressionTaskEntry Entry(
        VideoEncoder encoder,
        string fileName = "movie.mp4",
        double durationSeconds = 600) =>
        new(
            Media(fileName, durationSeconds),
            Plan(encoder, Media(fileName, durationSeconds)),
            new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许"),
            new CompressionPlanComparison([]));

    private static CompressionPlan Plan(VideoEncoder encoder, VideoFileInfo source) =>
        new(
            false,
            encoder,
            CompressionMode.Crf,
            26,
            "medium",
            null,
            null,
            null,
            AudioMode.Copy,
            192,
            ".mp4",
            [],
            TargetCodec: EncoderCatalog.Get(encoder).Codec)
        {
            InputInfo = source
        };

    private static VideoFileInfo Media(
        string fileName,
        double durationSeconds,
        int width = 1920,
        int height = 1080,
        double fps = 30) => new()
        {
            FileName = fileName,
            FullPath = Path.Combine(Path.GetTempPath(), fileName),
            Extension = ".mp4",
            FileSizeBytes = 100_000_000,
            DurationSeconds = durationSeconds,
            VideoCodec = "h264",
            VideoBitrateBps = 8_000_000,
            Width = width,
            Height = height,
            FrameRate = fps,
            AudioTrackCount = 1,
            AudioCodec = "aac",
            AudioBitrateBps = 128_000
        };

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
}
