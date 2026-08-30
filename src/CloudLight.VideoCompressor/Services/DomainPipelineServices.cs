using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Small composition-root friendly services that make the domain pipeline
/// explicit without introducing a container or a ceremony-heavy framework.
/// Existing concrete services remain the implementation behind these seams.
/// </summary>
public sealed class MediaDiscoveryService
{
    private readonly VideoScannerService _scanner;

    public MediaDiscoveryService(VideoScannerService scanner) => _scanner = scanner;

    public IAsyncEnumerable<string> DiscoverAsync(string directory, bool recursive, CancellationToken cancellationToken) =>
        _scanner.EnumerateVideoPathsAsync(directory, recursive, cancellationToken);
}

public sealed class MediaProbeService
{
    private readonly FFprobeService _probe;

    public MediaProbeService(FFprobeService probe) => _probe = probe;

    public Task<VideoFileInfo> ProbeAsync(FFmpegTools tools, string path, CancellationToken cancellationToken) =>
        _probe.ProbeAsync(tools, path, cancellationToken);
}

public sealed class CompressionEligibilityService
{
    private readonly RuleEngine _rules;

    public CompressionEligibilityService(RuleEngine rules) => _rules = rules;

    public ConditionEvaluationResult Evaluate(VideoFileInfo source, IEnumerable<CompressionRule> rules) =>
        _rules.Evaluate(source, rules);
}

public sealed class CompressionPlanningService
{
    private readonly CompressionPlanner _planner;

    public CompressionPlanningService(CompressionPlanner planner) => _planner = planner;

    public CompressionPlan CreatePlan(
        VideoFileInfo source,
        AppSettings settings,
        TargetSizeCalculation? targetSize,
        EncoderCapabilitySet? capabilities) =>
        _planner.CreatePlan(source, settings, targetSize, capabilities);
}

public sealed class EncoderCapabilityService
{
    private readonly EncoderCapabilityDetector _detector;

    public EncoderCapabilityService(EncoderCapabilityDetector detector) => _detector = detector;

    public Task<EncoderCapabilitySet> DetectAsync(FFmpegTools tools, CancellationToken cancellationToken) =>
        _detector.DetectAsync(tools, cancellationToken);
}

public sealed class CompressionExecutionService
{
    private readonly FFmpegService _ffmpeg;

    public CompressionExecutionService(FFmpegService ffmpeg) => _ffmpeg = ffmpeg;

    public Task<FFmpegRunResult> ExecuteAsync(
        FFmpegTools tools,
        IReadOnlyList<string> arguments,
        double? durationSeconds,
        IProgress<EncodingProgress>? progress,
        CancellationToken cancellationToken) =>
        _ffmpeg.RunAsync(tools, arguments, durationSeconds, progress, cancellationToken);
}

public sealed class OutputValidationService
{
    private readonly SafeFileService _safeFiles;

    public OutputValidationService(SafeFileService safeFiles) => _safeFiles = safeFiles;

    public Task<OutputValidationResult> ValidateAsync(
        FFmpegTools tools,
        VideoFileInfo source,
        string outputPath,
        CompressionPlan? plan,
        CancellationToken cancellationToken) =>
        _safeFiles.ValidateOutputAsync(tools, source, outputPath, plan, cancellationToken);
}

public sealed class SafeCommitService
{
    private readonly SafeFileService _safeFiles;

    public SafeCommitService(SafeFileService safeFiles) => _safeFiles = safeFiles;

    public OriginalMoveResult MoveOriginal(VideoFileInfo source, string destination) =>
        _safeFiles.MoveOriginal(source, destination);

    public void Commit(string temporaryPath, string finalPath) =>
        _safeFiles.FinalizeTemporaryOutput(temporaryPath, finalPath);

    public OriginalMoveRollbackResult Rollback(OriginalMoveResult? move) =>
        _safeFiles.TryRollbackOriginalMove(move);
}

/// <summary>
/// Shared entry point for scan-preview-execute and direct processing. Direct
/// processing may discover/probe one file at a time, but it still ends at this
/// same planned execution method.
/// </summary>
public sealed class CompressionSessionService
{
    private readonly CompressionTaskPlanner _taskPlanner;
    private readonly CompressionWorkflowService _workflow;

    public CompressionSessionService(
        CompressionTaskPlanner taskPlanner,
        CompressionWorkflowService workflow)
    {
        _taskPlanner = taskPlanner;
        _workflow = workflow;
    }

    public Task<CompressionTaskSession> CreatePreviewAsync(
        IEnumerable<VideoTaskItem> candidates,
        AppSettings settings,
        string scanRoot,
        FFmpegTools tools,
        EncoderCapabilitySet capabilities,
        CancellationToken cancellationToken) =>
        _taskPlanner.CreateSessionAsync(candidates, settings, scanRoot, tools, capabilities, cancellationToken);

    public Task<CompressionJobResult> ExecuteAsync(
        CompressionJob job,
        FFmpegTools tools,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken) =>
        job.Plan is null
            ? throw new InvalidOperationException("CompressionJob 尚未生成 CompressionPlan。")
            : _workflow.ProcessJobAsync(
                job,
                tools,
                progress,
                cancellationToken);
}
