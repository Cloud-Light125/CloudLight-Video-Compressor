using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class EncoderCapabilityDetector
{
    private readonly FFmpegLocator _ffmpegLocator;
    private readonly HardwareEncoderProbe _hardwareEncoderProbe;

    public EncoderCapabilityDetector(
        FFmpegLocator? ffmpegLocator = null,
        HardwareEncoderProbe? hardwareEncoderProbe = null)
    {
        _ffmpegLocator = ffmpegLocator ?? new FFmpegLocator();
        _hardwareEncoderProbe = hardwareEncoderProbe ?? new HardwareEncoderProbe();
    }

    public async Task<EncoderCapabilitySet> DetectAsync(
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var listed = await _ffmpegLocator.GetCapabilitiesAsync(tools, cancellationToken).ConfigureAwait(false);
        var capabilities = new List<EncoderCapability>();
        foreach (var definition in EncoderCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = listed.Encoders.Contains(definition.Encoder);
            if (!present)
            {
                capabilities.Add(new EncoderCapability(
                    definition.Id,
                    definition.DisplayName,
                    definition.Encoder,
                    definition.Codec,
                    definition.Vendor,
                    definition.IsHardware,
                    false,
                    false,
                    $"当前 FFmpeg build 不包含 {definition.Id}"));
                DiagnosticLog.Write("encoder-detect", $"{definition.Id}: unavailable: FFmpeg encoder not present");
                continue;
            }

            DiagnosticLog.Write("encoder-detect", $"{definition.Id}: present");
            if (!definition.IsHardware)
            {
                capabilities.Add(new EncoderCapability(
                    definition.Id,
                    definition.DisplayName,
                    definition.Encoder,
                    definition.Codec,
                    definition.Vendor,
                    false,
                    true,
                    true,
                    null));
                continue;
            }

            HardwareEncoderProbeResult smoke;
            try
            {
                smoke = await _hardwareEncoderProbe.ProbeAsync(tools, definition.Encoder, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One broken vendor runtime must not hide a different usable
                // hardware encoder from the settings page.
                smoke = new HardwareEncoderProbeResult(false, exception.Message);
            }
            var capability = new EncoderCapability(
                definition.Id,
                definition.DisplayName,
                definition.Encoder,
                definition.Codec,
                definition.Vendor,
                true,
                true,
                smoke.IsUsable,
                smoke.IsUsable ? null : smoke.Error ?? "硬件设备初始化失败");
            capabilities.Add(capability);
            DiagnosticLog.Write(
                "encoder-detect",
                $"{definition.Id} smoke test: {(smoke.IsUsable ? "success" : $"unavailable: {capability.UnavailableReason}")}");
        }

        return new EncoderCapabilitySet(capabilities);
    }
}
