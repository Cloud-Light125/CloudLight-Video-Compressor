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
    private static readonly FFprobeService DefaultProbeService = new();
    private static readonly FFmpegService DefaultFfmpegService = new();
    private static readonly MediaProbeCache DefaultProbeCache = new();
    private static readonly CompressionResultCache DefaultResultCache = new();
    private static readonly EncoderBenchmarkCache DefaultBenchmarkCache = new();
    private static readonly EncoderBenchmarkService DefaultBenchmarkService = new(
        DefaultFfmpegService,
        DefaultBenchmarkCache);
    private static readonly MediaHealthCheckService DefaultHealthCheckService = new(
        DefaultProbeCache,
        DefaultProbeService,
        DefaultFfmpegService);

    private readonly SettingsService _settingsService;
    private readonly FFmpegLocator _ffmpegLocator;
    private readonly EncoderCapabilityDetector _encoderCapabilityDetector;
    private readonly VideoScannerService _videoScannerService;
    private readonly FFprobeService _directProbeService = new();
    private readonly CompressionWorkflowService _workflowService;
    private readonly CompressionTaskPlanner _compressionTaskPlanner;
    private readonly CompressionHistoryService _historyService;
    private readonly CompressionTaskSessionStore _sessionStore;
    private readonly EncoderBenchmarkService _benchmarkService;
    private readonly EncoderBenchmarkCache _benchmarkCache;
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
    private bool _isBenchmarking;
    private int _benchmarkCompleted;
    private int _benchmarkTotal;
    private string _benchmarkStatus = "尚未进行本机性能测试。";
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
    private int _scanCacheHits;
    private int _scanActualProbes;
    private string _currentFile = string.Empty;
    private VideoTaskItem? _currentTaskItem;
    private readonly ObservableCollection<EncoderBenchmarkDisplayRow> _benchmarkRows = [];

    public MainViewModel()
        : this(
            new SettingsService(),
            new FFmpegLocator(),
            CreateVideoScannerService(),
            CreateWorkflowService(),
            System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            new EncoderCapabilityDetector(),
            CreateCompressionTaskPlanner(),
            benchmarkService: DefaultBenchmarkService,
            benchmarkCache: DefaultBenchmarkCache)
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
        CompressionHistoryService? historyService = null,
        CompressionTaskSessionStore? sessionStore = null,
        EncoderBenchmarkService? benchmarkService = null,
        EncoderBenchmarkCache? benchmarkCache = null)
    {
        _settingsService = settingsService;
        _ffmpegLocator = ffmpegLocator;
        _encoderCapabilityDetector = encoderCapabilityDetector ?? new EncoderCapabilityDetector(ffmpegLocator);
        _videoScannerService = videoScannerService;
        _workflowService = workflowService;
        _compressionTaskPlanner = compressionTaskPlanner ?? CreateCompressionTaskPlanner();
        _historyService = historyService ?? new CompressionHistoryService();
        _sessionStore = sessionStore ?? new CompressionTaskSessionStore();
        _benchmarkService = benchmarkService ?? DefaultBenchmarkService;
        _benchmarkCache = benchmarkCache ?? _benchmarkService.Cache;
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
        RunBenchmarkCommand = new AsyncRelayCommand(RunBenchmarkAsync, CanRunBenchmark);
        StopCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy && !IsShuttingDown);
        CancelBenchmarkCommand = new RelayCommand(CancelCurrentOperation, () => IsBenchmarking && !IsShuttingDown);
        SelectAllCommand = new RelayCommand(() => SetSelection(true), CanEditSettings);
        DeselectAllCommand = new RelayCommand(() => SetSelection(false), CanEditSettings);
        AddRuleCommand = new RelayCommand(AddRule, CanEditSettings);
        RemoveRuleCommand = new RelayCommand(RemoveRule, () => CanEditSettings() && SelectedRule is not null);
    }

    public event EventHandler<CompressionTaskReadyEventArgs>? CompressionTaskReady;
    public event EventHandler<CompressionTaskReadyEventArgs>? RecoveredTaskAvailable;

    public ObservableCollection<VideoTaskItem> Videos { get; } = [];
    public ObservableCollection<CompressionHistoryEntry> History { get; } = [];
    public string RecoveryStatusDisplay { get; private set; } = string.Empty;
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
    public int ScanCacheHits => _scanCacheHits;
    public int ScanActualProbes => _scanActualProbes;
    public double ScanProgressPercent => ScanTotal <= 0 ? 0 : Math.Clamp(ScanCompleted / (double)ScanTotal * 100, 0, 100);
    public string ScanProgressDisplay => ScanTotal <= 0 ? "—" : $"{ScanCompleted} / {ScanTotal}";
    public string ScanProbeSummaryDisplay => $"缓存命中 {_scanCacheHits} · 实际 ffprobe {_scanActualProbes}";
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
    public bool IsBenchmarking
    {
        get => _isBenchmarking;
        private set
        {
            if (SetProperty(ref _isBenchmarking, value))
            {
                OnPropertyChanged(nameof(BenchmarkProgressPercent));
                OnPropertyChanged(nameof(BenchmarkProgressDisplay));
                RunBenchmarkCommand.RaiseCanExecuteChanged();
                CancelBenchmarkCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public int BenchmarkCompleted => _benchmarkCompleted;
    public int BenchmarkTotal => _benchmarkTotal;
    public double BenchmarkProgressPercent => BenchmarkTotal <= 0
        ? 0
        : Math.Clamp(BenchmarkCompleted / (double)BenchmarkTotal * 100, 0, 100);
    public string BenchmarkProgressDisplay => BenchmarkTotal <= 0
        ? "—"
        : $"{BenchmarkCompleted} / {BenchmarkTotal}";
    public string BenchmarkStatusDisplay
    {
        get => _benchmarkStatus;
        private set => SetProperty(ref _benchmarkStatus, value);
    }
    public ObservableCollection<EncoderBenchmarkDisplayRow> BenchmarkRows => _benchmarkRows;
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
    public IReadOnlyList<PerformanceMode> PerformanceModes { get; } = Enum.GetValues<PerformanceMode>();
    public IReadOnlyList<EncoderTuningPreset> EncoderTuningPresets { get; } = EncoderTuningCatalog.Presets;
    public IReadOnlyList<BitDepthPolicy> BitDepthPolicies { get; } = Enum.GetValues<BitDepthPolicy>();
    public IReadOnlyList<TargetSizeUnit> TargetSizeUnits { get; } = Enum.GetValues<TargetSizeUnit>();
    public ObservableCollection<VideoEncoder> VideoEncoders { get; } = [VideoEncoder.Libx264, VideoEncoder.Libx265];
    public IReadOnlyList<VideoCodecKind> OutputVideoCodecs { get; } = Enum.GetValues<VideoCodecKind>();
    public ObservableCollection<EncoderSelectionOption> EncoderModes { get; } = [];
    public IReadOnlyList<SmartCompressionPreset> SmartPresets { get; } = Enum.GetValues<SmartCompressionPreset>();
    public IReadOnlyList<QueueFilter> QueueFilters { get; } = Enum.GetValues<QueueFilter>();
    public IReadOnlyList<AudioMode> AudioModes { get; } = Enum.GetValues<AudioMode>();
    public IReadOnlyList<OutputLocationMode> OutputLocations { get; } = Enum.GetValues<OutputLocationMode>();
    public IReadOnlyList<OutputContainerMode> OutputContainers { get; } = Enum.GetValues<OutputContainerMode>();
    public IReadOnlyList<HealthCheckLevel> HealthCheckLevels { get; } = Enum.GetValues<HealthCheckLevel>();
    public IReadOnlyList<CompletionAction> CompletionActions { get; } = Enum.GetValues<CompletionAction>();
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
    public AsyncRelayCommand RunBenchmarkCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CancelBenchmarkCommand { get; }
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
            await LoadRecoverySessionAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing while startup is checking local settings or FFmpeg is expected.
        }
    }

    public void DiscardRecoveredSession()
    {
        _sessionStore.Delete();
        RecoveryStatusDisplay = string.Empty;
        OnPropertyChanged(nameof(RecoveryStatusDisplay));
        StatusMessage = "已放弃上次未完成的任务记录。";
    }

    private async Task LoadRecoverySessionAsync(CancellationToken cancellationToken)
    {
        if (_capabilityDetectionTask is not null)
        {
            try
            {
                await _capabilityDetectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("session", $"恢复任务时硬件能力检测失败，将按软件能力继续：{exception.Message}");
            }
        }

        var loaded = await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(loaded.Warning))
            {
                RecoveryStatusDisplay = loaded.Warning;
                OnPropertyChanged(nameof(RecoveryStatusDisplay));
                StatusMessage = loaded.Warning;
            }

            if (loaded.Session is null || _tools is null)
            {
                return;
            }

            RecoveryStatusDisplay = $"检测到上次未完成的压缩任务：已完成 {loaded.Session.Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Completed)} / {loaded.Session.Entries.Count}，中断 {loaded.Session.Entries.Count(entry => entry.ExecutionState == CompressionExecutionState.Interrupted)}。";
            OnPropertyChanged(nameof(RecoveryStatusDisplay));
            DiagnosticLog.Write("session", $"SessionRecovered：{loaded.Session.SessionId}");
            RecoveredTaskAvailable?.Invoke(
                this,
                new CompressionTaskReadyEventArgs(loaded.Session, _workflowService, _tools, _encoderCapabilities));
        }, DispatcherPriority.Background, cancellationToken);
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
        var scanPolicy = LongRunningTaskPolicyResolver.Resolve(Settings, _encoderCapabilities);
        Videos.Clear();
        _scanCompleted = 0;
        _scanTotal = 0;
        _scanCacheHits = 0;
        _scanActualProbes = 0;
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
                scanPolicy.ProbeConcurrency,
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
                        if (info.HealthStatus == MediaHealthStatus.Corrupt)
                        {
                            item.IsSelected = false;
                            item.Status = VideoTaskStatus.Failed;
                            item.StatusDetail = $"健康检查失败：{info.HealthCheckMessage ?? "不建议直接压缩。"}";
                            item.ApplyConditionResult(new ConditionEvaluationResult(
                                ConditionResultState.Failed,
                                false,
                                "源文件健康检查失败，已阻止自动压缩。",
                                [item.StatusDetail],
                                item.StatusDetail));
                        }
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

                        var failedSourceBase = VideoFileInfo.FromFile(path);
                        var failedSource = failedSourceBase.WithHealthCheck(new MediaHealthCheckResult(
                            MediaHealthStatus.Corrupt,
                            Settings.HealthCheckLevel == HealthCheckLevel.Disabled ? HealthCheckLevel.Quick : Settings.HealthCheckLevel,
                            $"健康检查无法读取媒体：{message}",
                            DateTimeOffset.UtcNow,
                            MediaFileFingerprint.FromVideoInfo(failedSourceBase)));
                        var item = new VideoTaskItem(failedSource)
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
                        _scanCacheHits = scanProgress.CacheHits;
                        _scanActualProbes = scanProgress.ActualProbes;
                        _currentTaskItem = null;
                        _currentFile = string.IsNullOrWhiteSpace(scanProgress.CurrentPath)
                            ? _currentFile
                            : Path.GetFileName(scanProgress.CurrentPath);
                        StatusMessage = scanProgress.IsComplete
                            ? $"扫描完成：{scanProgress.Total} 个视频。缓存命中 {_scanCacheHits}，实际 ffprobe {_scanActualProbes}。"
                            : $"正在分析：{scanProgress.Completed} / {scanProgress.Total}" +
                              $"（缓存命中 {_scanCacheHits}，实际 ffprobe {_scanActualProbes}）" +
                              (string.IsNullOrWhiteSpace(scanProgress.CurrentPath) ? string.Empty : $" 当前：{Path.GetFileName(scanProgress.CurrentPath)}");
                        RefreshQueueProperties();
                    }, DispatcherPriority.Background, cancellationToken);
                },
                Settings.HealthCheckLevel);
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

    private async Task RunBenchmarkAsync()
    {
        if (IsShuttingDown || IsBenchmarking)
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

        IsBenchmarking = true;
        _benchmarkCompleted = 0;
        _benchmarkTotal = 0;
        OnPropertyChanged(nameof(BenchmarkCompleted));
        OnPropertyChanged(nameof(BenchmarkTotal));
        OnPropertyChanged(nameof(BenchmarkProgressPercent));
        OnPropertyChanged(nameof(BenchmarkProgressDisplay));
        BenchmarkStatusDisplay = "正在准备本机 Benchmark；不会上传视频或读取用户媒体。";
        try
        {
            var progress = new Progress<EncoderBenchmarkProgress>(ApplyBenchmarkProgress);
            var result = await _benchmarkService.RunAsync(
                _tools!,
                _encoderCapabilities,
                _operationCancellation!.Token,
                progress);
            if (result.Cancelled)
            {
                BenchmarkStatusDisplay = result.Message ?? "Benchmark 已取消；旧结果保持不变。";
            }
            else if (result.Snapshot is { } snapshot)
            {
                BenchmarkStatusDisplay = $"本机 Benchmark 已完成：{snapshot.Results.Count} 项，结果仅保存在本机。";
            }
            else
            {
                BenchmarkStatusDisplay = result.Message ?? "当前没有可测试的编码器。";
            }
            RefreshBenchmarkRows();
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusDisplay = "Benchmark 已取消；旧结果保持不变。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("benchmark", $"Benchmark 失败：{exception.Message}");
            BenchmarkStatusDisplay = $"Benchmark 失败：{exception.Message}";
        }
        finally
        {
            IsBenchmarking = false;
            EndOperation();
        }
    }

    private void ApplyBenchmarkProgress(EncoderBenchmarkProgress update)
    {
        void Apply()
        {
            _benchmarkCompleted = update.Completed;
            _benchmarkTotal = update.Total;
            OnPropertyChanged(nameof(BenchmarkCompleted));
            OnPropertyChanged(nameof(BenchmarkTotal));
            OnPropertyChanged(nameof(BenchmarkProgressPercent));
            OnPropertyChanged(nameof(BenchmarkProgressDisplay));
            BenchmarkStatusDisplay = update.IsComplete
                ? "Benchmark 测试完成，正在保存结果…"
                : $"正在测试 {update.CurrentEncoderDisplay} · {update.CurrentWorkloadDisplay}";
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(Apply, DispatcherPriority.DataBind);
        }
        catch (InvalidOperationException)
        {
            // The window is closing; no UI progress is needed.
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
        var executionPolicy = LongRunningTaskPolicyResolver.Resolve(snapshot, _encoderCapabilities);
        var pending = new List<Task>();
        var seenInputs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var generatedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var requiresProbe = _ruleEngine.RequiresProbe(snapshot.Rules) || CompressionPlanner.RequiresSourceProbe(snapshot);
        var started = 0;
        _scanCompleted = 0;
        _scanTotal = 0;
        _currentFile = string.Empty;
        _currentTaskItem = null;
        RefreshQueueProperties();
        using var semaphore = new SemaphoreSlim(executionPolicy.MaxTotalWorkers, executionPolicy.MaxTotalWorkers);
        using var probeSemaphore = new SemaphoreSlim(executionPolicy.ProbeConcurrency, executionPolicy.ProbeConcurrency);
        using var sleepPrevention = CompressionSleepPrevention.Acquire(snapshot.PreventSleepDuringCompression);
        try
        {
            await PersistSettingsAsync();
            StatusMessage = "直接处理模式：不会预扫描整个目录，文件会逐个判断。";
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
                pending.Add(ProcessItemWithGateAsync(
                    item,
                    snapshot,
                    semaphore,
                    cancellationToken,
                    generatedPaths,
                    probeSemaphore,
                    requiresProbe));
                if (pending.Count >= executionPolicy.MaxTotalWorkers * 4)
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
                : $"直接处理完成：已检查 {started} 个视频。缓存命中 {_scanCacheHits}，实际 ffprobe {_scanActualProbes}。";
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
                cancellationToken,
                GetTrustedBenchmarkSnapshot());
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

            var sessionPersistenceWarning = false;
            try
            {
                // Persist the preview snapshot as soon as the plan exists. A
                // crash before the user presses Start must not discard a
                // carefully reviewed batch plan.
                await _sessionStore.SaveAsync(session, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sessionPersistenceWarning = true;
                DiagnosticLog.Write("session", $"计划已生成，但无法保存恢复记录：{exception.Message}");
            }

            StatusMessage = sessionPersistenceWarning
                ? $"已生成压缩计划：{session.Entries.Count} 个视频，但恢复记录保存失败。"
                : $"已生成压缩计划：{session.Entries.Count} 个视频。请在“压缩任务”页面确认后开始。";
            CompressionTaskReady?.Invoke(this, new CompressionTaskReadyEventArgs(session, _workflowService, _tools!, _encoderCapabilities));
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
        ConcurrentDictionary<string, byte>? generatedPaths = null,
        SemaphoreSlim? probeSemaphore = null,
        bool requiresProbe = false)
    {
        var acquiredSemaphore = false;
        var acquiredProbeSemaphore = false;
        try
        {
            item.Status = VideoTaskStatus.Queued;
            item.StatusDetail = "排队中";
            _currentTaskItem = item;
            _currentFile = item.FileName;
            RefreshQueueProperties();

            var source = item.Media;
            if (probeSemaphore is not null && (requiresProbe || !source.HasProbeData || source.HealthStatus == MediaHealthStatus.NotChecked))
            {
                await probeSemaphore.WaitAsync(cancellationToken);
                acquiredProbeSemaphore = true;
                item.Status = VideoTaskStatus.Analyzing;
                item.StatusDetail = "正在读取媒体信息…";
                RefreshQueueProperties();
                try
                {
                    var probeLookup = await _videoScannerService.ProbeCache.GetOrProbeAsync(
                        _tools!,
                        source.FullPath,
                        _directProbeService,
                        cancellationToken);
                    source = probeLookup.Info;
                    if (probeLookup.CacheHit)
                    {
                        Interlocked.Increment(ref _scanCacheHits);
                    }
                    else
                    {
                        Interlocked.Increment(ref _scanActualProbes);
                    }
                    item.UpdateMedia(source);
                }
                finally
                {
                    probeSemaphore.Release();
                    acquiredProbeSemaphore = false;
                }
            }

            await semaphore.WaitAsync(cancellationToken);
            acquiredSemaphore = true;
            var progress = new Progress<WorkflowProgress>(update => ApplyWorkflowProgress(item, update));
            var result = await _workflowService.ProcessFileAsync(
                source,
                snapshot,
                _tools!,
                DirectoryPath,
                progress,
                cancellationToken,
                _encoderCapabilities,
                GetTrustedBenchmarkSnapshot());
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
            if (acquiredProbeSemaphore && probeSemaphore is not null)
            {
                probeSemaphore.Release();
            }
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
                RefreshBenchmarkRows();
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
                RefreshBenchmarkRows();
            }, DispatcherPriority.Background, CancellationToken.None);
        }
    }

    private EncoderBenchmarkSnapshot? GetTrustedBenchmarkSnapshot()
    {
        try
        {
            return _benchmarkCache.GetCurrent(_encoderCapabilities);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write("benchmark", $"读取本机 Benchmark 缓存失败：{exception.Message}");
            return null;
        }
    }

    private void RefreshBenchmarkRows()
    {
        EncoderBenchmarkSnapshot? snapshot = null;
        try
        {
            snapshot = GetTrustedBenchmarkSnapshot();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("benchmark", $"刷新 Benchmark 摘要失败：{exception.Message}");
        }

        _benchmarkRows.Clear();
        foreach (var definition in EncoderCatalog.Definitions.Where(definition =>
                     definition.Codec is VideoCodecKind.H264 or VideoCodecKind.H265))
        {
            var capability = _encoderCapabilities.Get(definition.Encoder);
            var results = snapshot?.Results
                .Where(result => result.Encoder == definition.Encoder ||
                                 string.Equals(result.EncoderId, definition.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(result => result.WorkloadId, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            var status = capability is null
                ? "尚未检测"
                : capability.IsUsable
                    ? results.Length == 0
                        ? "可用 · 尚未测试"
                        : string.Join("；", results.Select(result => $"{result.WorkloadDisplay} {result.SpeedDisplay}"))
                    : CompactCapabilityReason(capability.UnavailableReason, "未通过能力检测");
            _benchmarkRows.Add(new EncoderBenchmarkDisplayRow(
                definition.Id,
                definition.DisplayName,
                capability?.IsUsable == true,
                status,
                results));
        }

        var warning = _benchmarkCache.LastLoadWarning;
        BenchmarkStatusDisplay = snapshot is not null
            ? snapshot.IsFresh
                ? $"已有本机 Benchmark：{snapshot.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : $"已有本机 Benchmark：{snapshot.CompletedAt.ToLocalTime():yyyy-MM-dd HH:mm}（数据可能已过期，Auto 将降低置信度）"
            : warning ?? "尚未进行本机性能测试。";
        OnPropertyChanged(nameof(BenchmarkRows));
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
            capability?.IsUsable == true &&
            capability.SupportsBitDepth(Settings.BitDepthPolicy == BitDepthPolicy.TenBit ? 10 : 8),
            capability is null
                ? defaultReason
                : CompactCapabilityReason(capability.UnavailableReason, defaultReason));
    }

    private bool HasAnyUsableHardware(VideoCodecKind codec) =>
        _encoderCapabilities.Capabilities.Any(capability =>
            capability.IsHardware &&
            capability.Codec == codec &&
            capability.IsUsable &&
            capability.SupportsBitDepth(Settings.BitDepthPolicy == BitDepthPolicy.TenBit ? 10 : 8));

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
        OnPropertyChanged(nameof(ScanCacheHits));
        OnPropertyChanged(nameof(ScanActualProbes));
        OnPropertyChanged(nameof(ScanProgressPercent));
        OnPropertyChanged(nameof(ScanProgressDisplay));
        OnPropertyChanged(nameof(ScanProbeSummaryDisplay));
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
        if (e.PropertyName == nameof(AppSettings.EncoderTuningPreset))
        {
            // Keep the legacy serialized field aligned when the new UI
            // changes tuning. This prevents a previously loaded 1.2.0 raw
            // preset (for example, "slow") from overriding an intentional
            // return to the new Balanced choice.
            Settings.EncodingPreset = EncoderTuningCatalog.Resolve(
                Settings.VideoEncoder,
                Settings.EncoderTuningPreset);
        }

        if (e.PropertyName is nameof(AppSettings.VideoEncoder) or
            nameof(AppSettings.TargetVideoCodec) or
            nameof(AppSettings.EncoderSelection) or
            nameof(AppSettings.BitDepthPolicy) or
            nameof(AppSettings.EncoderTuningPreset))
        {
            OnPropertyChanged(nameof(Settings.SelectedVideoCodec));
            OnPropertyChanged(nameof(Settings.SelectedEncoderSelection));
            UpdateEncoderModes();
            RefreshBenchmarkRows();
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

        try
        {
            await WaitForCompletionAsync(FlushCachesAsync(), TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Cache persistence is best-effort during shutdown and must not
            // reintroduce a closing deadlock.
        }
    }

    private async Task FlushCachesAsync()
    {
        var flushes = new List<Task>
        {
            _videoScannerService.ProbeCache.FlushAsync()
        };
        if (_compressionTaskPlanner.ResultCache is { } resultCache)
        {
            flushes.Add(resultCache.FlushAsync());
        }
        flushes.Add(_benchmarkCache.FlushAsync());

        await Task.WhenAll(flushes).ConfigureAwait(false);
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

    private bool CanRunBenchmark() => CanEditSettings() && !IsBenchmarking;

    private bool CanStartCompression() => CanEditSettings() && HasSelectedVideo;

    private static bool IsProcessable(VideoTaskItem item) =>
        item.Media.HealthStatus != MediaHealthStatus.Corrupt &&
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
        RunBenchmarkCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        CancelBenchmarkCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        DeselectAllCommand.RaiseCanExecuteChanged();
        AddRuleCommand.RaiseCanExecuteChanged();
        RemoveRuleCommand.RaiseCanExecuteChanged();
    }

    private static CompressionWorkflowService CreateWorkflowService()
    {
        return new CompressionWorkflowService(
            new RuleEngine(),
            DefaultProbeService,
            DefaultFfmpegService,
            new CompressionPlanner(resultCache: DefaultResultCache),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new SafeFileService(DefaultProbeService),
            probeCache: DefaultProbeCache,
            healthCheckService: DefaultHealthCheckService);
    }

    private static CompressionTaskPlanner CreateCompressionTaskPlanner()
    {
        return new CompressionTaskPlanner(
            new RuleEngine(),
            DefaultProbeService,
            new CompressionPlanner(resultCache: DefaultResultCache),
            new TargetSizeCalculator(),
            new OutputPathService(),
            new VmafQualityCalibrationService(),
            DefaultProbeCache,
            DefaultResultCache);
    }

    private static VideoScannerService CreateVideoScannerService()
    {
        return new VideoScannerService(
            DefaultProbeService,
            DefaultProbeCache,
            DefaultHealthCheckService);
    }
}
