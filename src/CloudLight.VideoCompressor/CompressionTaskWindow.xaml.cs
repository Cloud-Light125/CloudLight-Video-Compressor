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
    private bool _allowClose;
    private bool _closeRequested;

    public CompressionTaskWindow(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools)
    {
        InitializeComponent();
        _viewModel = new CompressionTaskViewModel(
            session,
            workflowService,
            tools,
            Dispatcher);
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public CompressionTaskViewModel ViewModel => _viewModel;

    private void OnRequestClose(object? sender, EventArgs e)
    {
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
        _viewModel.StopCommand.Execute(null);
        _ = CloseAfterCancellationAsync();
    }

    private async Task CloseAfterCancellationAsync()
    {
        try
        {
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
