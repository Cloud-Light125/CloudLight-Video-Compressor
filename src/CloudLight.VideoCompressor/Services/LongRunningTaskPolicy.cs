using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// The single resource policy shared by scanning, queue execution, FFmpeg
/// progress reporting and the task page. It intentionally contains no video
/// quality settings: performance mode only changes resource contention and
/// observability.
/// </summary>
public sealed record LongRunningTaskPolicy(
    PerformanceMode Mode,
    int MaxCpuWorkers,
    int MaxHardwareWorkers,
    int MaxTotalWorkers,
    int ProbeConcurrency,
    TimeSpan UiProgressInterval,
    TimeSpan EtaSampleWindow,
    int EtaMinimumSamples,
    TimeSpan EtaMinimumRuntime,
    TimeSpan StartupGracePeriod,
    TimeSpan StallThreshold,
    ProcessPriorityMode ProcessPriority,
    SoftwareThreadPolicy SoftwareThreadPolicy,
    int? SoftwareThreadCount)
{
    public bool IsConservative => Mode == PerformanceMode.LowEndStable;

    public EncoderProgressWatchdogOptions WatchdogOptions => new(
        StartupGracePeriod,
        StallThreshold,
        EtaMinimumSamples);

    public EtaCalculatorOptions EtaOptions => new(
        EtaMinimumSamples,
        EtaSampleWindow,
        EtaMinimumRuntime);

    public bool ShouldLimitSoftwareThreads(VideoEncoder encoder) =>
        SoftwareThreadPolicy == SoftwareThreadPolicy.ReserveSystemCores &&
        !EncoderCatalog.Get(encoder).IsHardware &&
        SoftwareThreadCount is > 0;

    public static LongRunningTaskPolicy ForSettings(
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null,
        IEnumerable<VideoEncoder>? plannedEncoders = null,
        int? logicalProcessorCount = null,
        long? availableMemoryBytes = null) =>
        LongRunningTaskPolicyResolver.Resolve(
            settings,
            capabilities,
            plannedEncoders,
            logicalProcessorCount,
            availableMemoryBytes);
}

public sealed record EtaCalculatorOptions(
    int MinimumSamples,
    TimeSpan SampleWindow,
    TimeSpan MinimumRuntime,
    bool RequireValidSpeedSamples = true);

/// <summary>
/// Resolves a user setting into safe limits. Automatic mode is deliberately
/// conservative only when the machine signals constrained CPU/memory or when
/// a QSV-only hardware setup is also small; the presence of an Intel encoder
/// by itself is not treated as proof of weak hardware.
/// </summary>
public static class LongRunningTaskPolicyResolver
{
    private const long EightGiB = 8L * 1024 * 1024 * 1024;
    private const long SixteenGiB = 16L * 1024 * 1024 * 1024;

    public static LongRunningTaskPolicy Resolve(
        AppSettings settings,
        EncoderCapabilitySet? capabilities = null,
        IEnumerable<VideoEncoder>? plannedEncoders = null,
        int? logicalProcessorCount = null,
        long? availableMemoryBytes = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logicalProcessors = Math.Max(1, logicalProcessorCount ?? Environment.ProcessorCount);
        var memoryBytes = availableMemoryBytes ?? GetAvailableMemoryBytes();
        var encoders = plannedEncoders?.ToArray() ?? Array.Empty<VideoEncoder>();
        var mode = settings.PerformanceMode;
        if (mode == PerformanceMode.Automatic)
        {
            mode = IsConstrainedMachine(logicalProcessors, memoryBytes, capabilities, encoders)
                ? PerformanceMode.LowEndStable
                : PerformanceMode.Balanced;
        }

        var requestedWorkers = Math.Clamp(settings.CompressionConcurrency, 1, 4);
        var requestedProbeConcurrency = Math.Clamp(settings.ProbeConcurrency, 1, 8);
        return mode switch
        {
            PerformanceMode.LowEndStable => Build(
                mode,
                maxCpuWorkers: 1,
                maxHardwareWorkers: 1,
                maxTotalWorkers: 1,
                probeConcurrency: 1,
                uiProgressInterval: TimeSpan.FromMilliseconds(450),
                etaSampleWindow: TimeSpan.FromSeconds(60),
                etaMinimumRuntime: TimeSpan.FromSeconds(15),
                startupGracePeriod: TimeSpan.FromSeconds(120),
                stallThreshold: TimeSpan.FromSeconds(90),
                processPriority: ProcessPriorityMode.BelowNormal,
                softwareThreadPolicy: SoftwareThreadPolicy.ReserveSystemCores,
                logicalProcessors),
            PerformanceMode.SpeedPriority => Build(
                mode,
                maxCpuWorkers: Math.Min(3, Math.Max(1, logicalProcessors / 4)),
                maxHardwareWorkers: 2,
                maxTotalWorkers: requestedWorkers,
                probeConcurrency: Math.Min(4, requestedProbeConcurrency),
                uiProgressInterval: TimeSpan.FromMilliseconds(200),
                etaSampleWindow: TimeSpan.FromSeconds(20),
                etaMinimumRuntime: TimeSpan.FromSeconds(10),
                startupGracePeriod: TimeSpan.FromSeconds(75),
                stallThreshold: TimeSpan.FromSeconds(60),
                processPriority: ProcessPriorityMode.Normal,
                softwareThreadPolicy: SoftwareThreadPolicy.EncoderDefault,
                logicalProcessors),
            _ => Build(
                PerformanceMode.Balanced,
                maxCpuWorkers: 1,
                maxHardwareWorkers: 2,
                maxTotalWorkers: Math.Min(3, requestedWorkers),
                probeConcurrency: Math.Min(2, requestedProbeConcurrency),
                uiProgressInterval: TimeSpan.FromMilliseconds(250),
                etaSampleWindow: TimeSpan.FromSeconds(30),
                etaMinimumRuntime: TimeSpan.FromSeconds(10),
                startupGracePeriod: TimeSpan.FromSeconds(75),
                stallThreshold: TimeSpan.FromSeconds(45),
                processPriority: ProcessPriorityMode.Normal,
                softwareThreadPolicy: SoftwareThreadPolicy.EncoderDefault,
                logicalProcessors)
        };
    }

    private static LongRunningTaskPolicy Build(
        PerformanceMode mode,
        int maxCpuWorkers,
        int maxHardwareWorkers,
        int maxTotalWorkers,
        int probeConcurrency,
        TimeSpan uiProgressInterval,
        TimeSpan etaSampleWindow,
        TimeSpan etaMinimumRuntime,
        TimeSpan startupGracePeriod,
        TimeSpan stallThreshold,
        ProcessPriorityMode processPriority,
        SoftwareThreadPolicy softwareThreadPolicy,
        int logicalProcessors)
    {
        var total = Math.Clamp(maxTotalWorkers, 1, 4);
        return new LongRunningTaskPolicy(
            mode,
            Math.Clamp(maxCpuWorkers, 1, total),
            Math.Clamp(maxHardwareWorkers, 1, total),
            total,
            Math.Clamp(probeConcurrency, 1, 8),
            uiProgressInterval,
            etaSampleWindow,
            5,
            etaMinimumRuntime,
            startupGracePeriod,
            stallThreshold,
            processPriority,
            softwareThreadPolicy,
            softwareThreadPolicy == SoftwareThreadPolicy.ReserveSystemCores
                ? CalculateSoftwareThreadCount(logicalProcessors)
                : null);
    }

    private static bool IsConstrainedMachine(
        int logicalProcessors,
        long memoryBytes,
        EncoderCapabilitySet? capabilities,
        IReadOnlyList<VideoEncoder> plannedEncoders)
    {
        if (logicalProcessors <= 6 || memoryBytes is > 0 and <= EightGiB)
        {
            return true;
        }

        var usableHardware = capabilities?.Capabilities
            .Where(capability => capability.IsHardware && capability.IsUsable)
            .ToArray() ?? Array.Empty<EncoderCapability>();
        var hasQsvOnlyHardware = usableHardware.Length > 0 &&
            usableHardware.All(capability => capability.Vendor == EncoderVendor.Intel);
        var plannedHardwareIsQsv = plannedEncoders.Count > 0 &&
            plannedEncoders.Any(encoder => EncoderCatalog.Get(encoder).IsHardware) &&
            plannedEncoders.Where(encoder => EncoderCatalog.Get(encoder).IsHardware)
                .All(encoder => EncoderCatalog.Get(encoder).Vendor == EncoderVendor.Intel);

        // This is an intentionally weak signal: QSV-only plus a modest
        // machine, never Intel alone. A modern high-memory Intel desktop
        // remains in balanced mode.
        return (hasQsvOnlyHardware || plannedHardwareIsQsv) &&
            logicalProcessors <= 8 &&
            memoryBytes is > 0 and <= SixteenGiB;
    }

    private static int CalculateSoftwareThreadCount(int logicalProcessors)
    {
        var reserved = logicalProcessors >= 8 ? 2 : 1;
        return Math.Max(1, logicalProcessors - reserved);
    }

    private static long GetAvailableMemoryBytes()
    {
        try
        {
            return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
