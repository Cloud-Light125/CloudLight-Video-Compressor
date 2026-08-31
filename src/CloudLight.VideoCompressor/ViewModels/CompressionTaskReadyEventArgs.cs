using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.ViewModels;

public sealed class CompressionTaskReadyEventArgs : EventArgs
{
    public CompressionTaskReadyEventArgs(
        CompressionTaskSession session,
        CompressionWorkflowService workflowService,
        FFmpegTools tools,
        EncoderCapabilitySet? capabilities = null)
    {
        Session = session;
        WorkflowService = workflowService;
        Tools = tools;
        Capabilities = capabilities;
    }

    public CompressionTaskSession Session { get; }
    public CompressionWorkflowService WorkflowService { get; }
    public FFmpegTools Tools { get; }
    public EncoderCapabilitySet? Capabilities { get; }
}
