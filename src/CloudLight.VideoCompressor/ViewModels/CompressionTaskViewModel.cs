using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.ViewModels;

public sealed class CompressionTaskViewModel : ObservableObject, IDisposable
{
    private readonly CompressionTaskSession _session;
    private readonly CompressionWorkflowService _workflowService;
    private readonly FFmpegTools _tools;
    private readonly Dispatcher _dispatcher;
    private readonly CompressionWorkerPool _workerPool = new();
    private readonly CompressionHistoryService _historyService;
    private readonly CompressionDurationEstimator _durationEstimator = new();
    private readonly EncodingPerformanceHistory _performanceHistory;
    private readonly LongRunningTaskPolicy _executionPolicy;
    private readonly CompressionTaskSessionStore _sessionStore;
    private readonly ISystemPowerService _systemPowerService;
    private readonly EncoderCapabilitySet? _capabilities;
    private readonly CancellationTokenSource _cancellation = new();
    private CancellationTokenSource? _completionActionCancellation;
    private bool _completionCountdownActive;
    private int _completionCountdownSeconds;
    private bool _preserveRecoveryOnClose;
    private bool _hasStarted;
    private bool _isRunning;
    private bool _disposed;
    private bool _refreshingAggregateProperties;
    private string _statusMessage = "请确认计划后再开始。";
    private CompressionTaskEntry? _selectedEntry;
    private TimeSpan? _queueRemaining;
    private EtaConfidence _queueEtaConfidence = EtaConfidence.Unknown;
    private Stopwatch? _executionStopwatch;

    public CompressionTaskViewModel(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools,
        Dispatcher? dispatcher = null,
        CompressionHistoryService? historyService = null,
        CompressionTaskSessionStore? sessionStore = null,
        ISystemPowerService? systemPowerService = null,
        EncoderCapabilitySet? capabilities = null)
    {
        _session = session;
        _workflowService = workflowService;
        _tools = tools;
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _historyService = historyService ?? new CompressionHistoryService();
        _sessionStore = sessionStore ?? new CompressionTaskSessionStore();
        _systemPowerService = systemPowerService ?? new WindowsSystemPowerService();
        _capabilities = capabilities;
        _executionPolicy = session.ExecutionPolicy;
        _performanceHistory = session.PerformanceHistory;
        Entries = session.Entries;
        SelectedEntry = Entries.FirstOrDefault();
        foreach (var entry in Entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
        }

        StartCompressionCommand = new AsyncRelayCommand(StartCompressionAsync, () => CanStart);
        ReturnCommand = new RelayCommand(Return, () => CanReturn);
        StopCommand = new RelayCommand(CancelCurrentOperation, () => IsRunning);
        ToggleQueuePauseCommand = new AsyncRelayCommand(ToggleQueuePauseAsync, () => CanToggleQueuePause);
        CancelCompletionActionCommand = new RelayCommand(CancelCompletionAction, () => CompletionCountdownActive);
    }

    public event EventHandler? RequestClose;

    public ObservableCollection<CompressionTaskEntry> Entries { get; }
    public CompressionTaskSession Session => _session;
    public AppSettings SettingsSnapshot => _session.SettingsSnapshot;
    public LongRunningTaskPolicy ExecutionPolicy => _executionPolicy;
    public AsyncRelayCommand StartCompressionCommand { get; }
    public RelayCommand ReturnCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand ToggleQueuePauseCommand { get; }
    public RelayCommand CancelCompletionActionCommand { get; }

    public CompressionTaskEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    public bool HasStarted
    {
        get => _hasStarted;
        private set
        {
            if (SetProperty(ref _hasStarted, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanReturn));
                OnPropertyChanged(nameof(OverallStatusDisplay));
                OnPropertyChanged(nameof(TaskProgressDisplay));
                RefreshAggregateProperties();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanReturn));
                OnPropertyChanged(nameof(OverallStatusDisplay));
                StartCompressionCommand.RaiseCanExecuteChanged();
                ReturnCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                ToggleQueuePauseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool CanStart => !HasStarted && !IsRunning && Entries.Any(entry => !IsTerminal(entry)) &&
                            !HasBlockingStreamAudit && !HasBlockingPlan;
    public bool CanReturn => !IsRunning;
    public bool IsFinished => HasStarted && Entries.Count > 0 && Entries.All(IsTerminal);
    public bool HasBlockingStreamAudit => Entries.Any(entry => entry.Plan.StreamAudit?.BlocksExecution == true);
    public bool HasBlockingPlan => Entries.Any(entry => entry.Plan.BlocksExecution);
    public bool IsQueuePaused => _session.QueuePaused;
    public bool CanToggleQueuePause => Entries.Any(entry => !IsTerminal(entry)) && !CompletionCountdownActive;
    public string QueuePauseButtonDisplay => IsQueuePaused ? "继续队列" : "暂停队列";
    public string QueuePauseStatusDisplay => IsQueuePaused
        ? "队列已暂停，当前任务完成后不会启动新任务。"
        : string.Empty;
    public int TotalCount => Entries.Count;
    public long SourceTotalSizeBytes => Entries.Sum(entry => Math.Max(0, entry.Source.FileSizeBytes));
    public string SourceTotalSizeDisplay => DisplayFormat.FileSize(SourceTotalSizeBytes);

    public string PlannedOutputTotalSizeDisplay
    {
        get
        {
            if (Entries.Count == 0 || Entries.Any(entry => entry.Plan.EstimatedOutputLowerBoundBytes is null || entry.Plan.EstimatedOutputUpperBoundBytes is null))
            {
                return "无法精确预估";
            }

            var lower = Entries.Sum(entry => entry.Plan.EstimatedOutputLowerBoundBytes ?? 0);
            var upper = Entries.Sum(entry => entry.Plan.EstimatedOutputUpperBoundBytes ?? 0);
            return lower == upper
                ? $"约 {DisplayFormat.FileSize(lower)}"
                : $"约 {DisplayFormat.FileSize(lower)}～{DisplayFormat.FileSize(upper)}";
        }
    }

    public string PlannedSavingDisplay
    {
        get
        {
            if (SourceTotalSizeBytes <= 0 || Entries.Count == 0 ||
                Entries.Any(entry => entry.Plan.EstimatedOutputLowerBoundBytes is null || entry.Plan.EstimatedOutputUpperBoundBytes is null))
            {
                return "无法精确预估";
            }

            var lower = Entries.Sum(entry => entry.Plan.EstimatedOutputLowerBoundBytes ?? 0);
            var upper = Entries.Sum(entry => entry.Plan.EstimatedOutputUpperBoundBytes ?? 0);
            var savingAtUpper = (1 - upper / (double)SourceTotalSizeBytes) * 100;
            var savingAtLower = (1 - lower / (double)SourceTotalSizeBytes) * 100;
            return lower == upper
                ? $"{savingAtLower:0.0}%"
                : $"约 {Math.Min(savingAtUpper, savingAtLower):0.0}%～{Math.Max(savingAtUpper, savingAtLower):0.0}%";
        }
    }

    public string EncodingSummaryDisplay
    {
        get
        {
            if (Entries.Count == 0)
            {
                return "—";
            }

            return string.Join(" · ", Entries
                .GroupBy(entry => EncoderGroupDisplay(entry.Plan.Encoder))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}：{group.Count()}"));
        }
    }

    public string TaskProgressDisplay => !HasStarted ? $"0 / {TotalCount}" : $"{FinishedCount} / {TotalCount}";
    public double OverallProgressPercent => TotalCount == 0
        ? 0
        : Entries.Sum(entry => IsTerminal(entry) ? 100 : entry.ProgressPercent) / TotalCount;
    public string OverallProgressDisplay => HasStarted ? $"{OverallProgressPercent:0.0}%" : "等待开始";
    public string CurrentTaskDisplay
    {
        get
        {
            var current = Entries.FirstOrDefault(entry => entry.ExecutionState is
                CompressionExecutionState.Compressing or
                CompressionExecutionState.Verifying or
                CompressionExecutionState.Committing);
            return current is null ? "—" : current.FileName;
        }
    }

    public bool HasCurrentTask => CurrentTaskDisplay != "—";
    public string CurrentTaskEtaDisplay => Entries.FirstOrDefault(entry => entry.ExecutionState is
        CompressionExecutionState.Compressing or
        CompressionExecutionState.Verifying or
        CompressionExecutionState.Committing)?.ProgressEtaDisplay ?? "—";
    public string QueueEtaDisplay => IsQueuePaused
        ? "已暂停"
        : !HasStarted
        ? "计算中…"
        : IsFinished
            ? Entries.All(entry => entry.ExecutionState == CompressionExecutionState.Completed)
                ? "已完成"
                : "—"
            : _queueRemaining is { } remaining
                ? DisplayFormat.EstimatedDuration(remaining)
                : "计算中…";
    public string QueueEtaConfidenceDisplay => _queueEtaConfidence switch
    {
        EtaConfidence.High => "高",
        EtaConfidence.Medium => "中",
        EtaConfidence.Low => "低",
        _ => "未知"
    };
    public string EstimatedCompletionDisplay => IsRunning && _queueRemaining is { } remaining
        && !IsQueuePaused
        ? $"预计完成时间：{(DateTimeOffset.Now + remaining).ToLocalTime():HH:mm}"
        : string.Empty;
    public bool CompletionCountdownActive
    {
        get => _completionCountdownActive;
        private set
        {
            if (SetProperty(ref _completionCountdownActive, value))
            {
                OnPropertyChanged(nameof(CanToggleQueuePause));
                CancelCompletionActionCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public int CompletionCountdownSeconds
    {
        get => _completionCountdownSeconds;
        private set
        {
            if (SetProperty(ref _completionCountdownSeconds, value))
            {
                OnPropertyChanged(nameof(CompletionActionCountdownDisplay));
            }
        }
    }
    public string CompletionActionCountdownDisplay => CompletionCountdownActive
        ? $"{SettingsSnapshot.CompletionAction.GetDescription()} 将在 {CompletionCountdownSeconds} 秒后执行。"
        : string.Empty;
    public string ElapsedDisplay => _executionStopwatch is null ? "—" : _executionStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
    public int FinishedCount => Entries.Count(entry => IsTerminal(entry));
    public int CompletedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Completed);
    public int CompressingCount => Entries.Count(entry => entry.ExecutionState is CompressionExecutionState.Compressing or CompressionExecutionState.Verifying or CompressionExecutionState.Committing);
    public int QueuedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Queued);
    public int WaitingCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.WaitingToStart);
    public int FailedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Failed);
    public int CancelledCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Cancelled);
    public int AbandonedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Abandoned);
    public int InterruptedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Interrupted);
    public int SourceChangedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.SourceChanged);
    public long ActualOutputTotalSizeBytes => Entries
        .Where(entry => entry.ExecutionState == CompressionExecutionState.Completed)
        .Sum(entry => entry.ActualOutputSizeBytes ?? 0);
    public string ActualOutputTotalSizeDisplay => CompletedCount == 0 ? "—" : DisplayFormat.FileSize(ActualOutputTotalSizeBytes);
    public string ActualSavingDisplay => CompletedCount == 0 || SourceTotalSizeBytes <= 0
        ? "—"
        : $"{(1 - ActualOutputTotalSizeBytes / (double)SourceTotalSizeBytes) * 100:0.0}%";
    public string OutputTotalSizeDisplay => IsFinished ? ActualOutputTotalSizeDisplay : PlannedOutputTotalSizeDisplay;
    public string SavingDisplay => IsFinished ? ActualSavingDisplay : PlannedSavingDisplay;
    public string OverallStatusDisplay => !HasStarted
        ? HasBlockingPlan
            ? "计划包含安全阻止项，请返回调整设置"
            : "压缩前请确认处理计划"
        : IsRunning
            ? "正在执行压缩任务…"
            : CompletedCount == 0
                ? "任务结束，没有视频成功压缩。"
                : "压缩任务完成";
    public string FinalSummaryDisplay => $"成功：{CompletedCount} · 失败：{FailedCount} · 取消：{CancelledCount} · 中断：{InterruptedCount} · 源已变化：{SourceChangedCount} · 放弃结果：{AbandonedCount}";
    public bool HasPlanningNotes => _session.PlanningNotes.Count > 0;
    public string PlanningNotesDisplay => string.Join(Environment.NewLine, _session.PlanningNotes);

    private Task? _executionTask;

    private async Task StartCompressionAsync()
    {
        if (!CanStart || _disposed)
        {
            return;
        }

        HasStarted = true;
        IsRunning = true;
        _executionStopwatch = Stopwatch.StartNew();
        StatusMessage = $"已确认计划，开始处理 {TotalCount} 个视频（{_executionPolicy.Mode.GetDescription()}，最多 {_executionPolicy.MaxTotalWorkers} 个并发任务）。";
        await PersistSessionSafelyAsync();
        _executionTask = ExecuteAllAsync();
        try
        {
            await _executionTask;
        }
        catch (Exception exception)
        {
            StatusMessage = $"任务执行异常：{exception.Message}";
        }
        finally
        {
            if (_cancellation.IsCancellationRequested)
            {
                _queueRemaining = null;
                _queueEtaConfidence = EtaConfidence.Unknown;
            }

            _executionStopwatch?.Stop();
            IsRunning = false;
            StatusMessage = _cancellation.IsCancellationRequested
                ? "任务已取消，未提交的源文件保持不动。"
                : "任务处理结束。";
            RefreshAggregateProperties();
            await PersistSessionSafelyAsync();
            if (!_cancellation.IsCancellationRequested && IsFinished)
            {
                _ = ScheduleCompletionActionAsync();
            }
        }
    }

    private async Task ExecuteAllAsync()
    {
        using var sleepPrevention = CompressionSleepPrevention.Acquire(SettingsSnapshot.PreventSleepDuringCompression);
        foreach (var entry in Entries)
        {
            if (!IsTerminal(entry))
            {
                entry.MarkQueued();
            }
        }
        RefreshAggregateProperties();
        await PersistSessionSafelyAsync();

        using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        var checkpointTask = CheckpointLoopAsync(checkpointCancellation.Token);
        try
        {
            await _workerPool.ExecuteAsync(
                Entries,
                _executionPolicy,
                ExecuteEntryAsync,
                _cancellation.Token,
                () => IsQueuePaused);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Cancellation is converted into an explicit state below. This
            // keeps queued entries from remaining "排队中" after a user stop.
        }
        finally
        {
            checkpointCancellation.Cancel();
            try
            {
                await checkpointTask;
            }
            catch (OperationCanceledException) when (checkpointCancellation.IsCancellationRequested)
            {
            }
        }

        if (_cancellation.IsCancellationRequested)
        {
            ApplyOnUi(() =>
            {
                foreach (var entry in Entries.Where(entry => !IsTerminal(entry)))
                {
                    if (_preserveRecoveryOnClose)
                    {
                        entry.MarkInterrupted();
                    }
                    else
                    {
                        entry.MarkCancelled();
                    }
                }
                RefreshAggregateProperties();
            });
        }
        await PersistSessionSafelyAsync();
    }

    private async Task ExecuteEntryAsync(CompressionTaskEntry entry)
    {
        try
        {
            if (!File.Exists(entry.Source.FullPath) ||
                !entry.SourceFingerprint.Matches(MediaFileFingerprint.FromFile(entry.Source.FullPath)))
            {
                ApplyOnUi(() => entry.MarkSourceChanged());
                await PersistSessionSafelyAsync();
                return;
            }

            var progress = new Progress<WorkflowProgress>(update => ApplyEntryProgress(entry, update));
            var result = await _workflowService.ProcessJobAsync(
                entry.Job,
                _tools,
                progress,
                _cancellation.Token,
                _capabilities).ConfigureAwait(false);
            await _historyService.AppendAsync(CompressionHistoryEntry.From(result)).ConfigureAwait(false);
            _performanceHistory.Record(entry, result);
            ApplyOnUi(() =>
            {
                if (_preserveRecoveryOnClose && result.Status == VideoTaskStatus.Cancelled)
                {
                    entry.MarkInterrupted();
                }
                else
                {
                    entry.ApplyResult(result);
                }
                RefreshAggregateProperties();
            });
            await PersistSessionSafelyAsync();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            ApplyOnUi(() =>
            {
                if (_preserveRecoveryOnClose)
                {
                    entry.MarkInterrupted();
                }
                else
                {
                    entry.MarkCancelled();
                }
                RefreshAggregateProperties();
            });
            await PersistSessionSafelyAsync();
        }
        catch (Exception exception)
        {
            ApplyOnUi(() =>
            {
                entry.ExecutionState = CompressionExecutionState.Failed;
                entry.StatusDetail = $"处理失败：{exception.Message}";
                RefreshAggregateProperties();
            });
            await PersistSessionSafelyAsync();
        }
    }

    private async Task CheckpointLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            await PersistSessionSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ToggleQueuePauseAsync()
    {
        if (!CanToggleQueuePause)
        {
            return;
        }

        _session.QueuePaused = !_session.QueuePaused;
        StatusMessage = _session.QueuePaused
            ? "队列已暂停，当前任务完成后不会启动新任务。"
            : "队列已继续，等待中的任务可以开始。";
        DiagnosticLog.Write("queue", _session.QueuePaused ? "QueuePaused" : "QueueResumed");
        RefreshAggregateProperties();
        await PersistSessionSafelyAsync();
    }

    public async Task PrepareForShutdownRecoveryAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _preserveRecoveryOnClose = true;
        foreach (var entry in Entries.Where(entry => !IsTerminal(entry)))
        {
            if (entry.ExecutionState is CompressionExecutionState.Compressing or
                CompressionExecutionState.Verifying or
                CompressionExecutionState.Committing)
            {
                entry.MarkInterrupted();
            }
        }
        await PersistSessionSafelyAsync(normalizeRunningStates: true);
        CancelCurrentOperation();
    }

    private async Task PersistSessionSafelyAsync(
        CancellationToken cancellationToken = default,
        bool normalizeRunningStates = false)
    {
        try
        {
            await _sessionStore.SaveAsync(
                _session,
                cancellationToken,
                normalizeRunningStates).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("session", $"Session 保存失败：{exception.Message}");
        }
    }

    private void CancelCompletionAction()
    {
        _completionActionCancellation?.Cancel();
        CompletionCountdownActive = false;
        CompletionCountdownSeconds = 0;
        StatusMessage = "已取消任务完成后的系统操作。";
    }

    private async Task ScheduleCompletionActionAsync()
    {
        var decision = CompletionActionPolicy.Evaluate(SettingsSnapshot, Entries.Select(entry => entry.ExecutionState));
        if (!decision.ShouldExecute)
        {
            StatusMessage = decision.Message;
            return;
        }
        if (SettingsSnapshot.CompletionAction != CompletionAction.CloseApplication &&
            !SettingsSnapshot.CompletionActionConfirmed)
        {
            StatusMessage = "任务已结束；所选系统操作尚未确认，因此未执行。";
            return;
        }

        _completionActionCancellation?.Cancel();
        _completionActionCancellation?.Dispose();
        _completionActionCancellation = new CancellationTokenSource();
        CompletionCountdownSeconds = 30;
        CompletionCountdownActive = true;
        StatusMessage = $"任务已结束：成功 {CompletedCount}，失败 {FailedCount}。";
        DiagnosticLog.Write("completion-action", $"SystemActionScheduled：{SettingsSnapshot.CompletionAction}");
        try
        {
            while (CompletionCountdownSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _completionActionCancellation.Token).ConfigureAwait(false);
                ApplyOnUi(() => CompletionCountdownSeconds--);
            }

            ApplyOnUi(() => CompletionCountdownActive = false);
            switch (SettingsSnapshot.CompletionAction)
            {
                case CompletionAction.CloseApplication:
                    ApplyOnUi(() => RequestClose?.Invoke(this, EventArgs.Empty));
                    break;
                case CompletionAction.Sleep:
                    _systemPowerService.Sleep();
                    break;
                case CompletionAction.Hibernate:
                    _systemPowerService.Hibernate();
                    break;
                case CompletionAction.Shutdown:
                    _systemPowerService.Shutdown();
                    break;
            }
        }
        catch (OperationCanceledException) when (_completionActionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplyOnUi(() => StatusMessage = $"完成后的系统操作失败：{exception.Message}");
        }
        finally
        {
            ApplyOnUi(() =>
            {
                CompletionCountdownActive = false;
                CompletionCountdownSeconds = 0;
            });
        }
    }

    private void ApplyEntryProgress(CompressionTaskEntry entry, WorkflowProgress progress) =>
        ApplyOnUi(() =>
        {
            entry.ApplyProgress(progress);
            RefreshAggregateProperties();
        });

    private void ApplyOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException)
        {
            // The window dispatcher is shutting down; no UI update is needed.
        }
    }

    private void CancelCurrentOperation()
    {
        if (IsRunning && !_cancellation.IsCancellationRequested)
        {
            StatusMessage = "正在请求停止当前任务…";
            _cancellation.Cancel();
        }
    }

    public async Task WaitForCompletionAsync()
    {
        if (_executionTask is not null)
        {
            await _executionTask;
        }
    }

    private void Return()
    {
        if (CanReturn)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshAggregateProperties();

    private void RefreshAggregateProperties()
    {
        if (_refreshingAggregateProperties)
        {
            return;
        }

        _refreshingAggregateProperties = true;
        try
        {
            RefreshTimeEstimates();
            OnPropertyChanged(nameof(PlannedOutputTotalSizeDisplay));
            OnPropertyChanged(nameof(PlannedSavingDisplay));
            OnPropertyChanged(nameof(OutputTotalSizeDisplay));
            OnPropertyChanged(nameof(SavingDisplay));
            OnPropertyChanged(nameof(EncodingSummaryDisplay));
            OnPropertyChanged(nameof(TaskProgressDisplay));
            OnPropertyChanged(nameof(OverallProgressPercent));
            OnPropertyChanged(nameof(OverallProgressDisplay));
            OnPropertyChanged(nameof(CurrentTaskDisplay));
            OnPropertyChanged(nameof(HasCurrentTask));
            OnPropertyChanged(nameof(FinishedCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(CompressingCount));
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(WaitingCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(CancelledCount));
            OnPropertyChanged(nameof(AbandonedCount));
            OnPropertyChanged(nameof(InterruptedCount));
            OnPropertyChanged(nameof(SourceChangedCount));
            OnPropertyChanged(nameof(ActualOutputTotalSizeBytes));
            OnPropertyChanged(nameof(ActualOutputTotalSizeDisplay));
            OnPropertyChanged(nameof(ActualSavingDisplay));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(HasBlockingStreamAudit));
            OnPropertyChanged(nameof(HasBlockingPlan));
            OnPropertyChanged(nameof(OverallStatusDisplay));
            OnPropertyChanged(nameof(FinalSummaryDisplay));
            OnPropertyChanged(nameof(CurrentTaskEtaDisplay));
            OnPropertyChanged(nameof(QueueEtaDisplay));
            OnPropertyChanged(nameof(QueueEtaConfidenceDisplay));
            OnPropertyChanged(nameof(EstimatedCompletionDisplay));
            OnPropertyChanged(nameof(ElapsedDisplay));
            OnPropertyChanged(nameof(IsQueuePaused));
            OnPropertyChanged(nameof(QueuePauseButtonDisplay));
            OnPropertyChanged(nameof(QueuePauseStatusDisplay));
            OnPropertyChanged(nameof(CanToggleQueuePause));
            OnPropertyChanged(nameof(CompletionActionCountdownDisplay));
            StopCommand.RaiseCanExecuteChanged();
            ReturnCommand.RaiseCanExecuteChanged();
            ToggleQueuePauseCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _refreshingAggregateProperties = false;
        }
    }

    private void RefreshTimeEstimates()
    {
        var queue = _durationEstimator.EstimateQueue(Entries, _executionPolicy, _performanceHistory);
        _queueRemaining = queue.Remaining;
        _queueEtaConfidence = queue.Confidence;
        foreach (var entry in Entries.Where(entry => entry.ExecutionState is
                     CompressionExecutionState.WaitingToStart or CompressionExecutionState.Queued))
        {
            entry.ApplyEstimatedDuration(
                _durationEstimator.TryEstimateEntry(
                    entry,
                    _executionPolicy,
                    _performanceHistory,
                    Entries,
                    out var duration,
                    out _)
                    ? duration
                    : null);
        }
    }

    private static bool IsTerminal(CompressionTaskEntry entry) =>
        entry.ExecutionState is CompressionExecutionState.Completed or
            CompressionExecutionState.Cancelled or
            CompressionExecutionState.Failed or
            CompressionExecutionState.Abandoned or
            CompressionExecutionState.SourceChanged;

    private static string EncoderGroupDisplay(VideoEncoder encoder) => EncoderCatalog.Get(encoder).Vendor switch
    {
        EncoderVendor.Intel => "Intel QSV",
        EncoderVendor.Nvidia => "NVIDIA NVENC",
        EncoderVendor.Amd => "AMD AMF",
        _ => "CPU"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _completionActionCancellation?.Cancel();
        _completionActionCancellation?.Dispose();
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }
        _historyService.Dispose();
        _cancellation.Dispose();
    }
}
