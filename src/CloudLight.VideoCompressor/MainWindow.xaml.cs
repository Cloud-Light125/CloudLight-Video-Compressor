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
    {
        if (_compressionTaskWindow is not null)
        {
            return;
        }

        _compressionTaskWindow = new CompressionTaskWindow(e.Session, e.WorkflowService, e.Tools)
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CompressionTaskReady -= OnCompressionTaskReady;
        _viewModel.Dispose();
    }
}
