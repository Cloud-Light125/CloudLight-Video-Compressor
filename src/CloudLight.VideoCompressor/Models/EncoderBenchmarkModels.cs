using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Models;

public sealed record EncoderBenchmarkWorkload(
    string Id,
    string DisplayName,
    int Width,
    int Height,
    double Fps,
    double MediaDurationSeconds)
{
    public string ResolutionDisplay => $"{Width}×{Height} · {Fps:0.##} FPS";
}

public static class EncoderBenchmarkWorkloads
{
    public static IReadOnlyList<EncoderBenchmarkWorkload> Default { get; } =
    [
        new("1080p30", "1080p30", 1920, 1080, 30, 4),
        new("4k60", "4K60", 3840, 2160, 60, 2)
    ];
}

/// <summary>
/// A deliberately small, local-only machine identity. It contains no serial
/// numbers, usernames, file paths or other identifying hardware data.
/// </summary>
public sealed record MachineFingerprint(
    int LogicalProcessorCount,
    string FFmpegVersion,
    IReadOnlyList<string> AvailableEncoders,
    string CapabilityFingerprint,
    int SchemaVersion = 1)
{
    public string StableValue => string.Join(
        "|",
        SchemaVersion,
        LogicalProcessorCount,
        FFmpegVersion,
        string.Join(',', AvailableEncoders.Order(StringComparer.OrdinalIgnoreCase)),
        CapabilityFingerprint);

    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StableValue))).ToLowerInvariant();
}

public sealed record EncoderBenchmarkResult(
    string EncoderId,
    VideoCodecKind Codec,
    EncoderVendor Vendor,
    bool IsHardware,
    int Width,
    int Height,
    double Fps,
    string Preset,
    double MediaDuration,
    double WallClockDuration,
    double AverageSpeed,
    double AverageFps,
    bool Success,
    string? FailureReason,
    DateTimeOffset MeasuredAt,
    string? FFmpegVersion = null,
    BenchmarkConfidence Confidence = BenchmarkConfidence.High,
    string? WorkloadId = null,
    VideoEncoder? Encoder = null)
{
    public string WorkloadDisplay => string.IsNullOrWhiteSpace(WorkloadId)
        ? $"{Width}×{Height} · {Fps:0.##} FPS"
        : WorkloadId!;

    public string SpeedDisplay => Success && AverageSpeed > 0
        ? $"约 {AverageSpeed:0.##}x"
        : "不可用";

    public string SummaryDisplay => Success
        ? $"{EncoderId} · {WorkloadDisplay} · {SpeedDisplay}"
        : $"{EncoderId} · {WorkloadDisplay} · 不可用";

    public string DetailDisplay => Success
        ? $"{SpeedDisplay} · {AverageFps:0.##} FPS · wall time {WallClockDuration:0.##} 秒 · {Preset}"
        : FailureReason ?? "未返回错误文本";
}

public sealed record EncoderBenchmarkProgress(
    int Completed,
    int Total,
    string CurrentEncoderId,
    string CurrentEncoderDisplay,
    string CurrentWorkloadDisplay,
    bool IsComplete = false)
{
    public string Display => IsComplete
        ? $"已完成 {Completed} / {Total}"
        : $"{Completed} / {Total} · {CurrentEncoderDisplay} · {CurrentWorkloadDisplay}";
}

public sealed record EncoderBenchmarkSnapshot(
    int SchemaVersion,
    MachineFingerprint Machine,
    string FFmpegVersion,
    IReadOnlyList<EncoderBenchmarkResult> Results,
    DateTimeOffset CompletedAt,
    bool IsComplete = true)
{
    public static TimeSpan FreshnessWindow => TimeSpan.FromDays(30);

    public bool IsFresh =>
        CompletedAt <= DateTimeOffset.UtcNow.AddMinutes(5) &&
        DateTimeOffset.UtcNow - CompletedAt <= FreshnessWindow;

    public bool Matches(MachineFingerprint fingerprint) =>
        IsComplete &&
        SchemaVersion == EncoderBenchmarkCache.CurrentSchemaVersion &&
        string.Equals(Machine.Hash, fingerprint.Hash, StringComparison.OrdinalIgnoreCase);

    public BenchmarkConfidence ConfidenceFor(MachineFingerprint fingerprint) =>
        Matches(fingerprint) && IsFresh
            ? BenchmarkConfidence.High
            : Matches(fingerprint)
                ? BenchmarkConfidence.Low
            : Results.Count == 0
                ? BenchmarkConfidence.Unknown
                : BenchmarkConfidence.Low;
}

public sealed record EncoderBenchmarkRunResult(
    IReadOnlyList<EncoderBenchmarkResult> Results,
    EncoderBenchmarkSnapshot? Snapshot,
    bool Completed,
    bool Cancelled,
    string? Message = null);

public sealed record EncoderBenchmarkDisplayRow(
    string EncoderId,
    string DisplayName,
    bool IsAvailable,
    string Status,
    IReadOnlyList<EncoderBenchmarkResult> Results)
{
    public string StatusDisplay => IsAvailable ? Status : $"不可用 · {Status}";
}
