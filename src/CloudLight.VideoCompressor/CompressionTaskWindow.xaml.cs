using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;
using CloudLight.VideoCompressor.ViewModels;

namespace CloudLight.VideoCompressor;

public partial class CompressionTaskWindow : Window
{
    private readonly CompressionTaskViewModel _viewModel;
    private bool _startImmediately;
    private bool _allowClose;
    private bool _closeRequested;

    public CompressionTaskWindow(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools,
        EncoderCapabilitySet? capabilities = null,
        bool startImmediately = false)
    {
        InitializeComponent();
        _startImmediately = startImmediately;
        _viewModel = new CompressionTaskViewModel(
            session,
            workflowService,
            tools,
            Dispatcher,
            capabilities: capabilities);
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public CompressionTaskViewModel ViewModel => _viewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_startImmediately)
        {
            _startImmediately = false;
            _viewModel.StartCompressionCommand.Execute(null);
        }
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_viewModel.SettingsSnapshot.CompletionAction == CompletionAction.CloseApplication)
        {
            // The completion action is explicitly “close the application”,
            // not merely close this modal task window. MainWindow's normal
            // bounded shutdown path will still flush settings and caches.
            System.Windows.Application.Current?.Shutdown();
            return;
        }

        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        _ = PrepareAndCloseAfterCancellationAsync();
    }

    private async Task PrepareAndCloseAfterCancellationAsync()
    {
        try
        {
            await _viewModel.PrepareForShutdownRecoveryAsync();
            _viewModel.StopCommand.Execute(null);
            await _viewModel.WaitForCompletionAsync();
        }
        catch
        {
            // Individual entries convert expected failures to visible task states.
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.Dispose();
    }
}
