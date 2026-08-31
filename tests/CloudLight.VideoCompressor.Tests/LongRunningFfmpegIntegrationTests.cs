using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

[Collection("Long-running FFmpeg")]
public sealed class LongRunningFfmpegIntegrationTests
{
    private const string ToolDirectoryVariable = "CLOUDLIGHT_FFMPEG_TEST_DIR";
    private const int SourceDurationSeconds = 180;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealLowEndPolicy_Libx265LongTaskCompletesWithStableEta()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过 180 秒 libx265 长任务实测。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "cpu-long-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);
        var source = await new FFprobeService().ProbeAsync(tools, sourcePath, CancellationToken.None);
        var settings = LowEndSettings(VideoEncoder.Libx265);
        var plan = new CompressionPlanner().CreatePlan(source, settings) with
        {
            SourcePath = sourcePath,
            TargetPath = Path.Combine(directory.Path, "cpu-long-output.mp4")
        };

        var run = await RunWorkflowAsync(source, settings, plan, tools, directory.Path);
        var result = run.Result;

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.Libx265, result.Encoder);
        Assert.True(File.Exists(result.OutputPath), result.Message);
        Assert.NotEmpty(result.Attempts!);
        Assert.True(result.Attempts!.Last().AverageSpeed is > 0, "libx265 attempt did not retain an average speed.");
        AssertStableEtaAndNoFalseStall(result, run.Progress, directory.Path, "CPU libx265");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealLowEndPolicy_HevcQsvLongTaskCompletesWithStableEta()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过 180 秒 hevc_qsv 长任务实测。");
            return;
        }

        var capabilities = await new EncoderCapabilityDetector().DetectAsync(tools, CancellationToken.None);
        if (capabilities.Get(VideoEncoder.HevcQsv)?.IsUsable != true)
        {
            Console.WriteLine("SKIP: 当前设备 hevc_qsv 不可用，跳过 180 秒 QSV 长任务实测。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "qsv-long-source.mp4");
        await CreateVideoAsync(tools.FFmpegPath, sourcePath);
        var probe = new FFprobeService();
        var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
        var settings = LowEndSettings(VideoEncoder.HevcQsv);
        settings.EncoderSelection = EncoderSelectionMode.IntelQsv;
        var plan = new CompressionPlanner().CreatePlan(source, settings, capabilities: capabilities) with
        {
            SourcePath = sourcePath,
            TargetPath = Path.Combine(directory.Path, "qsv-long-output.mp4")
        };
        Assert.Equal(VideoEncoder.HevcQsv, plan.Encoder);

        var run = await RunWorkflowAsync(source, settings, plan, tools, directory.Path, probe);
        var result = run.Result;

        Assert.Equal(VideoTaskStatus.Completed, result.Status);
        Assert.Equal(VideoEncoder.HevcQsv, result.Encoder);
        Assert.True(File.Exists(result.OutputPath), result.Message);
        Assert.NotEmpty(result.Attempts!);
        Assert.True(result.Attempts!.Last().AverageSpeed is > 0, "hevc_qsv attempt did not retain an average speed.");
        AssertStableEtaAndNoFalseStall(result, run.Progress, directory.Path, "Intel QSV");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealSession_ThreeVideoQueueEtaLearnsAfterEachCompletedFile()
    {
        var tools = ResolveTools();
        if (tools is null)
        {
            Console.WriteLine($"SKIP: 未设置 {ToolDirectoryVariable} 或 FFmpeg 工具不存在，跳过三视频 Queue ETA 实测。");
            return;
        }

        using var directory = new TemporaryDirectory();
        var probe = new FFprobeService();
        var planner = new CompressionPlanner();
        var settings = LowEndSettings(VideoEncoder.Libx265);
        var entries = new List<CompressionTaskEntry>();
        foreach (var duration in new[] { 30, 60, 90 })
        {
            var sourcePath = Path.Combine(directory.Path, $"queue-{duration}.mp4");
            await CreateVideoAsync(tools.FFmpegPath, sourcePath, duration, "640x360");
            var source = await probe.ProbeAsync(tools, sourcePath, CancellationToken.None);
            var plan = planner.CreatePlan(source, settings) with
            {
                SourcePath = sourcePath,
                TargetPath = Path.Combine(directory.Path, $"queue-{duration}-output.mp4")
            };
            entries.Add(new CompressionTaskEntry(
                source,
                plan,
                new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许"),
                new CompressionPlanComparison([])));
        }

        var history = new EncodingPerformanceHistory();
        var estimator = new CompressionDurationEstimator();
        var policy = LongRunningTaskPolicyResolver.Resolve(
            settings,
            logicalProcessorCount: 12,
            availableMemoryBytes: 32L * 1024 * 1024 * 1024);
        var initial = estimator.EstimateQueue(entries, policy, history);
        Assert.Null(initial.Remaining);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var run = await RunWorkflowAsync(entry.Source, settings, entry.Plan, tools, directory.Path, probe);
            Assert.Equal(VideoTaskStatus.Completed, run.Result.Status);
            entry.ApplyResult(run.Result);
            history.Record(entry, run.Result);

            var estimate = estimator.EstimateQueue(entries, policy, history);
            Console.WriteLine($"Queue after {entry.FileName}: remaining={estimate.Remaining}, confidence={estimate.Confidence}, known={estimate.KnownTaskCount}, unknown={estimate.UnknownTaskCount}");
            if (index < entries.Count - 1)
            {
                Assert.True(estimate.Remaining is { } remaining && remaining > TimeSpan.Zero);
                Assert.Equal(0, estimate.UnknownTaskCount);
            }
        }

        var completed = estimator.EstimateQueue(entries, policy, history);
        Assert.Equal(TimeSpan.Zero, completed.Remaining);
        Assert.True(completed.IsKnown);
        Assert.Equal(3, history.Count);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories), path =>
            Path.GetFileName(path).Contains(".clvc-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(CompressionJobResult Result, IReadOnlyList<WorkflowProgress> Progress)> RunWorkflowAsync(
        VideoFileInfo source,
        AppSettings settings,
        CompressionPlan plan,
        FFmpegTools tools,
        string scanRoot,
        FFprobeService? probeService = null)
    {
        var probe = probeService ?? new FFprobeService();
        var progress = new CapturingProgress<WorkflowProgress>();
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
            scanRoot,
            progress,
            CancellationToken.None);
        return (result, progress.Items);
    }

    private static void AssertStableEtaAndNoFalseStall(
        CompressionJobResult result,
        IReadOnlyList<WorkflowProgress> progress,
        string directory,
        string label)
    {
        Assert.NotNull(result.Attempts);
        Assert.DoesNotContain(result.Attempts!, attempt => attempt.Status == CompressionAttemptStatus.Stalled);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), path =>
            Path.GetFileName(path).Contains(".clvc-", StringComparison.OrdinalIgnoreCase));

        Assert.NotEmpty(progress);

        var encoding = progress
            .Where(update => update.Encoding is not null)
            .Select(update => update.Encoding!)
            .Where(item => !item.IsStalled)
            .ToArray();
        Assert.NotEmpty(encoding);
        Assert.Contains(encoding, item => item.IsEtaStable && item.Remaining is not null && item.SmoothedSpeed is > 0);

        var points = new List<string>();
        foreach (var target in new[] { 20d, 50d, 80d })
        {
            var sample = encoding
                .Where(item => item.IsEtaStable && item.Remaining is not null && item.Percent >= target)
                .OrderBy(item => item.Percent)
                .FirstOrDefault();
            if (sample is null)
            {
                points.Add($"{target:0}%=未采到稳定样本");
                continue;
            }

            var actualDuration = result.Attempts!.Last().Duration?.TotalSeconds ?? 0;
            var actualRemaining = Math.Max(0, actualDuration - sample.Elapsed.GetValueOrDefault().TotalSeconds);
            var predicted = sample.Remaining!.Value.TotalSeconds;
            var errorRatio = actualRemaining <= 1
                ? 0
                : Math.Abs(predicted - actualRemaining) / actualRemaining;
            points.Add($"{target:0}%={predicted:0}s/实际约{actualRemaining:0}s(误差{errorRatio:P0})");
        }

        Console.WriteLine($"{label} ETA checkpoints: {string.Join("; ", points)}");
        Console.WriteLine($"{label} average speed: {result.Attempts!.Last().AverageSpeed:0.00}x, elapsed: {result.Attempts.Last().Duration}");
    }

    private static AppSettings LowEndSettings(VideoEncoder encoder) => new()
    {
        PerformanceMode = PerformanceMode.LowEndStable,
        PreventSleepDuringCompression = false,
        CompressionMode = CompressionMode.Crf,
        VideoEncoder = encoder,
        TargetVideoCodec = VideoCodecKind.H265,
        EncodingPreset = "medium",
        Crf = 30,
        OutputSuffix = "-long-output",
        DiscardIfLarger = false
    };

    private static FFmpegTools? ResolveTools()
    {
        var directory = Environment.GetEnvironmentVariable(ToolDirectoryVariable)?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        var ffprobe = Path.Combine(directory, "ffprobe.exe");
        return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FFmpegTools(ffmpeg, ffprobe) : null;
    }

    private static async Task CreateVideoAsync(
        string ffmpeg,
        string outputPath,
        int durationSeconds = SourceDurationSeconds,
        string size = "1920x1080")
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
            "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-shortest", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        var errorText = await error;
        Assert.True(process.ExitCode == 0, $"无法生成长任务测试视频：{errorText}");
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _items = new();

        public IReadOnlyList<T> Items => _items.ToArray();
        public int Count => _items.Count;

        public void Report(T value) => _items.Enqueue(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CloudLightVideoCompressorLongTests",
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

[CollectionDefinition("Long-running FFmpeg", DisableParallelization = true)]
public sealed class LongRunningFfmpegCollectionDefinition
{
}
