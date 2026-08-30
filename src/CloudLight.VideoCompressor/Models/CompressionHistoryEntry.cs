using CloudLight.VideoCompressor.Infrastructure;

namespace CloudLight.VideoCompressor.Models;

/// <summary>
/// Lightweight, path-and-metadata-only history. It never stores video bytes or
/// media content, and it is intentionally independent from the task queue.
/// </summary>
public sealed record CompressionHistoryEntry(
    DateTimeOffset Date,
    string SourceFile,
    VideoTaskStatus Result,
    VideoEncoder? PlannedEncoder,
    VideoEncoder? ActualEncoder,
    long BeforeSizeBytes,
    long? AfterSizeBytes,
    double? SavingRatio,
    TimeSpan? Duration,
    string? FailureReason,
    Guid? PlanId)
{
    public string DateDisplay => Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string FileNameDisplay => string.IsNullOrWhiteSpace(SourceFile) ? "—" : Path.GetFileName(SourceFile);
    public string ResultDisplay => Result switch
    {
        VideoTaskStatus.Completed => "已完成",
        VideoTaskStatus.Cancelled => "已取消",
        VideoTaskStatus.Skipped when !string.IsNullOrWhiteSpace(FailureReason) => "已放弃结果",
        VideoTaskStatus.Skipped => "已跳过",
        _ => Result.GetDescription()
    };
    public string PlannedEncoderDisplay => PlannedEncoder is { } encoder
        ? EncoderCatalog.Get(encoder).DisplayName
        : "—";
    public string ActualEncoderDisplay => ActualEncoder is { } encoder
        ? EncoderCatalog.Get(encoder).DisplayName
        : "—";
    public string BeforeSizeDisplay => BeforeSizeBytes > 0 ? DisplayFormat.FileSize(BeforeSizeBytes) : "—";
    public string AfterSizeDisplay => AfterSizeBytes is > 0 ? DisplayFormat.FileSize(AfterSizeBytes.Value) : "—";
    public string SavingDisplay => SavingRatio is { } saving ? $"{saving:P1}" : "—";

    public static CompressionHistoryEntry From(CompressionJobResult result)
    {
        var source = result.SourceInfo;
        var attempts = result.Attempts;
        var duration = attempts is { Count: > 0 }
            ? attempts.Where(attempt => attempt.CompletedAt is not null)
                .Select(attempt => attempt.Duration)
                .Where(value => value is not null)
                .Aggregate<TimeSpan?, TimeSpan?>(null, (total, value) => total.GetValueOrDefault() + value!.Value)
            : null;
        var before = source?.FileSizeBytes ?? 0;
        var after = result.OutputInfo?.FileSizeBytes;
        double? saving = after is > 0 && before > 0 ? 1 - after.Value / (double)before : null;

        return new CompressionHistoryEntry(
            DateTimeOffset.UtcNow,
            source?.FullPath ?? string.Empty,
            result.Status,
            result.PlannedEncoder,
            result.Encoder,
            before,
            after,
            saving,
            duration,
            result.FailureKind == CompressionFailureKind.None ? null : result.Message,
            result.PlanId);
    }
}
