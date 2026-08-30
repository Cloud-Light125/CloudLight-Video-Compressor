using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly CancellationTokenSource _cancellation = new();
    private bool _hasStarted;
    private bool _isRunning;
    private bool _disposed;
    private string _statusMessage = "请确认计划后再开始。";
    private CompressionTaskEntry? _selectedEntry;

    public CompressionTaskViewModel(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools,
        Dispatcher? dispatcher = null)
    {
        _session = session;
        _workflowService = workflowService;
        _tools = tools;
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        Entries = session.Entries;
        SelectedEntry = Entries.FirstOrDefault();
        foreach (var entry in Entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
        }

        StartCompressionCommand = new AsyncRelayCommand(StartCompressionAsync, () => CanStart);
        ReturnCommand = new RelayCommand(Return, () => CanReturn);
        StopCommand = new RelayCommand(CancelCurrentOperation, () => IsRunning);
    }

    public event EventHandler? RequestClose;

    public ObservableCollection<CompressionTaskEntry> Entries { get; }
    public CompressionTaskSession Session => _session;
    public AppSettings SettingsSnapshot => _session.SettingsSnapshot;
    public AsyncRelayCommand StartCompressionCommand { get; }
    public RelayCommand ReturnCommand { get; }
    public RelayCommand StopCommand { get; }

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
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool CanStart => !HasStarted && !IsRunning && Entries.Count > 0;
    public bool CanReturn => !IsRunning;
    public bool IsFinished => HasStarted && Entries.Count > 0 && Entries.All(IsTerminal);
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
    public int FinishedCount => Entries.Count(entry => IsTerminal(entry));
    public int CompletedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Completed);
    public int CompressingCount => Entries.Count(entry => entry.ExecutionState is CompressionExecutionState.Compressing or CompressionExecutionState.Verifying or CompressionExecutionState.Committing);
    public int QueuedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Queued);
    public int WaitingCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.WaitingToStart);
    public int FailedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Failed);
    public int CancelledCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Cancelled);
    public int AbandonedCount => Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Abandoned);
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
        ? "压缩前请确认处理计划"
        : IsRunning
            ? "正在执行压缩任务…"
            : CompletedCount == 0
                ? "任务结束，没有视频成功压缩。"
                : "压缩任务完成";
    public string FinalSummaryDisplay => $"成功：{CompletedCount} · 失败：{FailedCount} · 取消：{CancelledCount} · 放弃结果：{AbandonedCount}";
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
        StatusMessage = $"已确认计划，开始处理 {TotalCount} 个视频（并发 {SettingsSnapshot.CompressionConcurrency}）。";
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
            IsRunning = false;
            StatusMessage = _cancellation.IsCancellationRequested
                ? "任务已取消，未提交的源文件保持不动。"
                : "任务处理结束。";
            RefreshAggregateProperties();
        }
    }

    private async Task ExecuteAllAsync()
    {
        using var semaphore = new SemaphoreSlim(
            Math.Clamp(SettingsSnapshot.CompressionConcurrency, 1, 4),
            Math.Clamp(SettingsSnapshot.CompressionConcurrency, 1, 4));
        var tasks = Entries.Select(entry => ExecuteEntryAsync(entry, semaphore)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task ExecuteEntryAsync(CompressionTaskEntry entry, SemaphoreSlim semaphore)
    {
        entry.MarkQueued();
        RefreshAggregateProperties();
        try
        {
            await semaphore.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            entry.MarkCancelled();
            RefreshAggregateProperties();
            return;
        }

        try
        {
            var progress = new Progress<WorkflowProgress>(update => ApplyEntryProgress(entry, update));
            var result = await _workflowService.ProcessPlannedFileAsync(
                entry.Source,
                SettingsSnapshot,
                entry.Plan,
                entry.ConditionEvaluation,
                _tools,
                _session.ScanRoot,
                progress,
                _cancellation.Token).ConfigureAwait(false);
            ApplyOnUi(() =>
            {
                entry.ApplyResult(result);
                RefreshAggregateProperties();
            });
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            ApplyOnUi(() =>
            {
                entry.MarkCancelled();
                RefreshAggregateProperties();
            });
        }
        catch (Exception exception)
        {
            ApplyOnUi(() =>
            {
                entry.ExecutionState = CompressionExecutionState.Failed;
                entry.StatusDetail = $"处理失败：{exception.Message}";
                RefreshAggregateProperties();
            });
        }
        finally
        {
            semaphore.Release();
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

        _dispatcher.Invoke(action);
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
        OnPropertyChanged(nameof(ActualOutputTotalSizeBytes));
        OnPropertyChanged(nameof(ActualOutputTotalSizeDisplay));
        OnPropertyChanged(nameof(ActualSavingDisplay));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(OverallStatusDisplay));
        OnPropertyChanged(nameof(FinalSummaryDisplay));
        StopCommand.RaiseCanExecuteChanged();
        ReturnCommand.RaiseCanExecuteChanged();
    }

    private static bool IsTerminal(CompressionTaskEntry entry) =>
        entry.ExecutionState is CompressionExecutionState.Completed or
            CompressionExecutionState.Cancelled or
            CompressionExecutionState.Failed or
            CompressionExecutionState.Abandoned;

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
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }
        _cancellation.Dispose();
    }
}
