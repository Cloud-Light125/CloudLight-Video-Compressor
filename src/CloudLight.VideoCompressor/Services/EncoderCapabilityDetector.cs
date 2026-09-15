using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class EncoderCapabilityDetector
{
    private readonly FFmpegLocator _ffmpegLocator;
    private readonly HardwareEncoderProbe _hardwareEncoderProbe;
    private readonly EncoderHelpProbe _encoderHelpProbe;
    private readonly EncoderCapabilityCache _cache;
    private readonly Func<CancellationToken, Task<HardwareEnvironmentIdentity>> _hardwareIdentityProvider;
    private readonly Func<FFmpegTools, CancellationToken, Task<FFmpegCapabilities>> _capabilitiesProvider;
    private readonly Func<FFmpegTools, VideoEncoder, CancellationToken, Task<EncoderHelpProbeResult>> _helpProvider;
    private readonly Func<FFmpegTools, VideoEncoder, CancellationToken, int, Task<HardwareEncoderProbeResult>> _smokeProvider;

    public EncoderCapabilityDetector(
        FFmpegLocator? ffmpegLocator = null,
        HardwareEncoderProbe? hardwareEncoderProbe = null,
        EncoderHelpProbe? encoderHelpProbe = null,
        EncoderCapabilityCache? cache = null,
        HardwareEnvironmentProbe? hardwareEnvironmentProbe = null,
        Func<FFmpegTools, CancellationToken, Task<FFmpegCapabilities>>? capabilitiesProvider = null,
        Func<FFmpegTools, VideoEncoder, CancellationToken, Task<EncoderHelpProbeResult>>? helpProvider = null,
        Func<FFmpegTools, VideoEncoder, CancellationToken, int, Task<HardwareEncoderProbeResult>>? smokeProvider = null,
        Func<CancellationToken, Task<HardwareEnvironmentIdentity>>? hardwareIdentityProvider = null)
    {
        _ffmpegLocator = ffmpegLocator ?? new FFmpegLocator();
        _hardwareEncoderProbe = hardwareEncoderProbe ?? new HardwareEncoderProbe();
        _encoderHelpProbe = encoderHelpProbe ?? new EncoderHelpProbe();
        _cache = cache ?? new EncoderCapabilityCache();
        var identityProbe = hardwareEnvironmentProbe ?? new HardwareEnvironmentProbe();
        _hardwareIdentityProvider = hardwareIdentityProvider ?? identityProbe.ProbeAsync;
        _capabilitiesProvider = capabilitiesProvider ?? ((tools, token) => _ffmpegLocator.GetCapabilitiesAsync(tools, token));
        _helpProvider = helpProvider ?? ((tools, encoder, token) => _encoderHelpProbe.ProbeAsync(tools, encoder, token));
        _smokeProvider = smokeProvider ?? ((tools, encoder, token, bitDepth) =>
            _hardwareEncoderProbe.ProbeAsync(tools, encoder, token, bitDepth));
    }

    public async Task<EncoderCapabilitySet> DetectAsync(
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = Path.GetFullPath(tools.FFmpegPath);
        DiagnosticLog.Write("encoder-detect", $"detection started; FFmpeg path: {ffmpegPath}");
        var listed = await _capabilitiesProvider(tools, cancellationToken).ConfigureAwait(false);
        var hardwareIdentity = await _hardwareIdentityProvider(cancellationToken).ConfigureAwait(false);
        var listedEncoderIds = listed.EncoderIds ?? listed.Encoders
            .Select(encoder => CompressionPlan.FfmpegEncoderName(encoder))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DiagnosticLog.Write(
            "encoder-detect",
            $"encoders result: exit code {listed.ExitCode}; detected: {string.Join(", ", listedEncoderIds.Order(StringComparer.OrdinalIgnoreCase))}");
        if (!string.IsNullOrWhiteSpace(listed.StandardError))
        {
            DiagnosticLog.Write("encoder-detect", $"encoders stderr:{Environment.NewLine}{listed.StandardError.Trim()}");
        }

        var identity = new EncoderCapabilityCacheIdentity(
            hardwareIdentity.GpuName,
            hardwareIdentity.NvidiaDriverVersion,
            listed.Version ?? "(unknown)",
            ffmpegPath);
        if (_cache.TryGet(identity, listedEncoderIds, out var cached, out var cacheMissReason))
        {
            DiagnosticLog.Write("encoder-capability-cache", $"CacheHit: {_cache.CachePath}; timestamp: {cached.DetectedAt:O}");
            return cached;
        }
        DiagnosticLog.Write("encoder-capability-cache", $"CacheMiss: {_cache.CachePath}; reason: {cacheMissReason}");

        var capabilities = new List<EncoderCapability>();
        var probeTime = DateTimeOffset.UtcNow;
        foreach (var definition in EncoderCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = listed.Encoders.Contains(definition.Encoder) || listedEncoderIds.Contains(definition.Id);
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
                help = await _helpProvider(tools, definition.Encoder, cancellationToken).ConfigureAwait(false);
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
                smoke = await _smokeProvider(tools, definition.Encoder, cancellationToken, 8).ConfigureAwait(false);
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
            LogSmokeResult(definition.Id, smoke);
            if (smoke.IsUsable && supportedBitDepths.Contains(10))
            {
                try
                {
                    var tenBit = await _smokeProvider(tools, definition.Encoder, cancellationToken, 10).ConfigureAwait(false);
                    LogSmokeResult($"{definition.Id} 10-bit", tenBit);
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

        var result = new EncoderCapabilitySet(
            capabilities,
            listed.Version,
            gpuName: hardwareIdentity.GpuName,
            nvidiaDriverVersion: hardwareIdentity.NvidiaDriverVersion,
            ffmpegPath: ffmpegPath,
            detectedAt: probeTime);
        try
        {
            await _cache.SaveAsync(identity, listedEncoderIds, result, cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Write("encoder-capability-cache", $"saved: {_cache.CachePath}; timestamp: {result.DetectedAt:O}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            DiagnosticLog.Write("encoder-capability-cache", $"save failed; detection result remains valid: {exception.Message}");
        }
        return result;
    }

    private static void LogSmokeResult(string encoderId, HardwareEncoderProbeResult smoke)
    {
        if (!string.IsNullOrWhiteSpace(smoke.Command))
        {
            DiagnosticLog.Write("encoder-detect", $"{encoderId} smoke command: {smoke.Command}");
        }
        DiagnosticLog.Write("encoder-detect", $"{encoderId} smoke exit code: {smoke.ExitCode?.ToString() ?? "(not started)"}");
        DiagnosticLog.Write(
            "encoder-detect",
            $"{encoderId} smoke stderr:{Environment.NewLine}{(string.IsNullOrWhiteSpace(smoke.StandardError) ? "(empty)" : smoke.StandardError.Trim())}");
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
