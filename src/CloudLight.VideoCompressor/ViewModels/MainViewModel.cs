using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly FFmpegLocator _ffmpegLocator;
    private readonly EncoderCapabilityDetector _encoderCapabilityDetector;
    private readonly VideoScannerService _videoScannerService;
    private readonly CompressionWorkflowService _workflowService;
    private readonly CompressionTaskPlanner _compressionTaskPlanner;
    private readonly CompressionHistoryService _historyService;
    private readonly RuleEngine _ruleEngine = new();
    private readonly OutputPathService _outputPathService = new();
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly HashSet<VideoTaskItem> _observedVideoItems = [];
    private CancellationTokenSource? _operationCancellation;
    private FFmpegTools? _tools;
    private TaskCompletionSource? _operationCompletion;
    private Task? _shutdownTask;
    private Task? _capabilityDetectionTask;
    private readonly ICollectionView _videoView;
    private readonly DispatcherTimer _videoViewRefreshTimer;
    private bool _videoViewRefreshPending;
    private EncoderCapabilitySet _encoderCapabilities = EncoderCapabilitySet.SoftwareDefaults;
    private Stopwatch? _operationStopwatch;
    private bool _initialized;
    private bool _isShuttingDown;
    private bool _disposed;
    private string _directoryPath = string.Empty;
    private string _ffmpegStatus = "正在检查 FFmpeg…";
    private string _ffmpegToolTip = string.Empty;
    private string _statusMessage = "请选择目录后扫描，或直接处理目录。";
    private string _encoderCapabilitySummary = "正在检测硬件编码器…";
    private bool _isBusy;
    private CompressionRule? _selectedRule;
    private AppSettings _settings = new();
    private QueueFilter _selectedQueueFilter = QueueFilter.All;
    private int _scanCompleted;
    private int _scanTotal;
    private string _currentFile = string.Empty;
    private VideoTaskItem? _currentTaskItem;

    public MainViewModel()
        : this(
            new SettingsService(),
            new FFmpegLocator(),
            new VideoScannerService(new FFprobeService()),
            CreateWorkflowService(),
            System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            new EncoderCapabilityDetector(),
            CreateCompressionTaskPlanner())
    {
    }

    internal MainViewModel(
        SettingsService settingsService,
        FFmpegLocator ffmpegLocator,
        VideoScannerService videoScannerService,
        CompressionWorkflowService workflowService,
        Dispatcher dispatcher,
        EncoderCapabilityDetector? encoderCapabilityDetector = null,
        CompressionTaskPlanner? compressionTaskPlanner = null,
        CompressionHistoryService? historyService = null)
    {
        _settingsService = settingsService;
        _ffmpegLocator = ffmpegLocator;
        _encoderCapabilityDetector = encoderCapabilityDetector ?? new EncoderCapabilityDetector(ffmpegLocator);
        _videoScannerService = videoScannerService;
        _workflowService = workflowService;
        _compressionTaskPlanner = compressionTaskPlanner ?? CreateCompressionTaskPlanner();
        _historyService = historyService ?? new CompressionHistoryService();
        _dispatcher = dispatcher;
        _videoViewRefreshTimer = new DispatcherTimer(DispatcherPriority.DataBind, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _videoViewRefreshTimer.Tick += OnVideoViewRefreshTimerTick;
        _videoView = CollectionViewSource.GetDefaultView(Videos);
        _videoView.Filter = FilterVideo;
        Videos.CollectionChanged += OnVideosCollectionChanged;
        ObserveRules(Settings.Rules);
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => CanEditSettings());
        BrowseOutputDirectoryCommand = new RelayCommand(BrowseOutputDirectory, () => CanEditSettings());
        BrowseOriginalFilesDirectoryCommand = new RelayCommand(BrowseOriginalFilesDirectory, () => CanEditSettings());
        BrowseFfmpegCommand = new RelayCommand(BrowseFfmpeg, () => CanEditSettings());
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanRunDirectoryOperation);
        DirectProcessCommand = new AsyncRelayCommand(DirectProcessAsync, CanRunDirectoryOperation);
        StartCompressionCommand = new AsyncRelayCommand(StartCompressionAsync, CanStartCompression);
        StopCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy && !IsShuttingDown);
        SelectAllCommand = new RelayCommand(() => SetSelection(true), CanEditSettings);
        DeselectAllCommand = new RelayCommand(() => SetSelection(false), CanEditSettings);
        AddRuleCommand = new RelayCommand(AddRule, CanEditSettings);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => CanEditSettings() && SelectedRule is not null);
    }

    public event EventHandler<CompressionTaskReadyEventArgs>? CompressionTaskReady;

    public ObservableCollection<VideoTaskItem> Videos { get; } = [];
    public ObservableCollection<CompressionHistoryEntry> History { get; } = [];
    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            if (ReferenceEquals(_settings, value))
            {
                return;
            }

            ObserveRules(_settings.Rules, subscribe: false);
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            _settings = value;
            _settings.PropertyChanged += OnSettingsPropertyChanged;
            ObserveRules(_settings.Rules);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Rules));
            OnPropertyChanged(nameof(HasRules));
            OnPropertyChanged(nameof(HasEnabledRules));
        }
    }
    public ObservableCollection<CompressionRule> Rules => Settings.Rules;
    public string DirectoryPath
    {
        get => _directoryPath;
        set
        {
            if (SetProperty(ref _directoryPath, value))
            {
                RefreshCommandStates();
            }
        }
    }
    public string FfmpegStatus { get => _ffmpegStatus; private set => SetProperty(ref _ffmpegStatus, value); }
    public string FfmpegToolTip { get => _ffmpegToolTip; private set => SetProperty(ref _ffmpegToolTip, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool IsShuttingDown { get => _isShuttingDown; private set => SetProperty(ref _isShuttingDown, value); }
    public bool HasRules => Rules.Count > 0;
    public bool HasEnabledRules => Rules.Any(rule => rule.IsEnabled);
    public bool HasSelectedVideo => Videos.Any(item => item.IsSelected && item.ConditionResult.IsMatch && IsProcessable(item));
    public ICollectionView VideoView => _videoView;
    public QueueFilter SelectedQueueFilter
    {
        get => _selectedQueueFilter;
        set
        {
            if (SetProperty(ref _selectedQueueFilter, value))
            {
                RequestVideoViewRefresh();
            }
        }
    }

    public string CurrentFile => _currentTaskItem?.FileName ??
        (string.IsNullOrWhiteSpace(_currentFile) ? "—" : _currentFile);
    public int ScanCompleted => _scanCompleted;
    public int ScanTotal => _scanTotal;
    public double ScanProgressPercent => ScanTotal <= 0 ? 0 : Math.Clamp(ScanCompleted / (double)ScanTotal * 100, 0, 100);
    public string ScanProgressDisplay => ScanTotal <= 0 ? "—" : $"{ScanCompleted} / {ScanTotal}";
    public double CurrentFileProgressPercent
    {
        get
        {
            if (_currentTaskItem is { } current &&
                current.Status is VideoTaskStatus.Compressing or VideoTaskStatus.Verifying or VideoTaskStatus.Committing or VideoTaskStatus.Completed)
            {
                return current.ProgressPercent;
            }

            return Videos.FirstOrDefault(item =>
                string.Equals(item.FileName, _currentFile, StringComparison.OrdinalIgnoreCase) &&
                item.Status is VideoTaskStatus.Compressing or VideoTaskStatus.Verifying or VideoTaskStatus.Committing or VideoTaskStatus.Completed)?.ProgressPercent ?? 0;
        }
    }
    public string CurrentFileProgressDisplay => $"{CurrentFileProgressPercent:0.0}%";
    public double OverallProgressPercent => ScanTotal <= 0 ? 0 : Math.Clamp(ScanCompleted / (double)ScanTotal * 100, 0, 100);
    public string OverallProgressDisplay => $"{OverallProgressPercent:0.0}%";
    public int TotalVideos => Videos.Count;
    public int ConditionMatches => Videos.Count(item => item.ConditionResult.State is ConditionResultState.Matches or ConditionResultState.AllAllowed);
    public int ConditionDoesNotMatch => Videos.Count(item => item.ConditionResult.State == ConditionResultState.DoesNotMatch);
    public int ConditionPending => Videos.Count(item => item.ConditionResult.State == ConditionResultState.Pending);
    public int CompletedVideos => Videos.Count(item => item.Status == VideoTaskStatus.Completed);
    public int SkippedVideos => Videos.Count(item => item.Status == VideoTaskStatus.Skipped);
    public int FailedVideos => Videos.Count(item => item.Status == VideoTaskStatus.Failed);
    public int CancelledVideos => Videos.Count(item => item.Status == VideoTaskStatus.Cancelled);
    public int RemainingVideos => Math.Max(0, TotalVideos - CompletedVideos - SkippedVideos - FailedVideos - CancelledVideos);
    public string QueueSummary => $"视频 {TotalVideos} · 符合 {ConditionMatches} · 不符合 {ConditionDoesNotMatch} · 已完成 {CompletedVideos} · 已跳过 {SkippedVideos} · 失败 {FailedVideos} · 剩余 {RemainingVideos}" +
        (ScanTotal > 0 ? $" · 操作 {ScanProgressDisplay}" : string.Empty);
    public string HistoryStatus => History.Count == 0 ? "暂无压缩记录。" : $"最近 {History.Count} 条记录";
    public string ElapsedDisplay => _operationStopwatch is null ? "—" : _operationStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
    public IReadOnlyList<EncoderCapability> EncoderCapabilities => _encoderCapabilities.Capabilities;
    public string EncoderCapabilitySummary
    {
        get => _encoderCapabilitySummary;
        private set => SetProperty(ref _encoderCapabilitySummary, value);
    }
    public CompressionRule? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (SetProperty(ref _selectedRule, value))
            {
                RemoveRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<RuleField> RuleFields { get; } = Enum.GetValues<RuleField>();
    public IReadOnlyList<RuleComparison> RuleComparisons { get; } = Enum.GetValues<RuleComparison>();
    public IReadOnlyList<RuleJoin> RuleJoins { get; } = Enum.GetValues<RuleJoin>();
    public IReadOnlyList<CompressionMode> CompressionModes { get; } = Enum.GetValues<CompressionMode>();
    public IReadOnlyList<string> EncodingPresets { get; } = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    public IReadOnlyList<TargetSizeUnit> TargetSizeUnits { get; } = Enum.GetValues<TargetSizeUnit>();
    public ObservableCollection<VideoEncoder> VideoEncoders { get; } = [VideoEncoder.Libx264, VideoEncoder.Libx265];
    public IReadOnlyList<VideoCodecKind> OutputVideoCodecs { get; } = Enum.GetValues<VideoCodecKind>();
    public ObservableCollection<EncoderSelectionOption> EncoderModes { get; } = [];
    public IReadOnlyList<SmartCompressionPreset> SmartPresets { get; } = Enum.GetValues<SmartCompressionPreset>();
    public IReadOnlyList<QueueFilter> QueueFilters { get; } = Enum.GetValues<QueueFilter>();
    public IReadOnlyList<AudioMode> AudioModes { get; } = Enum.GetValues<AudioMode>();
    public IReadOnlyList<OutputLocationMode> OutputLocations { get; } = Enum.GetValues<OutputLocationMode>();
    public IReadOnlyList<OriginalFileAction> OriginalFileActions { get; } = Enum.GetValues<OriginalFileAction>();
    public IReadOnlyList<ResolutionLimitPreset> ResolutionPresets { get; } = Enum.GetValues<ResolutionLimitPreset>();
    public IReadOnlyList<FpsLimitPreset> FpsPresets { get; } = Enum.GetValues<FpsLimitPreset>();

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand BrowseOutputDirectoryCommand { get; }
    public RelayCommand BrowseOriginalFilesDirectoryCommand { get; }
    public RelayCommand BrowseFfmpegCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand DirectProcessCommand { get; }
    public AsyncRelayCommand StartCompressionCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand AddRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized || IsShuttingDown)
        {
            return;
        }

        _initialized = true;
        try
        {
            Settings = await _settingsService.LoadAsync(_lifetimeCancellation.Token);
            if (IsShuttingDown)
            {
                return;
            }

            DirectoryPath = Settings.LastDirectory;
            await RefreshFfmpegAsync(_lifetimeCancellation.Token);
            await RefreshHistoryAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing while startup is checking local settings or FFmpeg is expected.
        }
    }

    public async Task PersistSettingsAsync(CancellationToken cancellationToken = default)
    {
        Settings.LastDirectory = DirectoryPath;
        await _settingsService.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsShuttingDown)
        {
            return;
        }

        var entries = await _historyService.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            History.Clear();
            foreach (var entry in entries.Take(100))
            {
                History.Add(entry);
            }

            OnPropertyChanged(nameof(HistoryStatus));
        }, DispatcherPriority.Background, cancellationToken);
    }

    public Task ShutdownAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return _shutdownTask ??= ShutdownCoreAsync(timeout);
    }

    private void BrowseFolder()
    {
        BrowseDirectory(
            "选择要扫描或直接处理的视频目录",
            DirectoryPath,
            path =>
            {
                DirectoryPath = path;
                StatusMessage = "已选择目录。可以扫描视频，或直接处理目录。";
            });
    }

    private void BrowseOutputDirectory()
    {
        BrowseDirectory(
            "选择统一压缩输出目录",
            Settings.OutputDirectory,
            path =>
            {
                Settings.OutputDirectory = path;
                Settings.OutputLocation = OutputLocationMode.SelectedDirectory;
                StatusMessage = "已选择统一输出目录，并切换到“指定目录”输出模式。";
            });
    }

    private void BrowseOriginalFilesDirectory()
    {
        BrowseDirectory(
            "选择原文件归档目录",
            Settings.OriginalFilesDirectory,
            path =>
            {
                Settings.OriginalFilesDirectory = path;
                Settings.OriginalFileAction = OriginalFileAction.MoveToSelectedDirectory;
                StatusMessage = "已选择原文件归档目录，并启用“移动到指定目录”。";
            });
    }

    private void BrowseFfmpeg()
    {
        var initialDirectory = Directory.Exists(Settings.FFmpegDirectory)
            ? Settings.FFmpegDirectory
            : Directory.Exists(Path.GetDirectoryName(Settings.FFmpegDirectory))
                ? Path.GetDirectoryName(Settings.FFmpegDirectory)
                : null;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|可执行文件 (*.exe)|*.exe|所有文件|*.*",
            InitialDirectory = initialDirectory,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            if (!string.Equals(Path.GetFileName(dialog.FileName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "请选择 ffmpeg.exe；程序也会在同一目录检查 ffprobe.exe。";
                return;
            }

            var directory = Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName;
            Settings.FFmpegDirectory = directory;
            StatusMessage = File.Exists(Path.Combine(directory, "ffprobe.exe"))
                ? "已选择包含 ffmpeg.exe 与 ffprobe.exe 的目录。"
                : "已选择 FFmpeg 目录，但同一目录尚未找到 ffprobe.exe。";
        }
    }

    private void BrowseDirectory(string description, string? selectedPath, Action<string> onSelected)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(selectedPath)
                ? selectedPath
                : Directory.Exists(DirectoryPath)
                    ? DirectoryPath
                    : string.Empty
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            onSelected(dialog.SelectedPath);
        }
    }

    private async Task ScanAsync()
    {
        if (IsShuttingDown)
        {
            return;
        }
        if (!ValidateDirectory())
        {
            return;
        }
        if (!await EnsureToolsAsync())
        {
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        var cancellationToken = _operationCancellation!.Token;
        Videos.Clear();
        _scanCompleted = 0;
        _scanTotal = 0;
        _currentFile = string.Empty;
        _currentTaskItem = null;
        RefreshQueueProperties();
        try
        {
            StatusMessage = "正在枚举视频并读取媒体信息…";
            await PersistSettingsAsync();
            await _videoScannerService.ScanAsync(
                DirectoryPath,
                Settings.RecursiveScan,
                Settings.ProbeConcurrency,
                _tools!,
                async info =>
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (IsShuttingDown)
                        {
                            return;
                        }

                        var item = new VideoTaskItem(info);
                        ApplyConditionResult(item, resetTaskStatus: true);
                        Videos.Add(item);
                    }, DispatcherPriority.Background, cancellationToken);
                },
                async (path, message) =>
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (IsShuttingDown)
                        {
                            return;
                        }

                        var item = new VideoTaskItem(VideoFileInfo.FromFile(path))
                        {
                            Status = VideoTaskStatus.Failed,
                            StatusDetail = $"ffprobe 分析失败：{message}",
                            IsSelected = false
                        };
                        item.ApplyConditionResult(new ConditionEvaluationResult(
                            ConditionResultState.Failed,
                            false,
                            "判断失败：ffprobe 分析失败。",
                            [$"ffprobe 分析失败：{message}"],
                            $"ffprobe 分析失败：{message}"));
                        Videos.Add(item);
                    }, DispatcherPriority.Background, cancellationToken);
                },
                cancellationToken,
                async scanProgress =>
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (IsShuttingDown)
                        {
                            return;
                        }

                        _scanCompleted = scanProgress.Completed;
                        _scanTotal = scanProgress.Total;
                        _currentTaskItem = null;
                        _currentFile = string.IsNullOrWhiteSpace(scanProgress.CurrentPath)
                            ? _currentFile
                            : Path.GetFileName(scanProgress.CurrentPath);
                        StatusMessage = scanProgress.IsComplete
                            ? $"扫描完成：{scanProgress.Total} 个视频。"
                            : $"正在分析：{scanProgress.Completed} / {scanProgress.Total}" +
                              (string.IsNullOrWhiteSpace(scanProgress.CurrentPath) ? string.Empty : $" 当前：{Path.GetFileName(scanProgress.CurrentPath)}");
                        RefreshQueueProperties();
                    }, DispatcherPriority.Background, cancellationToken);
                });
            StatusMessage = $"扫描完成：{TotalVideos} 个视频，符合 {ConditionMatches}，不符合 {ConditionDoesNotMatch}，失败 {FailedVideos}。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"扫描失败：{exception.Message}";
        }
        finally
        {
            RefreshQueueProperties();
            EndOperation();
        }
    }

    private async Task DirectProcessAsync()
    {
        if (IsShuttingDown)
        {
            return;
        }
        if (!ValidateDirectory())
        {
            return;
        }
        if (!await EnsureToolsAsync())
        {
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        var cancellationToken = _operationCancellation!.Token;
        Videos.Clear();
        var snapshot = Settings.Clone();
        var pending = new List<Task>();
        var seenInputs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var generatedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var started = 0;
        _scanCompleted = 0;
        _scanTotal = 0;
        _currentFile = string.Empty;
        _currentTaskItem = null;
        RefreshQueueProperties();
        try
        {
            await PersistSettingsAsync();
            StatusMessage = "直接处理模式：不会预扫描整个目录，文件会逐个判断。";
            using var semaphore = new SemaphoreSlim(snapshot.CompressionConcurrency, snapshot.CompressionConcurrency);
            await foreach (var path in _videoScannerService.EnumerateVideoPathsAsync(DirectoryPath, snapshot.RecursiveScan, cancellationToken))
            {
                var fullPath = Path.GetFullPath(path);
                if (IsInternalTemporaryPath(fullPath) || !seenInputs.TryAdd(fullPath, 0) || generatedPaths.ContainsKey(fullPath))
                {
                    continue;
                }

                var item = new VideoTaskItem(VideoFileInfo.FromFile(path));
                ReserveGeneratedPaths(item.Media, snapshot, generatedPaths);
                Videos.Add(item);
                started++;
                _scanTotal = started;
                StatusMessage = $"直接处理：已发现 {started} 个视频，当前：{item.FileName}";
                pending.Add(ProcessItemWithGateAsync(item, snapshot, semaphore, cancellationToken, generatedPaths));
                if (pending.Count >= snapshot.CompressionConcurrency * 4)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    await completed;
                }
            }

            await Task.WhenAll(pending);
            _scanCompleted = started;
            StatusMessage = cancellationToken.IsCancellationRequested
                ? "直接处理已取消。"
                : $"直接处理完成：已检查 {started} 个视频。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "直接处理已取消。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"直接处理失败：{exception.Message}";
        }
        finally
        {
            try
            {
                await Task.WhenAll(pending);
            }
            catch (Exception)
            {
                // Individual tasks turn expected cancellation/failure into an item status before reaching here.
            }
            RefreshQueueProperties();
            EndOperation();
        }
    }

    private async Task StartCompressionAsync()
    {
        if (IsShuttingDown)
        {
            return;
        }
        ReevaluateAllConditions(resetTaskStatus: true);
        var selected = Videos
            .Where(item => item.IsSelected && item.ConditionResult.IsMatch && IsProcessable(item))
            .ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "没有同时满足“已勾选”和“符合压缩条件”的可处理视频。";
            return;
        }
        if (!await EnsureToolsAsync())
        {
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        var cancellationToken = _operationCancellation!.Token;
        try
        {
            var snapshot = Settings.Clone();
            await PersistSettingsAsync();
            StatusMessage = $"正在为 {selected.Count} 个视频生成压缩计划…";
            var session = await _compressionTaskPlanner.CreateSessionAsync(
                selected,
                snapshot,
                DirectoryPath,
                _tools!,
                _encoderCapabilities,
                cancellationToken);
            if (session.Entries.Count == 0)
            {
                var smartSkippedCount = session.PlanningNotes.Count(note => note.Contains("：智能跳过：", StringComparison.Ordinal));
                StatusMessage = smartSkippedCount == selected.Count
                    ? $"{selected.Count} 个已选视频均被智能判断为无需重新压缩。请在主列表查看“智能跳过”和原因。"
                    : session.PlanningNotes.Count == 0
                        ? "没有视频进入压缩计划。"
                        : $"没有视频进入压缩计划：{session.PlanningNotes[0]}";
                return;
            }

            StatusMessage = $"已生成压缩计划：{session.Entries.Count} 个视频。请在“压缩任务”页面确认后开始。";
            CompressionTaskReady?.Invoke(this, new CompressionTaskReadyEventArgs(session, _workflowService, _tools!));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "压缩已取消。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"批量压缩失败：{exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ProcessItemWithGateAsync(
        VideoTaskItem item,
        AppSettings snapshot,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken,
        ConcurrentDictionary<string, byte>? generatedPaths = null)
    {
        var acquiredSemaphore = false;
        try
        {
            item.Status = VideoTaskStatus.Queued;
            item.StatusDetail = "排队中";
            _currentTaskItem = item;
            _currentFile = item.FileName;
            RefreshQueueProperties();
            await semaphore.WaitAsync(cancellationToken);
            acquiredSemaphore = true;
            var progress = new Progress<WorkflowProgress>(update => ApplyWorkflowProgress(item, update));
            var result = await _workflowService.ProcessFileAsync(
                item.Media,
                snapshot,
                _tools!,
                DirectoryPath,
                progress,
                cancellationToken,
                _encoderCapabilities);
            if (result.SourceInfo is not null)
            {
                item.UpdateMedia(result.SourceInfo);
            }
            if (result.Condition is not null)
            {
                item.ApplyConditionResult(result.Condition);
            }
            item.ApplySmartDecision(result.SmartDecision);
            item.ApplySelectedEncoder(result.Encoder);
            item.Status = result.Status;
            item.StatusDetail = result.Message;
            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                generatedPaths?.TryAdd(Path.GetFullPath(result.OutputPath), 0);
            }
            if (result.Status == VideoTaskStatus.Completed)
            {
                item.ProgressPercent = 100;
                item.RefreshDisplays();
            }
            Interlocked.Increment(ref _scanCompleted);
            RefreshQueueProperties();
        }
        catch (OperationCanceledException)
        {
            item.Status = VideoTaskStatus.Cancelled;
            item.StatusDetail = "任务已取消，原文件未被修改。";
            Interlocked.Increment(ref _scanCompleted);
            RefreshQueueProperties();
        }
        catch (Exception exception)
        {
            item.Status = VideoTaskStatus.Failed;
            item.StatusDetail = $"未处理：{exception.Message}";
            Interlocked.Increment(ref _scanCompleted);
            RefreshQueueProperties();
        }
        finally
        {
            if (acquiredSemaphore)
            {
                semaphore.Release();
            }
        }
    }

    private void ApplyWorkflowProgress(VideoTaskItem item, WorkflowProgress update)
    {
        if (IsShuttingDown)
        {
            return;
        }

        item.Status = update.Status;
        item.StatusDetail = update.Detail;
        if (update.Condition is not null)
        {
            item.ApplyConditionResult(update.Condition);
        }
        if (update.SmartDecision is not null)
        {
            item.ApplySmartDecision(update.SmartDecision);
        }
        if (update.Encoder is not null)
        {
            item.ApplySelectedEncoder(update.Encoder);
        }
        if (update.Encoding is not null)
        {
            item.ApplyProgress(update.Encoding);
        }
        _currentTaskItem = item;
        _currentFile = item.FileName;
        RefreshQueueProperties();
    }

    private void ReserveGeneratedPaths(
        VideoFileInfo source,
        AppSettings settings,
        ConcurrentDictionary<string, byte> generatedPaths)
    {
        try
        {
            var outputPath = _outputPathService.GetOutputPath(source, settings, DirectoryPath);
            generatedPaths.TryAdd(Path.GetFullPath(outputPath), 0);
            if (settings.OriginalFileAction != OriginalFileAction.Keep)
            {
                generatedPaths.TryAdd(Path.GetFullPath(_outputPathService.GetOriginalDestinationPath(source, settings, outputPath)), 0);
            }
        }
        catch (Exception)
        {
            // The workflow will show the user the configuration error for this individual file.
        }
    }

    private static bool IsInternalTemporaryPath(string path) =>
        Path.GetFileName(path).Contains(".clvc-", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> EnsureToolsAsync()
    {
        if (IsShuttingDown)
        {
            return false;
        }

        _tools = _ffmpegLocator.Locate(Settings.FFmpegDirectory);
        if (_tools is null)
        {
            StatusMessage = "找不到完整的 ffmpeg.exe 与 ffprobe.exe。请把它们放在软件目录、加入 PATH，或在“命名与性能”页填写 FFmpeg 所在目录。";
            return false;
        }

        if (_capabilityDetectionTask is null)
        {
            StartCapabilityDetection();
        }

        try
        {
            await _capabilityDetectionTask!.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        return true;
    }

    private Task RefreshFfmpegAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tools = _ffmpegLocator.Locate(Settings.FFmpegDirectory);
        if (_tools is null)
        {
            FfmpegStatus = "未找到";
            FfmpegToolTip = "未找到同一目录下的 ffmpeg.exe 与 ffprobe.exe。";
            return Task.CompletedTask;
        }

        FfmpegStatus = "已就绪 · 正在检测硬件编码器…";
        FfmpegToolTip = $"ffmpeg.exe：{_tools.FFmpegPath}{Environment.NewLine}ffprobe.exe：{_tools.FFprobePath}";
        StartCapabilityDetection();
        return Task.CompletedTask;
    }

    private void StartCapabilityDetection()
    {
        if (_tools is null || _capabilityDetectionTask is { IsCompleted: false })
        {
            return;
        }

        var tools = _tools;
        _capabilityDetectionTask = DetectCapabilitiesAsync(tools, _lifetimeCancellation.Token);
    }

    private async Task DetectCapabilitiesAsync(FFmpegTools tools, CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await _encoderCapabilityDetector.DetectAsync(tools, cancellationToken).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                _encoderCapabilities = capabilities;
                OnPropertyChanged(nameof(EncoderCapabilities));
                UpdateEncoderModes();
                EncoderCapabilitySummary = BuildEncoderCapabilitySummary(capabilities);
                var version = capabilities.Capabilities.Count == 0 ? null : "能力已检测";
                FfmpegStatus = string.IsNullOrWhiteSpace(version) ? "已就绪" : $"已就绪 · {version}";
                FfmpegToolTip = BuildFfmpegToolTip(tools, capabilities);
            }, DispatcherPriority.Background, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("encoder-detect", $"detection failed: {exception.Message}");
            await _dispatcher.InvokeAsync(() =>
            {
                _encoderCapabilities = EncoderCapabilitySet.SoftwareDefaults;
                OnPropertyChanged(nameof(EncoderCapabilities));
                UpdateEncoderModes();
                EncoderCapabilitySummary = $"硬件编码器检测失败：{exception.Message}";
                FfmpegStatus = "已就绪（硬件未验证）";
            }, DispatcherPriority.Background, CancellationToken.None);
        }
    }

    private void UpdateEncoderModes()
    {
        var selected = Settings.SelectedEncoderSelection;
        EncoderModes.Clear();
        EncoderModes.Add(new EncoderSelectionOption(EncoderSelectionMode.Automatic, true));
        EncoderModes.Add(new EncoderSelectionOption(EncoderSelectionMode.CpuSoftware, true));
        EncoderModes.Add(new EncoderSelectionOption(EncoderSelectionMode.HardwareAutomatic, HasAnyUsableHardware(Settings.SelectedVideoCodec),
            HasAnyUsableHardware(Settings.SelectedVideoCodec) ? null : "当前编码格式没有通过检测的硬件编码器"));
        EncoderModes.Add(CreateHardwareSelectionOption(
            EncoderSelectionMode.NvidiaNvenc,
            EncoderVendor.Nvidia,
            "未检测到可用的 NVIDIA NVENC 编码环境"));
        EncoderModes.Add(CreateHardwareSelectionOption(
            EncoderSelectionMode.IntelQsv,
            EncoderVendor.Intel,
            "未检测到可用的 Intel Quick Sync 编码环境"));
        EncoderModes.Add(CreateHardwareSelectionOption(
            EncoderSelectionMode.AmdAmf,
            EncoderVendor.Amd,
            "未检测到可用的 AMD AMF 编码环境"));

        var selectedOption = EncoderModes.FirstOrDefault(option => option.Mode == selected);
        if (selectedOption is null || !selectedOption.IsAvailable)
        {
            Settings.SelectedEncoderSelection = EncoderSelectionMode.CpuSoftware;
        }
    }

    private EncoderSelectionOption CreateHardwareSelectionOption(
        EncoderSelectionMode mode,
        EncoderVendor vendor,
        string defaultReason)
    {
        var capability = _encoderCapabilities.Capabilities.FirstOrDefault(candidate =>
            candidate.IsHardware && candidate.Vendor == vendor && candidate.Codec == Settings.SelectedVideoCodec);
        return new EncoderSelectionOption(
            mode,
            capability?.IsUsable == true,
            capability is null
                ? defaultReason
                : CompactCapabilityReason(capability.UnavailableReason, defaultReason));
    }

    private bool HasAnyUsableHardware(VideoCodecKind codec) =>
        _encoderCapabilities.Capabilities.Any(capability =>
            capability.IsHardware && capability.Codec == codec && capability.IsUsable);

    private static string BuildEncoderCapabilitySummary(EncoderCapabilitySet capabilities)
    {
        var groups = capabilities.Capabilities
            .Where(capability => capability.IsHardware)
            .GroupBy(capability => capability.Vendor)
            .Select(group =>
            {
                var usable = group.Where(capability => capability.IsUsable).Select(capability => capability.Codec.GetDescription()).ToArray();
                return usable.Length == 0
                    ? $"{group.Key.GetDescription()}：不可用（{CompactCapabilityReason(group.First().UnavailableReason, "未通过能力检测")}）"
                    : $"{group.Key.GetDescription()}：可用（{string.Join(" / ", usable.Distinct())}）";
            });
        return string.Join("；", groups);
    }

    private static string BuildFfmpegToolTip(FFmpegTools tools, EncoderCapabilitySet capabilities) =>
        $"ffmpeg.exe：{tools.FFmpegPath}{Environment.NewLine}ffprobe.exe：{tools.FFprobePath}{Environment.NewLine}" +
        string.Join(Environment.NewLine, capabilities.Capabilities.Select(capability =>
            $"{capability.Id}: {(capability.IsUsable ? "可用" : CompactCapabilityReason(capability.UnavailableReason, "未通过能力检测"))}"));

    private static string CompactCapabilityReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return fallback;
        }

        if (reason.Contains("nvcuda.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA 驱动 / CUDA 编码环境不可用（未找到 nvcuda.dll）";
        }

        if (reason.Contains("amfrt64.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD 驱动 / AMF 编码环境不可用（未找到 amfrt64.dll）";
        }

        var firstLine = reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length <= 140 ? firstLine : $"{firstLine[..140]}…";
    }

    private void ApplyConditionResult(VideoTaskItem item, bool resetTaskStatus)
    {
        var result = _ruleEngine.Evaluate(item.Media, Settings.Rules);
        item.ApplyConditionResult(result);
        if (!resetTaskStatus || item.Status is VideoTaskStatus.Compressing or VideoTaskStatus.Verifying or VideoTaskStatus.Committing or VideoTaskStatus.Completed)
        {
            return;
        }

        if (result.State == ConditionResultState.Failed)
        {
            item.Status = VideoTaskStatus.Failed;
            item.StatusDetail = result.Summary;
        }
        else if (result.IsMatch)
        {
            item.Status = VideoTaskStatus.Eligible;
            item.StatusDetail = "符合条件，等待处理。";
        }
        else
        {
            item.Status = VideoTaskStatus.Skipped;
            item.StatusDetail = "跳过：不符合压缩条件。";
        }
    }

    private void ReevaluateAllConditions(bool resetTaskStatus)
    {
        foreach (var item in Videos)
        {
            if (!_ruleEngine.RequiresProbe(Settings.Rules) || item.ConditionResult.State == ConditionResultState.Pending || item.Media.HasProbeData)
            {
                ApplyConditionResult(item, resetTaskStatus);
            }
        }

        RefreshQueueProperties();
    }

    private bool FilterVideo(object value)
    {
        if (value is not VideoTaskItem item)
        {
            return false;
        }

        return SelectedQueueFilter switch
        {
            QueueFilter.ConditionMatches => item.ConditionResult.State is ConditionResultState.Matches or ConditionResultState.AllAllowed,
            QueueFilter.ConditionNotMatches => item.ConditionResult.State == ConditionResultState.DoesNotMatch,
            QueueFilter.Processing => item.Status is VideoTaskStatus.Analyzing or VideoTaskStatus.Queued or VideoTaskStatus.Compressing or VideoTaskStatus.Verifying or VideoTaskStatus.Committing,
            QueueFilter.Completed => item.Status == VideoTaskStatus.Completed,
            QueueFilter.Failed => item.Status == VideoTaskStatus.Failed,
            _ => true
        };
    }

    private void RefreshQueueProperties()
    {
        OnPropertyChanged(nameof(TotalVideos));
        OnPropertyChanged(nameof(ConditionMatches));
        OnPropertyChanged(nameof(ConditionDoesNotMatch));
        OnPropertyChanged(nameof(ConditionPending));
        OnPropertyChanged(nameof(CompletedVideos));
        OnPropertyChanged(nameof(SkippedVideos));
        OnPropertyChanged(nameof(FailedVideos));
        OnPropertyChanged(nameof(CancelledVideos));
        OnPropertyChanged(nameof(RemainingVideos));
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(CurrentFile));
        OnPropertyChanged(nameof(ScanCompleted));
        OnPropertyChanged(nameof(ScanTotal));
        OnPropertyChanged(nameof(ScanProgressPercent));
        OnPropertyChanged(nameof(ScanProgressDisplay));
        OnPropertyChanged(nameof(CurrentFileProgressPercent));
        OnPropertyChanged(nameof(CurrentFileProgressDisplay));
        OnPropertyChanged(nameof(OverallProgressPercent));
        OnPropertyChanged(nameof(OverallProgressDisplay));
        OnPropertyChanged(nameof(ElapsedDisplay));
        RequestVideoViewRefresh();
    }

    private void RequestVideoViewRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.BeginInvoke(RequestVideoViewRefresh, DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException)
            {
                // The dispatcher is shutting down; the view no longer needs refreshes.
            }

            return;
        }

        if (_videoViewRefreshPending)
        {
            return;
        }

        _videoViewRefreshPending = true;
        _videoViewRefreshTimer.Start();
    }

    private void OnVideoViewRefreshTimerTick(object? sender, EventArgs e)
    {
        _videoViewRefreshTimer.Stop();
        _videoViewRefreshPending = false;
        if (!_disposed)
        {
            _videoView.Refresh();
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.VideoEncoder) or nameof(AppSettings.TargetVideoCodec) or nameof(AppSettings.EncoderSelection))
        {
            OnPropertyChanged(nameof(Settings.SelectedVideoCodec));
            OnPropertyChanged(nameof(Settings.SelectedEncoderSelection));
            UpdateEncoderModes();
        }
    }

    private void AddRule()
    {
        var rule = new CompressionRule
        {
            JoinWithPrevious = RuleJoin.And,
            Field = RuleField.FileSize,
            Comparison = RuleComparison.GreaterThan,
            Value = "1"
        };
        Rules.Add(rule);
        NormalizeRuleJoins();
        SelectedRule = rule;
    }

    private void RemoveRule()
    {
        if (SelectedRule is null)
        {
            return;
        }

        Rules.Remove(SelectedRule);
        SelectedRule = null;
        NormalizeRuleJoins();
    }

    private void SetSelection(bool value)
    {
        foreach (var item in Videos.Where(item => IsProcessable(item)))
        {
            item.IsSelected = value;
        }
    }

    private bool ValidateDirectory()
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath) || !Directory.Exists(DirectoryPath))
        {
            StatusMessage = "请选择存在的视频目录。";
            return false;
        }

        return true;
    }

    private bool BeginOperation()
    {
        if (IsShuttingDown || _operationCancellation is not null)
        {
            return false;
        }

        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationStopwatch = Stopwatch.StartNew();
        IsBusy = true;
        RefreshQueueProperties();
        RefreshCommandStates();
        return true;
    }

    private void EndOperation()
    {
        var completion = _operationCompletion;
        _operationCompletion = null;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _operationStopwatch?.Stop();
        IsBusy = false;
        RefreshQueueProperties();
        RefreshCommandStates();
        completion?.TrySetResult();
    }

    private void CancelCurrentOperation()
    {
        if (_operationCancellation is { IsCancellationRequested: false })
        {
            if (!IsShuttingDown)
            {
                StatusMessage = "正在请求取消当前 FFmpeg 任务…";
            }
            _operationCancellation.Cancel();
        }
    }

    private async Task ShutdownCoreAsync(TimeSpan timeout)
    {
        IsShuttingDown = true;
        StatusMessage = IsBusy ? "正在安全停止后台任务…" : "正在保存设置并退出…";
        RefreshCommandStates();

        _lifetimeCancellation.Cancel();
        CancelCurrentOperation();
        MediaProcessRegistry.TerminateAll();

        var operation = _operationCompletion?.Task ?? Task.CompletedTask;
        var operationCompleted = await WaitForCompletionAsync(operation, timeout);
        if (!operationCompleted)
        {
            // The cancellation registrations normally kill processes immediately. Retry the app-owned registry
            // after the bounded wait so a resistant process can never keep the window open forever.
            MediaProcessRegistry.TerminateAll();
        }

        var capabilityTask = _capabilityDetectionTask;
        if (capabilityTask is not null)
        {
            try
            {
                await WaitForCompletionAsync(capabilityTask, timeout);
            }
            catch (Exception)
            {
                // Capability detection is best-effort during shutdown.
            }
        }

        try
        {
            await WaitForCompletionAsync(PersistSettingsAsync(), TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // A JSON write failure must not make the user unable to close the application.
        }
    }

    private static async Task<bool> WaitForCompletionAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            await task;
            return true;
        }

        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            return false;
        }

        await task;
        return true;
    }

    private bool CanEditSettings() => !IsBusy && !IsShuttingDown;

    private bool CanRunDirectoryOperation() =>
        CanEditSettings() && !string.IsNullOrWhiteSpace(DirectoryPath) && Directory.Exists(DirectoryPath);

    private bool CanStartCompression() => CanEditSettings() && HasSelectedVideo;

    private static bool IsProcessable(VideoTaskItem item) =>
        item.Status is VideoTaskStatus.Waiting or VideoTaskStatus.Eligible or VideoTaskStatus.Skipped or VideoTaskStatus.Failed;

    private void OnVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _observedVideoItems)
            {
                item.PropertyChanged -= OnVideoTaskItemPropertyChanged;
            }
            _observedVideoItems.Clear();
        }
        if (e.OldItems is not null)
        {
            foreach (VideoTaskItem item in e.OldItems)
            {
                item.PropertyChanged -= OnVideoTaskItemPropertyChanged;
                _observedVideoItems.Remove(item);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (VideoTaskItem item in e.NewItems)
            {
                item.PropertyChanged += OnVideoTaskItemPropertyChanged;
                _observedVideoItems.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasSelectedVideo));
        StartCompressionCommand.RaiseCanExecuteChanged();
        RefreshQueueProperties();
    }

    private void OnVideoTaskItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoTaskItem.IsSelected) or nameof(VideoTaskItem.Status) or nameof(VideoTaskItem.ConditionResult))
        {
            OnPropertyChanged(nameof(HasSelectedVideo));
            StartCompressionCommand.RaiseCanExecuteChanged();
            RefreshQueueProperties();
        }
    }

    private void ObserveRules(ObservableCollection<CompressionRule> rules, bool subscribe = true)
    {
        if (subscribe)
        {
            rules.CollectionChanged += OnRulesCollectionChanged;
            foreach (var rule in rules)
            {
                rule.PropertyChanged += OnRulePropertyChanged;
            }
            NormalizeRuleJoins();
            return;
        }

        rules.CollectionChanged -= OnRulesCollectionChanged;
        foreach (var rule in rules)
        {
            rule.PropertyChanged -= OnRulePropertyChanged;
        }
    }

    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CompressionRule rule in e.OldItems)
            {
                rule.PropertyChanged -= OnRulePropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (CompressionRule rule in e.NewItems)
            {
                rule.PropertyChanged += OnRulePropertyChanged;
            }
        }

        NormalizeRuleJoins();
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasEnabledRules));
        ReevaluateAllConditions(resetTaskStatus: true);
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CompressionRule.IsEnabled) or nameof(CompressionRule.Field) or nameof(CompressionRule.Comparison) or nameof(CompressionRule.Value) or nameof(CompressionRule.JoinWithPrevious))
        {
            OnPropertyChanged(nameof(HasEnabledRules));
            ReevaluateAllConditions(resetTaskStatus: true);
        }
    }

    private void NormalizeRuleJoins()
    {
        if (Rules.Count > 0 && Rules[0].JoinWithPrevious != RuleJoin.And)
        {
            Rules[0].JoinWithPrevious = RuleJoin.And;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _videoViewRefreshTimer.Stop();
        _videoViewRefreshTimer.Tick -= OnVideoViewRefreshTimerTick;
        _lifetimeCancellation.Cancel();
        CancelCurrentOperation();
        MediaProcessRegistry.TerminateAll();
        Videos.CollectionChanged -= OnVideosCollectionChanged;
        foreach (var item in _observedVideoItems)
        {
            item.PropertyChanged -= OnVideoTaskItemPropertyChanged;
        }
        _observedVideoItems.Clear();
        ObserveRules(Settings.Rules, subscribe: false);
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        if (!IsBusy)
        {
            _lifetimeCancellation.Dispose();
        }
    }

    public void ApplyTaskSessionResults(CompressionTaskSession session)
    {
        foreach (var entry in session.Entries)
        {
            var item = Videos.FirstOrDefault(video =>
                string.Equals(video.FullPath, entry.Source.FullPath, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                continue;
            }

            item.ApplySelectedEncoder(entry.ActualEncoder ?? entry.Plan.Encoder);
            item.OutputSizeBytes = entry.ActualOutputSizeBytes;
            item.ProgressPercent = entry.ProgressPercent;
            item.StatusDetail = entry.StatusDetail;
            item.Status = entry.ExecutionState switch
            {
                CompressionExecutionState.Completed => VideoTaskStatus.Completed,
                CompressionExecutionState.Cancelled => VideoTaskStatus.Cancelled,
                CompressionExecutionState.Failed => VideoTaskStatus.Failed,
                CompressionExecutionState.Abandoned => VideoTaskStatus.Skipped,
                _ => item.Status
            };
            item.RefreshDisplays();
        }

        RefreshQueueProperties();
    }

    private void RefreshCommandStates()
    {
        BrowseFolderCommand.RaiseCanExecuteChanged();
        BrowseOutputDirectoryCommand.RaiseCanExecuteChanged();
        BrowseOriginalFilesDirectoryCommand.RaiseCanExecuteChanged();
        BrowseFfmpegCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        DirectProcessCommand.RaiseCanExecuteChanged();
        StartCompressionCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        DeselectAllCommand.RaiseCanExecuteChanged();
        AddRuleCommand.RaiseCanExecuteChanged();
        RemoveRuleCommand.RaiseCanExecuteChanged();
    }

    private static CompressionWorkflowService CreateWorkflowService()
    {
        var ffprobe = new FFprobeService();
        return new CompressionWorkflowService(
            new RuleEngine(),
            ffprobe,
            new FFmpegService(),
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(ffprobe));
    }

    private static CompressionTaskPlanner CreateCompressionTaskPlanner()
    {
        var ffprobe = new FFprobeService();
        return new CompressionTaskPlanner(
            new RuleEngine(),
            ffprobe,
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new VmafQualityCalibrationService());
    }
}
