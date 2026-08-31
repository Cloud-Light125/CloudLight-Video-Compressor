using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CloudLight.VideoCompressor.ViewModels;

namespace CloudLight.VideoCompressor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(12);
    private readonly MainViewModel _viewModel;
    private CompressionTaskWindow? _compressionTaskWindow;
    private bool _shutdownStarted;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        _viewModel.CompressionTaskReady += OnCompressionTaskReady;
        _viewModel.RecoveredTaskAvailable += OnRecoveredTaskAvailable;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (OperationCanceledException) when (_viewModel.IsShuttingDown)
        {
            // The user closed the window while startup was still loading local settings or FFmpeg capabilities.
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, $"初始化设置失败：{exception.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _ = ShutdownThenCloseAsync();
    }

    private async Task ShutdownThenCloseAsync()
    {
        try
        {
            await _viewModel.ShutdownAsync(ShutdownTimeout);
        }
        catch
        {
            // Shutdown failures must never trap the user in the application.
        }
        finally
        {
            _allowClose = true;
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
            }
        }
    }

    private void OnCompressionTaskReady(object? sender, CompressionTaskReadyEventArgs e)
        => OpenCompressionTask(e, startImmediately: false);

    private void OpenCompressionTask(CompressionTaskReadyEventArgs e, bool startImmediately)
    {
        if (_compressionTaskWindow is not null)
        {
            return;
        }

        _compressionTaskWindow = new CompressionTaskWindow(e.Session, e.WorkflowService, e.Tools, e.Capabilities, startImmediately)
        {
            Owner = this
        };
        try
        {
            _compressionTaskWindow.ShowDialog();
        }
        finally
        {
            _viewModel.ApplyTaskSessionResults(e.Session);
            _ = _viewModel.RefreshHistoryAsync();
            _compressionTaskWindow = null;
        }
    }

    private void OnRecoveredTaskAvailable(object? sender, CompressionTaskReadyEventArgs e)
    {
        if (_compressionTaskWindow is not null)
        {
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            this,
            "检测到上次未完成的压缩任务。\n\n选择“是”继续任务，选择“否”查看任务，选择“取消”放弃记录。\n只有选择“是”后才会继续启动 FFmpeg。",
            Title,
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Information);
        if (choice == System.Windows.MessageBoxResult.Cancel)
        {
            _viewModel.DiscardRecoveredSession();
            return;
        }

        OpenCompressionTask(e, choice == System.Windows.MessageBoxResult.Yes);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CompressionTaskReady -= OnCompressionTaskReady;
        _viewModel.RecoveredTaskAvailable -= OnRecoveredTaskAvailable;
        _viewModel.Dispose();
    }
}
