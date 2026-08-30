using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class CompressionWorkflowService
{
    private const double MaximumTargetSizeRatio = 1.03;
    private readonly RuleEngine _ruleEngine;
    private readonly FFprobeService _ffprobeService;
    private readonly FFmpegService _ffmpegService;
    private readonly CompressionPlanner _planner;
    private readonly TargetSizeCalculator _targetSizeCalculator;
    private readonly OutputPathService _outputPathService;
    private readonly SafeFileService _safeFileService;
    private readonly EncoderCapabilitySet? _defaultCapabilities;
    private readonly object _pathReservationLock = new();
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);

    public CompressionWorkflowService(
        RuleEngine ruleEngine,
        FFprobeService ffprobeService,
        FFmpegService ffmpegService,
        CompressionPlanner planner,
        TargetSizeCalculator targetSizeCalculator,
        OutputPathService outputPathService,
        SafeFileService safeFileService,
        EncoderCapabilitySet? defaultCapabilities = null)
    {
        _ruleEngine = ruleEngine;
        _ffprobeService = ffprobeService;
        _ffmpegService = ffmpegService;
        _planner = planner;
        _targetSizeCalculator = targetSizeCalculator;
        _outputPathService = outputPathService;
        _safeFileService = safeFileService;
        _defaultCapabilities = defaultCapabilities;
    }

    public Task<CompressionJobResult> ProcessFileAsync(
        VideoFileInfo initialInfo,
        AppSettings settings,
        FFmpegTools tools,
        string? scanRoot,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken,
        EncoderCapabilitySet? capabilities = null)
        => ProcessFileCoreAsync(
            initialInfo,
            settings,
            tools,
            scanRoot,
            progress,
            cancellationToken,
            capabilities,
            plannedPlan: null,
            plannedCondition: null);

    /// <summary>
    /// Executes the exact plan snapshot shown by the compression task page.
    /// The workflow keeps all existing temporary-file, validation, commit and
    /// rollback safeguards, but deliberately does not invoke a planner again.
    /// </summary>
    public Task<CompressionJobResult> ProcessPlannedFileAsync(
        VideoFileInfo source,
        AppSettings settings,
        CompressionPlan plan,
        ConditionEvaluationResult condition,
        FFmpegTools tools,
        string? scanRoot,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken)
        => ProcessFileCoreAsync(
            source,
            settings,
            tools,
            scanRoot,
            progress,
            cancellationToken,
            capabilities: null,
            plannedPlan: plan,
            plannedCondition: condition);

    private async Task<CompressionJobResult> ProcessFileCoreAsync(
        VideoFileInfo initialInfo,
        AppSettings settings,
        FFmpegTools tools,
        string? scanRoot,
        IProgress<WorkflowProgress>? progress,
        CancellationToken cancellationToken,
        EncoderCapabilitySet? capabilities,
        CompressionPlan? plannedPlan,
        ConditionEvaluationResult? plannedCondition)
    {
        string? temporaryOutputPath = null;
        string? passLogPrefix = null;
        OriginalMoveResult? originalMove = null;
        OutputPathReservation? pathReservation = null;
        var source = initialInfo;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var needsProbeForRules = _ruleEngine.RequiresProbe(settings.Rules);
            var needsProbeForPlan = CompressionPlanner.RequiresSourceProbe(settings);
            if (plannedPlan is null && (needsProbeForRules || needsProbeForPlan) && !source.HasProbeData)
            {
                progress?.Report(new WorkflowProgress(VideoTaskStatus.Analyzing, "正在用 ffprobe 分析当前文件…"));
                source = await _ffprobeService.ProbeAsync(tools, source.FullPath, cancellationToken);
            }

            var ruleResult = plannedCondition ?? _ruleEngine.Evaluate(source, settings.Rules);
            if (!ruleResult.IsMatch)
            {
                progress?.Report(new WorkflowProgress(VideoTaskStatus.Skipped, "跳过：不符合压缩条件。", Condition: ruleResult));
                return new CompressionJobResult(VideoTaskStatus.Skipped, ruleResult.Summary, SourceInfo: source, Condition: ruleResult);
            }
            progress?.Report(new WorkflowProgress(VideoTaskStatus.Eligible, "符合压缩条件，准备进入处理阶段。", Condition: ruleResult));

            TargetSizeCalculation? targetSize = null;
            if (plannedPlan is null && settings.CompressionMode == CompressionMode.TargetSize)
            {
                if (!source.HasProbeData)
                {
                    progress?.Report(new WorkflowProgress(VideoTaskStatus.Analyzing, "目标大小模式需要读取时长与音频码率…"));
                    source = await _ffprobeService.ProbeAsync(tools, source.FullPath, cancellationToken);
                }

                targetSize = _targetSizeCalculator.Calculate(settings.TargetSize, source, settings.AudioMode, settings.AudioBitrateKbps);
                if (!targetSize.IsValid)
                {
                    return new CompressionJobResult(VideoTaskStatus.Skipped, $"已跳过：{targetSize.Error}", SourceInfo: source);
                }
                if (targetSize.IsLargerThanSource)
                {
                    return new CompressionJobResult(VideoTaskStatus.Skipped, "已跳过：目标文件大小不小于原文件，压缩没有意义。", SourceInfo: source);
                }
            }

            var plan = plannedPlan ?? _planner.CreatePlan(source, settings, targetSize, capabilities ?? _defaultCapabilities);
            if (plannedPlan is null && plan.SmartDecision is { ShouldCompress: false } smartDecision)
            {
                progress?.Report(new WorkflowProgress(
                    VideoTaskStatus.Skipped,
                    "跳过：智能判断认为无需压缩。",
                    Condition: ruleResult,
                    SmartDecision: smartDecision,
                    Encoder: smartDecision.SelectedEncoder));
                return new CompressionJobResult(
                    VideoTaskStatus.Skipped,
                    $"跳过：智能判断认为无需压缩。{smartDecision.Reason}",
                    SourceInfo: source,
                    Condition: ruleResult,
                    SmartDecision: smartDecision,
                    Encoder: smartDecision.SelectedEncoder);
            }
            var warning = plan.Warnings.Count == 0 ? string.Empty : $" 警告：{string.Join("；", plan.Warnings)}";
            pathReservation = ReserveOutputPaths(source, settings, scanRoot, plannedPlan);
            var finalOutputPath = pathReservation.FinalOutputPath;
            temporaryOutputPath = _safeFileService.CreateTemporaryOutputPath(finalOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryOutputPath)!);
            passLogPrefix = Path.Combine(Path.GetDirectoryName(temporaryOutputPath)!, $".clvc-pass-{Guid.NewGuid():N}");

            progress?.Report(new WorkflowProgress(VideoTaskStatus.Queued, "已进入压缩队列。", Condition: ruleResult, SmartDecision: plan.SmartDecision, Encoder: plan.Encoder));
            progress?.Report(new WorkflowProgress(VideoTaskStatus.Compressing, $"正在压缩到受保护的临时文件…{warning}", Condition: ruleResult, SmartDecision: plan.SmartDecision, Encoder: plan.Encoder));
            var activePlan = plan;
            var fallbackNotes = new List<string>();
            IProgress<EncodingProgress>? encodingProgress = progress is null
                ? null
                : new Progress<EncodingProgress>(item =>
                    progress.Report(new WorkflowProgress(
                        VideoTaskStatus.Compressing,
                        $"正在使用 {EncoderCatalog.Get(activePlan.Encoder).DisplayName} 压缩到受保护的临时文件…",
                        item,
                        ruleResult,
                        activePlan.SmartDecision,
                        activePlan.Encoder)));

            FFmpegRunResult? encoding = null;
            foreach (var candidate in plan.EncoderCandidates.Take(4))
            {
                activePlan = plan.WithEncoder(candidate);
                if (candidate != plan.Encoder)
                {
                    _safeFileService.DeleteTemporaryFile(temporaryOutputPath);
                    _safeFileService.DeletePassLogs(passLogPrefix);
                    progress?.Report(new WorkflowProgress(
                        VideoTaskStatus.Compressing,
                        $"前一个编码器初始化失败，正在回退到 {EncoderCatalog.Get(candidate).DisplayName}…",
                        Condition: ruleResult,
                        SmartDecision: activePlan.SmartDecision,
                        Encoder: candidate));
                }

                if (activePlan.IsTwoPass)
                {
                    var firstPass = await _ffmpegService.RunAsync(
                        tools,
                        activePlan.BuildArguments(source.FullPath, temporaryOutputPath, true, passLogPrefix),
                        source.DurationSeconds,
                        encodingProgress,
                        cancellationToken);
                    if (!firstPass.Succeeded)
                    {
                        if (IsEncoderInitializationFailure(firstPass.ErrorOutput) &&
                            TryGetNextCandidate(plan, candidate, out var firstPassNextCandidate))
                        {
                            var firstPassFallbackNote = $"{EncoderCatalog.Get(candidate).DisplayName} 初始化失败，自动尝试 {EncoderCatalog.Get(firstPassNextCandidate).DisplayName}";
                            fallbackNotes.Add(firstPassFallbackNote);
                            DiagnosticLog.Write("workflow", firstPassFallbackNote);
                            _safeFileService.DeleteTemporaryFile(temporaryOutputPath);
                            _safeFileService.DeletePassLogs(passLogPrefix);
                            continue;
                        }

                        return FailedFromFfmpeg(
                            firstPass,
                            source,
                            "两遍编码第一遍失败",
                            ruleResult,
                            activePlan.SmartDecision,
                            candidate,
                            string.Join("；", fallbackNotes));
                    }
                }

                encoding = await _ffmpegService.RunAsync(
                    tools,
                    activePlan.BuildArguments(source.FullPath, temporaryOutputPath, false, passLogPrefix),
                    source.DurationSeconds,
                    encodingProgress,
                    cancellationToken);
                if (encoding.Succeeded)
                {
                    break;
                }

                if (!TryGetNextCandidate(plan, candidate, out var nextCandidate) ||
                    !IsEncoderInitializationFailure(encoding.ErrorOutput))
                {
                    return FailedFromFfmpeg(
                        encoding,
                        source,
                        "FFmpeg 压缩失败",
                        ruleResult,
                        activePlan.SmartDecision,
                        candidate,
                        string.Join("；", fallbackNotes));
                }

                var fallbackNote = $"{EncoderCatalog.Get(candidate).DisplayName} 初始化失败，自动尝试 {EncoderCatalog.Get(nextCandidate).DisplayName}";
                fallbackNotes.Add(fallbackNote);
                DiagnosticLog.Write("workflow", fallbackNote);
                _safeFileService.DeleteTemporaryFile(temporaryOutputPath);
                _safeFileService.DeletePassLogs(passLogPrefix);
            }

            if (encoding is null || !encoding.Succeeded)
            {
                return FailedFromFfmpeg(
                    encoding ?? new FFmpegRunResult(false, -1, "未执行任何编码器。"),
                    source,
                    "FFmpeg 压缩失败",
                    ruleResult,
                    activePlan.SmartDecision,
                    activePlan.Encoder,
                    string.Join("；", fallbackNotes));
            }

            progress?.Report(new WorkflowProgress(
                VideoTaskStatus.Verifying,
                "正在验证压缩输出…",
                Condition: ruleResult,
                SmartDecision: activePlan.SmartDecision,
                Encoder: activePlan.Encoder));
            var verification = await _safeFileService.ValidateOutputAsync(tools, source, temporaryOutputPath, cancellationToken);
            if (!verification.IsValid)
            {
                return new CompressionJobResult(
                    VideoTaskStatus.Failed,
                    $"输出验证失败：{verification.Error}",
                    SourceInfo: source,
                    OutputInfo: verification.OutputInfo,
                    Condition: ruleResult,
                    SmartDecision: activePlan.SmartDecision,
                    Encoder: activePlan.Encoder);
            }

            var outputLength = new FileInfo(temporaryOutputPath).Length;
            var targetSizeBytes = plannedPlan?.TargetSizeBytes ?? targetSize?.TargetSizeBytes;
            if (targetSizeBytes is { } plannedTargetSize && outputLength > plannedTargetSize * MaximumTargetSizeRatio)
            {
                return new CompressionJobResult(
                    VideoTaskStatus.Skipped,
                    $"已放弃：目标大小为 {DisplayFormat.FileSize(plannedTargetSize)}，实际结果为 {DisplayFormat.FileSize(outputLength)}，超过允许的 3% 余量。原文件保持不动。",
                    SourceInfo: source,
                    OutputInfo: verification.OutputInfo,
                    Condition: ruleResult,
                    SmartDecision: activePlan.SmartDecision,
                    Encoder: activePlan.Encoder,
                    FallbackReason: string.Join("；", fallbackNotes));
            }
            if (settings.DiscardIfLarger && outputLength >= source.FileSizeBytes)
            {
                return new CompressionJobResult(
                    VideoTaskStatus.Skipped,
                    $"已跳过：压缩结果为 {DisplayFormat.FileSize(outputLength)}，不小于原文件 {DisplayFormat.FileSize(source.FileSizeBytes)}。",
                    SourceInfo: source,
                    OutputInfo: verification.OutputInfo,
                    Condition: ruleResult,
                    SmartDecision: activePlan.SmartDecision,
                    Encoder: activePlan.Encoder,
                    FallbackReason: string.Join("；", fallbackNotes));
            }

            // The commit boundary: a cancellation before this line leaves both source and final output untouched.
            // After an original move begins, finalizing its already-verified replacement is intentionally atomic from the user's perspective.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new WorkflowProgress(
                VideoTaskStatus.Committing,
                "正在提交已验证的输出…",
                Condition: ruleResult,
                SmartDecision: activePlan.SmartDecision,
                Encoder: activePlan.Encoder));
            if (settings.OriginalFileAction != OriginalFileAction.Keep)
            {
                var originalDestination = pathReservation.OriginalDestinationPath
                    ?? throw new InvalidOperationException("未能保留原文件移动目标。");
                originalMove = _safeFileService.MoveOriginal(source, originalDestination);
            }

            try
            {
                _safeFileService.FinalizeTemporaryOutput(temporaryOutputPath, finalOutputPath);
                temporaryOutputPath = null;
            }
            catch (Exception exception)
            {
                var rollback = _safeFileService.TryRollbackOriginalMove(originalMove);
                var rollbackDetail = rollback.Succeeded
                    ? "原文件已自动回滚。"
                    : rollback.Error ?? "原文件的自动回滚状态未知。";
                throw new IOException($"无法提交最终输出“{finalOutputPath}”：{exception.Message} {rollbackDetail}", exception);
            }

            var message = originalMove is null
                ? $"压缩完成：{Path.GetFileName(finalOutputPath)}"
                : $"压缩完成，原文件已移动到：{originalMove.DestinationPath}";
            if (fallbackNotes.Count > 0)
            {
                message += $"（{string.Join("；", fallbackNotes)}）";
            }
            return new CompressionJobResult(
                VideoTaskStatus.Completed,
                message,
                finalOutputPath,
                source,
                verification.OutputInfo,
                ruleResult,
                activePlan.SmartDecision,
                activePlan.Encoder,
                string.Join("；", fallbackNotes));
        }
        catch (FFmpegCancellationTimeoutException exception)
        {
            var retainedTemporaryPath = temporaryOutputPath;
            temporaryOutputPath = null;
            passLogPrefix = null;
            var retainedPathDetail = string.IsNullOrWhiteSpace(retainedTemporaryPath)
                ? string.Empty
                : $" 为避免删除可能仍被占用的临时文件，已保留：{retainedTemporaryPath}。";
            return new CompressionJobResult(
                VideoTaskStatus.Cancelled,
                $"取消请求超时：{exception.Message} 原文件未被修改，也不会提交压缩结果。{retainedPathDetail}",
                SourceInfo: source);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CompressionJobResult(VideoTaskStatus.Cancelled, "任务已取消，原文件未被修改。", SourceInfo: source);
        }
        catch (Exception exception)
        {
            return new CompressionJobResult(VideoTaskStatus.Failed, $"处理失败：{exception.Message}", SourceInfo: source);
        }
        finally
        {
            try
            {
                _safeFileService.DeleteTemporaryFile(temporaryOutputPath);
                _safeFileService.DeletePassLogs(passLogPrefix);
            }
            finally
            {
                ReleasePathReservation(pathReservation);
            }
        }
    }

    private OutputPathReservation ReserveOutputPaths(
        VideoFileInfo source,
        AppSettings settings,
        string? scanRoot,
        CompressionPlan? plannedPlan = null)
    {
        lock (_pathReservationLock)
        {
            var finalOutputPath = string.IsNullOrWhiteSpace(plannedPlan?.TargetPath)
                ? _outputPathService.GetOutputPath(source, settings, scanRoot, _reservedPaths)
                : Path.GetFullPath(plannedPlan.TargetPath);
            var sameAsSource = string.Equals(finalOutputPath, Path.GetFullPath(source.FullPath), StringComparison.OrdinalIgnoreCase);
            if ((File.Exists(finalOutputPath) && !(sameAsSource && settings.OriginalFileAction != OriginalFileAction.Keep)) ||
                _reservedPaths.Contains(finalOutputPath))
            {
                throw new IOException($"计划的输出路径已不可用：{finalOutputPath}");
            }
            var originalDestinationPath = settings.OriginalFileAction == OriginalFileAction.Keep
                ? null
                : _outputPathService.GetOriginalDestinationPath(source, settings, finalOutputPath, _reservedPaths);

            _reservedPaths.Add(finalOutputPath);
            if (!string.IsNullOrWhiteSpace(originalDestinationPath))
            {
                _reservedPaths.Add(originalDestinationPath);
            }

            return new OutputPathReservation(finalOutputPath, originalDestinationPath);
        }
    }

    private void ReleasePathReservation(OutputPathReservation? reservation)
    {
        if (reservation is null)
        {
            return;
        }

        lock (_pathReservationLock)
        {
            _reservedPaths.Remove(reservation.FinalOutputPath);
            if (!string.IsNullOrWhiteSpace(reservation.OriginalDestinationPath))
            {
                _reservedPaths.Remove(reservation.OriginalDestinationPath);
            }
        }
    }

    private static CompressionJobResult FailedFromFfmpeg(
        FFmpegRunResult run,
        VideoFileInfo source,
        string prefix,
        ConditionEvaluationResult? condition = null,
        SmartCompressionDecision? smartDecision = null,
        VideoEncoder? encoder = null,
        string? fallbackReason = null)
    {
        var error = run.ErrorOutput.Trim();
        if (error.Length > 1_500)
        {
            error = error[^1_500..];
        }

        return new CompressionJobResult(
            VideoTaskStatus.Failed,
            $"{prefix}（退出码 {run.ExitCode}）：{(string.IsNullOrWhiteSpace(error) ? "未返回错误文本" : error)}",
            SourceInfo: source,
            Condition: condition,
            SmartDecision: smartDecision,
            Encoder: encoder,
            FallbackReason: string.IsNullOrWhiteSpace(fallbackReason) ? null : fallbackReason);
    }

    private static bool TryGetNextCandidate(CompressionPlan plan, VideoEncoder candidate, out VideoEncoder nextCandidate)
    {
        var candidates = plan.EncoderCandidates.Take(4).ToArray();
        var index = Array.IndexOf(candidates, candidate);
        if (index >= 0 && index + 1 < candidates.Length)
        {
            nextCandidate = candidates[index + 1];
            return true;
        }

        nextCandidate = default;
        return false;
    }

    private static bool IsEncoderInitializationFailure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var text = error.ToLowerInvariant();
        return text.Contains("cannot load") ||
               text.Contains("failed to create") ||
               text.Contains("error while opening encoder") ||
               text.Contains("no capable device") ||
               text.Contains("device") && (text.Contains("not found") || text.Contains("unavailable") || text.Contains("initial")) ||
               text.Contains("driver") ||
               text.Contains("initialization");
    }

    private sealed record OutputPathReservation(string FinalOutputPath, string? OriginalDestinationPath);
}
