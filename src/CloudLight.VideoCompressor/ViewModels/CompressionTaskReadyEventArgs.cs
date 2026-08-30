using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.ViewModels;

public sealed class CompressionTaskReadyEventArgs : EventArgs
{
    public CompressionTaskReadyEventArgs(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools)
    {
        Session = session;
        WorkflowService = workflowService;
        Tools = tools;
    }

    public CompressionTaskSession Session { get; }
    public CompressionWorkflowService WorkflowService { get; }
    public FFmpegTools Tools { get; }
}
