using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record EncoderBenchmarkOptions(
    IReadOnlyList<EncoderBenchmarkWorkload>? Workloads = null,
    TimeSpan? PerTestTimeout = null,
    int? MediaDurationSeconds = null,
    int? FourKMediaDurationSeconds = null)
{
    public IReadOnlyList<EncoderBenchmarkWorkload> EffectiveWorkloads =>
        Workloads ?? EncoderBenchmarkWorkloads.Default;

    public TimeSpan EffectivePerTestTimeout =>
        PerTestTimeout is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromSeconds(25);

    public EncoderBenchmarkWorkload AdjustDuration(EncoderBenchmarkWorkload workload)
    {
        var seconds = workload.Id.Equals("4k60", StringComparison.OrdinalIgnoreCase)
            ? FourKMediaDurationSeconds.GetValueOrDefault((int)Math.Round(workload.MediaDurationSeconds))
            : MediaDurationSeconds.GetValueOrDefault((int)Math.Round(workload.MediaDurationSeconds));
        return seconds == workload.MediaDurationSeconds
            ? workload
            : workload with { MediaDurationSeconds = Math.Max(1, seconds) };
    }
}

/// <summary>
/// Runs a bounded, user-triggered benchmark against synthetic lavfi sources.
/// It uses the same encoder strategy that a real plan uses and never reads a
/// user video or changes the user's long-running performance mode.
/// </summary>
public sealed class EncoderBenchmarkService
{
    private readonly FFmpegService _ffmpegService;
    private readonly EncoderBenchmarkCache _cache;

    public EncoderBenchmarkService(
        FFmpegService? ffmpegService = null,
        EncoderBenchmarkCache? cache = null)
    {
        _ffmpegService = ffmpegService ?? new FFmpegService();
        _cache = cache ?? new EncoderBenchmarkCache();
    }

    public EncoderBenchmarkCache Cache => _cache;

    public async Task<EncoderBenchmarkRunResult> RunAsync(
        FFmpegTools tools,
        EncoderCapabilitySet capabilities,
        CancellationToken cancellationToken,
        IProgress<EncoderBenchmarkProgress>? progress = null,
        EncoderBenchmarkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(capabilities);
        options ??= new EncoderBenchmarkOptions();

        var available = capabilities.Capabilities
            .Where(capability =>
                capability.Available &&
                capability.Codec is VideoCodecKind.H264 or VideoCodecKind.H265)
            .GroupBy(capability => capability.Encoder)
            .Select(group => group.Last())
            .OrderBy(capability => capability.Encoder)
            .ToArray();
        var workloads = options.EffectiveWorkloads.Select(options.AdjustDuration).ToArray();
        var total = available.Length * workloads.Length;
        var results = new List<EncoderBenchmarkResult>(total);
        var completed = 0;
        DiagnosticLog.Write("benchmark", $"BenchmarkStarted：{total} 项，FFmpeg {capabilities.FFmpegVersion ?? "(unknown)"}");
        progress?.Report(new EncoderBenchmarkProgress(0, total, string.Empty, string.Empty, "准备中"));

        try
        {
            foreach (var capability in available)
            {
                foreach (var workload in workloads)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new EncoderBenchmarkProgress(
                        completed,
                        total,
                        capability.Id,
                        capability.DisplayName,
                        workload.DisplayName));
                    var result = await RunOneAsync(
                        tools,
                        capability,
                        workload,
                        capabilities.FFmpegVersion,
                        options.EffectivePerTestTimeout,
                        cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    completed++;
                    progress?.Report(new EncoderBenchmarkProgress(
                        completed,
                        total,
                        capability.Id,
                        capability.DisplayName,
                        workload.DisplayName,
                        completed == total));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Write("benchmark", $"BenchmarkCancelled：已完成 {completed} / {total}");
            return new EncoderBenchmarkRunResult(
                results,
                null,
                false,
                true,
                "Benchmark 已取消；旧结果保持不变。");
        }

        if (available.Length == 0 || workloads.Length == 0)
        {
            DiagnosticLog.Write("benchmark", "BenchmarkCompleted：没有可用的 H.264/H.265 encoder 或 workload");
            return new EncoderBenchmarkRunResult([], null, true, false, "当前没有通过能力检测的 H.264/H.265 encoder。");
        }

        var machine = MachineFingerprintService.Create(capabilities);
        var snapshot = new EncoderBenchmarkSnapshot(
            EncoderBenchmarkCache.CurrentSchemaVersion,
            machine,
            capabilities.FFmpegVersion ?? "(unknown)",
            results,
            DateTimeOffset.UtcNow);
        try
        {
            await _cache.SaveCompleteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Write("benchmark", "BenchmarkCancelled：结果已完成但持久化被取消，旧结果保持不变。");
            return new EncoderBenchmarkRunResult(results, null, false, true, "Benchmark 保存已取消；旧结果保持不变。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("benchmark", $"Benchmark 已完成，但结果保存失败：{exception.Message}");
        }

        DiagnosticLog.Write("benchmark", $"BenchmarkCompleted：{results.Count} 项");
        return new EncoderBenchmarkRunResult(
            results,
            snapshot,
            true,
            false,
            "Benchmark 已完成；结果仅保存在本机。");
    }

    private async Task<EncoderBenchmarkResult> RunOneAsync(
        FFmpegTools tools,
        EncoderCapability capability,
        EncoderBenchmarkWorkload workload,
        string? ffmpegVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var encoder = capability.Encoder;
        var preset = EncoderTuningCatalog.Resolve(encoder, EncoderTuningPreset.Balanced, legacyPreset: null);
        var plan = new CompressionPlan(
            false,
            encoder,
            CompressionMode.Crf,
            24,
            preset,
            null,
            null,
            null,
            AudioMode.Copy,
            192,
            ".mkv",
            [])
        {
            EncoderTuningPreset = EncoderTuningPreset.Balanced,
            TargetBitDepth = 8,
            TargetPixelFormat = BitDepthPolicyResolver.TargetPixelFormat(encoder, 8),
            TargetProfile = null
        };
        var arguments = new List<string>
        {
            "-f", "lavfi",
            "-i", $"testsrc2=size={workload.Width}x{workload.Height}:rate={Format(workload.Fps)}:duration={Format(workload.MediaDurationSeconds)}",
            "-an",
            "-c:v", CompressionPlan.FfmpegEncoderName(encoder)
        };
        arguments.AddRange(EncoderStrategyCatalog.Get(encoder).BuildVideoArguments(plan));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-f", "null", "NUL"]);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            var run = await _ffmpegService.RunAsync(
                tools,
                arguments,
                workload.MediaDurationSeconds,
                progress: null,
                timeoutCancellation.Token,
                encoder: encoder).ConfigureAwait(false);
            stopwatch.Stop();
            var wallClockSeconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            var speed = run.AverageSpeed is > 0
                ? run.AverageSpeed.Value
                : workload.MediaDurationSeconds / wallClockSeconds;
            return new EncoderBenchmarkResult(
                capability.Id,
                capability.Codec,
                capability.Vendor,
                capability.IsHardware,
                workload.Width,
                workload.Height,
                workload.Fps,
                preset,
                workload.MediaDurationSeconds,
                wallClockSeconds,
                run.Succeeded ? Math.Max(0, speed) : 0,
                run.Succeeded ? Math.Max(0, speed * workload.Fps) : 0,
                run.Succeeded,
                run.Succeeded ? null : CompactFailure(run.ErrorOutput, run.FailureMessage),
                DateTimeOffset.UtcNow,
                ffmpegVersion,
                BenchmarkConfidence.High,
                workload.Id,
                encoder);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new EncoderBenchmarkResult(
                capability.Id,
                capability.Codec,
                capability.Vendor,
                capability.IsHardware,
                workload.Width,
                workload.Height,
                workload.Fps,
                preset,
                workload.MediaDurationSeconds,
                stopwatch.Elapsed.TotalSeconds,
                0,
                0,
                false,
                $"Benchmark 超时（单项上限 {timeout.TotalSeconds:0.#} 秒）。",
                DateTimeOffset.UtcNow,
                ffmpegVersion,
                BenchmarkConfidence.High,
                workload.Id,
                encoder);
        }
    }

    private static string CompactFailure(string error, string? failureMessage)
    {
        var text = string.IsNullOrWhiteSpace(failureMessage) ? error : $"{failureMessage}\n{error}";
        text = string.IsNullOrWhiteSpace(text) ? "未返回错误文本。" : text.Trim();
        return text.Length <= 1_000 ? text : text[^1_000..];
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
