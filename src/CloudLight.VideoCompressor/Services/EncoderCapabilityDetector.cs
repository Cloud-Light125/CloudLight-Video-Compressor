using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class EncoderCapabilityDetector
{
    private readonly FFmpegLocator _ffmpegLocator;
    private readonly HardwareEncoderProbe _hardwareEncoderProbe;
    private readonly EncoderHelpProbe _encoderHelpProbe;

    public EncoderCapabilityDetector(
        FFmpegLocator? ffmpegLocator = null,
        HardwareEncoderProbe? hardwareEncoderProbe = null,
        EncoderHelpProbe? encoderHelpProbe = null)
    {
        _ffmpegLocator = ffmpegLocator ?? new FFmpegLocator();
        _hardwareEncoderProbe = hardwareEncoderProbe ?? new HardwareEncoderProbe();
        _encoderHelpProbe = encoderHelpProbe ?? new EncoderHelpProbe();
    }

    public async Task<EncoderCapabilitySet> DetectAsync(
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var listed = await _ffmpegLocator.GetCapabilitiesAsync(tools, cancellationToken).ConfigureAwait(false);
        var capabilities = new List<EncoderCapability>();
        var probeTime = DateTimeOffset.UtcNow;
        foreach (var definition in EncoderCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = listed.Encoders.Contains(definition.Encoder);
            var strategy = EncoderStrategyCatalog.Get(definition.Encoder);
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
                    $"当前 FFmpeg build 不包含 {definition.Id}")
                {
                    InitializationTestPassed = false,
                    LastProbeTime = probeTime,
                    SupportedRateControls = strategy.SupportedRateControls,
                    SupportedPresets = strategy.SupportedPresets,
                    SupportedPixelFormats = strategy.SupportedPixelFormats
                });
                DiagnosticLog.Write("encoder-detect", $"{definition.Id}: unavailable: FFmpeg encoder not present");
                continue;
            }

            DiagnosticLog.Write("encoder-detect", $"{definition.Id}: present");
            EncoderHelpProbeResult help;
            try
            {
                help = await _encoderHelpProbe.ProbeAsync(tools, definition.Encoder, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                help = new EncoderHelpProbeResult(false, [], strategy.SupportedPresets, [], [], exception.Message);
            }

            var supportedFormats = help.SupportedPixelFormats.Count > 0
                ? help.SupportedPixelFormats
                : strategy.SupportedPixelFormats;
            var supportedBitDepths = help.SupportedBitDepths.Count > 0
                ? help.SupportedBitDepths
                : GetSupportedBitDepths(supportedFormats);
            var supportedProfiles = help.SupportedProfiles.Count > 0
                ? help.SupportedProfiles
                : DefaultProfiles(definition, supportedBitDepths);
            var supportedPresets = help.SupportedPresets.Count > 0
                ? help.SupportedPresets
                : strategy.SupportedPresets;
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
                    null)
                {
                    InitializationTestPassed = true,
                    LastProbeTime = probeTime,
                    SupportedRateControls = strategy.SupportedRateControls,
                    SupportedPresets = supportedPresets,
                    SupportedPixelFormats = supportedFormats,
                    SupportedBitDepths = supportedBitDepths,
                    SupportedProfiles = supportedProfiles,
                    HelpProbePassed = help.Succeeded,
                    FFmpegVersion = listed.Version,
                    CapabilityFingerprint = string.Join(",", supportedFormats)
                });
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
            if (smoke.IsUsable && supportedBitDepths.Contains(10))
            {
                try
                {
                    var tenBit = await _hardwareEncoderProbe.ProbeAsync(tools, definition.Encoder, cancellationToken, 10).ConfigureAwait(false);
                    if (!tenBit.IsUsable)
                    {
                        supportedBitDepths = supportedBitDepths.Where(depth => depth != 10).ToArray();
                        DiagnosticLog.Write("encoder-detect", $"{definition.Id} 10-bit smoke test failed: {tenBit.Error}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    supportedBitDepths = supportedBitDepths.Where(depth => depth != 10).ToArray();
                    DiagnosticLog.Write("encoder-detect", $"{definition.Id} 10-bit smoke test failed: {exception.Message}");
                }
            }
            if (smoke.IsUsable && !supportedBitDepths.Contains(8))
            {
                // The ordinary smoke command is an 8-bit input/output probe.
                // Keep that direct evidence even when a vendor help response
                // lists only its high-bit-depth formats.
                supportedBitDepths = supportedBitDepths.Append(8).Order().ToArray();
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
                smoke.IsUsable ? null : smoke.Error ?? "硬件设备初始化失败")
            {
                InitializationTestPassed = smoke.IsUsable,
                LastProbeTime = probeTime,
                SupportedRateControls = strategy.SupportedRateControls,
                SupportedPresets = supportedPresets,
                SupportedPixelFormats = supportedFormats,
                SupportedBitDepths = supportedBitDepths,
                SupportedProfiles = supportedProfiles,
                HelpProbePassed = help.Succeeded,
                FFmpegVersion = listed.Version,
                CapabilityFingerprint = string.Join(",", supportedFormats)
            };
            capabilities.Add(capability);
            DiagnosticLog.Write(
                "encoder-detect",
                $"{definition.Id} smoke test: {(smoke.IsUsable ? "success" : $"unavailable: {capability.UnavailableReason}")}");
        }

        return new EncoderCapabilitySet(capabilities, listed.Version);
    }

    private static IReadOnlyList<int> GetSupportedBitDepths(IReadOnlyList<string> formats) =>
        formats.Select(BitDepthPolicyResolver.DetectPixelFormatBitDepth)
            .Where(depth => depth is > 0)
            .Select(depth => depth!.Value >= 10 ? 10 : 8)
            .Distinct()
            .Order()
            .ToArray();

    private static IReadOnlyList<string> DefaultProfiles(
        EncoderDefinition definition,
        IReadOnlyList<int> supportedBitDepths) =>
        definition.Codec == VideoCodecKind.H265
            ? supportedBitDepths.Contains(10) ? ["main", "main10"] : ["main"]
            : definition.Codec == VideoCodecKind.H264
                ? supportedBitDepths.Contains(10) ? ["high", "high10"] : ["high"]
                : [];
}
