using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record CompressionTaskSessionLoadResult(
    CompressionTaskSession? Session,
    string? Warning = null)
{
    public bool HasRecoverableSession => Session is not null;
}

internal sealed class CompressionTaskSessionDocument
{
    public int SchemaVersion { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool QueuePaused { get; set; }
    public string ScanRoot { get; set; } = string.Empty;
    public AppSettings SettingsSnapshot { get; set; } = new();
    public List<string> PlanningNotes { get; set; } = [];
    public List<CompressionTaskEntryDocument> Entries { get; set; } = [];
}

internal sealed class CompressionTaskEntryDocument
{
    public int QueueOrder { get; set; }
    public string JobId { get; set; } = string.Empty;
    public DateTimeOffset JobCreatedAt { get; set; }
    public MediaFileFingerprint? SourceFingerprint { get; set; }
    public VideoFileInfo Source { get; set; } = null!;
    public CompressionPlan Plan { get; set; } = null!;
    public ConditionEvaluationResult ConditionEvaluation { get; set; } = ConditionEvaluationResult.Pending;
    public CompressionPlanComparison Comparison { get; set; } = new([]);
    public CompressionExecutionState State { get; set; }
    public double ProgressPercent { get; set; }
    public VideoEncoder? ActualEncoder { get; set; }
    public string StatusDetail { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string? FallbackReason { get; set; }
    public VideoFileInfo? FinalVideoInfo { get; set; }
    public long? ActualOutputSizeBytes { get; set; }
    public CompressionJobResult? Result { get; set; }
}

/// <summary>
/// Persists a task page snapshot outside the installation directory. A bad
/// session record is treated as recoverable diagnostic data, never as a reason
/// to prevent the application from starting.
/// </summary>
public sealed class CompressionTaskSessionStore
{
    public const int CurrentSchemaVersion = 1;
    public const string SessionFileName = "compression-task-session.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _sessionPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public CompressionTaskSessionStore(string? sessionPath = null)
    {
        _sessionPath = sessionPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Video Compressor",
            "sessions",
            SessionFileName);
    }

    public string SessionPath => _sessionPath;

    public async Task SaveAsync(
        CompressionTaskSession session,
        CancellationToken cancellationToken = default,
        bool normalizeRunningStates = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var directory = Path.GetDirectoryName(_sessionPath) ?? throw new InvalidOperationException("任务会话目录无效。");
        Directory.CreateDirectory(directory);
        var tempPath = $"{_sessionPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Take the snapshot after acquiring the gate so a queued save can
            // never overwrite a newer state with a document captured earlier.
            var document = CreateDocument(session, normalizeRunningStates);
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_sessionPath))
            {
                try
                {
                    File.Replace(tempPath, _sessionPath, null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, _sessionPath, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(tempPath, _sessionPath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, _sessionPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _writeGate.Release();
        }
    }

    public async Task<CompressionTaskSessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionPath))
        {
            return new CompressionTaskSessionLoadResult(null);
        }

        try
        {
            await using var stream = File.OpenRead(_sessionPath);
            var document = await JsonSerializer.DeserializeAsync<CompressionTaskSessionDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                DiagnosticLog.Write("session", "SessionInvalidated：任务记录 schema 已变化，无法恢复。");
                return new CompressionTaskSessionLoadResult(null, "上次任务记录版本已变化，无法恢复；已安全忽略。");
            }

            var entries = new List<CompressionTaskEntry>();
            foreach (var snapshot in (document.Entries ?? []).OrderBy(entry => entry.QueueOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Source is null || snapshot.Plan is null)
                {
                    continue;
                }

                var source = snapshot.Source;
                var sourceChanged = false;
                if (File.Exists(source.FullPath))
                {
                    var currentFingerprint = MediaFileFingerprint.FromFile(source.FullPath);
                    sourceChanged = snapshot.SourceFingerprint is not null &&
                                    !snapshot.SourceFingerprint.Matches(currentFingerprint);
                    source = source.WithFileIdentity(currentFingerprint);
                }
                else
                {
                    sourceChanged = true;
                }

                var job = new CompressionJob(
                    string.IsNullOrWhiteSpace(snapshot.JobId) ? Guid.NewGuid().ToString("N") : snapshot.JobId,
                    source,
                    (document.SettingsSnapshot ?? new AppSettings()).Clone(),
                    snapshot.ConditionEvaluation,
                    document.ScanRoot,
                    snapshot.JobCreatedAt == default ? document.CreatedAt : snapshot.JobCreatedAt)
                {
                    Plan = snapshot.Plan
                };
                var entry = new CompressionTaskEntry(
                    source,
                    snapshot.Plan,
                    snapshot.ConditionEvaluation,
                    snapshot.Comparison,
                    job,
                    snapshot.SourceFingerprint);

                var state = snapshot.State;
                entry.RestoreSnapshot(
                    state,
                    snapshot.ProgressPercent,
                    snapshot.ActualEncoder,
                    snapshot.StatusDetail,
                    snapshot.FailureReason,
                    snapshot.FallbackReason,
                    snapshot.FinalVideoInfo,
                    snapshot.ActualOutputSizeBytes,
                    snapshot.Result);

                if (state is CompressionExecutionState.Compressing or
                    CompressionExecutionState.Verifying or
                    CompressionExecutionState.Committing)
                {
                    entry.MarkInterrupted();
                }
                if (sourceChanged)
                {
                    DiagnosticLog.Write(
                        "session",
                        $"SessionInvalidated：源文件 fingerprint 已变化或文件不存在：{source.FullPath}");
                    entry.MarkSourceChanged();
                }

                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                return new CompressionTaskSessionLoadResult(null);
            }

            if (!entries.Any(entry => entry.ExecutionState is
                    CompressionExecutionState.WaitingToStart or
                    CompressionExecutionState.Queued or
                    CompressionExecutionState.Compressing or
                    CompressionExecutionState.Verifying or
                    CompressionExecutionState.Committing or
                    CompressionExecutionState.Interrupted or
                    CompressionExecutionState.SourceChanged))
            {
                // A fully terminal session is history, not an unfinished task
                // that should interrupt startup with a recovery prompt.
                return new CompressionTaskSessionLoadResult(null);
            }

            var session = new CompressionTaskSession(
                entries,
                document.SettingsSnapshot ?? new AppSettings(),
                document.ScanRoot,
                document.PlanningNotes ?? [],
                executionPolicy: null,
                sessionId: document.SessionId,
                createdAt: document.CreatedAt,
                queuePaused: document.QueuePaused);
            DiagnosticLog.Write("session", $"SessionRecovered：{session.SessionId}，{entries.Count} 个任务。");
            return new CompressionTaskSessionLoadResult(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            var warning = "上次任务记录损坏，无法恢复。";
            DiagnosticLog.Write("session", $"{warning} {exception.Message}");
            return new CompressionTaskSessionLoadResult(null, warning);
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_sessionPath))
            {
                File.Delete(_sessionPath);
            }
        }
        catch (IOException exception)
        {
            DiagnosticLog.Write("session", $"删除任务记录失败：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            DiagnosticLog.Write("session", $"删除任务记录失败：{exception.Message}");
        }
    }

    private static CompressionTaskSessionDocument CreateDocument(
        CompressionTaskSession session,
        bool normalizeRunningStates) => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            QueuePaused = session.QueuePaused,
            ScanRoot = session.ScanRoot,
            SettingsSnapshot = session.SettingsSnapshot.Clone(),
            PlanningNotes = session.PlanningNotes.ToList(),
            Entries = session.Entries.Select((entry, index) => new CompressionTaskEntryDocument
            {
                QueueOrder = index,
                JobId = entry.Job.JobId,
                JobCreatedAt = entry.Job.CreatedAt,
                SourceFingerprint = entry.SourceFingerprint,
                Source = entry.Source,
                Plan = entry.Plan,
                ConditionEvaluation = entry.ConditionEvaluation,
                Comparison = entry.Comparison,
                State = normalizeRunningStates && entry.ExecutionState is
                    CompressionExecutionState.Compressing or
                    CompressionExecutionState.Verifying or
                    CompressionExecutionState.Committing
                    ? CompressionExecutionState.Interrupted
                    : entry.ExecutionState,
                ProgressPercent = normalizeRunningStates && entry.ExecutionState is
                    CompressionExecutionState.Compressing or
                    CompressionExecutionState.Verifying or
                    CompressionExecutionState.Committing
                    ? 0
                    : entry.ProgressPercent,
                ActualEncoder = entry.ActualEncoder,
                StatusDetail = normalizeRunningStates && entry.ExecutionState is
                    CompressionExecutionState.Compressing or
                    CompressionExecutionState.Verifying or
                    CompressionExecutionState.Committing
                    ? "上次应用退出时任务正在执行；恢复后将从头重新处理该视频。"
                    : entry.StatusDetail,
                FailureReason = entry.FailureReason,
                FallbackReason = entry.FallbackReason,
                FinalVideoInfo = entry.FinalVideoInfo,
                ActualOutputSizeBytes = entry.ActualOutputSizeBytes,
                Result = entry.Result
            }).ToList()
        };
}
