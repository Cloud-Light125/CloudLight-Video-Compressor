using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Models;
using Microsoft.Win32;

namespace CloudLight.VideoCompressor.Services;

public sealed record HardwareEnvironmentIdentity(string GpuName, string NvidiaDriverVersion)
{
    public static HardwareEnvironmentIdentity Unknown { get; } = new("(none)", "(none)");
}

public sealed class HardwareEnvironmentProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HardwareEnvironmentIdentity> ProbeAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveNvidiaSmiPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--query-gpu=name,driver_version");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                DiagnosticLog.Write("encoder-detect", $"nvidia-smi exit code {process.ExitCode}: {Compact(error)}");
                return ProbeWindowsRegistry();
            }

            var rows = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(',', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                .ToArray();
            if (rows.Length == 0)
            {
                return ProbeWindowsRegistry();
            }

            var identity = new HardwareEnvironmentIdentity(
                string.Join("; ", rows.Select(parts => parts[0]).Distinct(StringComparer.OrdinalIgnoreCase)),
                string.Join("; ", rows.Select(parts => parts[1]).Distinct(StringComparer.OrdinalIgnoreCase)));
            DiagnosticLog.Write("encoder-detect", $"GPU: {identity.GpuName}; NVIDIA driver: {identity.NvidiaDriverVersion}");
            return identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            DiagnosticLog.Write("encoder-detect", $"nvidia-smi identity probe unavailable: {exception.Message}");
            return ProbeWindowsRegistry();
        }
    }

    private static string Compact(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();
        return text.Length <= 1_000 ? text : text[^1_000..];
    }

    private static string ResolveNvidiaSmiPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvidia-smi.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "nvidia-smi.exe";
    }

    private static HardwareEnvironmentIdentity ProbeWindowsRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return HardwareEnvironmentIdentity.Unknown;
        }

        try
        {
            using var videoKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (videoKey is null)
            {
                return HardwareEnvironmentIdentity.Unknown;
            }

            var adapters = new List<(string Name, string Driver)>();
            foreach (var adapterKeyName in videoKey.GetSubKeyNames())
            {
                using var adapterKey = videoKey.OpenSubKey(adapterKeyName);
                if (adapterKey is null)
                {
                    continue;
                }
                foreach (var instanceName in adapterKey.GetSubKeyNames().Where(name => int.TryParse(name, out _)))
                {
                    using var instanceKey = adapterKey.OpenSubKey(instanceName);
                    var name = instanceKey?.GetValue("DriverDesc") as string;
                    var provider = instanceKey?.GetValue("ProviderName") as string;
                    if (string.IsNullOrWhiteSpace(name) ||
                        !(name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                          provider?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        continue;
                    }

                    var driver = instanceKey?.GetValue("DriverVersion") as string ?? "(unknown)";
                    adapters.Add((name, driver));
                }
            }

            if (adapters.Count == 0)
            {
                return HardwareEnvironmentIdentity.Unknown;
            }

            var identity = new HardwareEnvironmentIdentity(
                string.Join("; ", adapters.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase)),
                string.Join("; ", adapters.Select(item => item.Driver).Distinct(StringComparer.OrdinalIgnoreCase)));
            DiagnosticLog.Write("encoder-detect", $"GPU registry fallback: {identity.GpuName}; NVIDIA driver: {identity.NvidiaDriverVersion}");
            return identity;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write("encoder-detect", $"GPU registry identity probe unavailable: {exception.Message}");
            return HardwareEnvironmentIdentity.Unknown;
        }
    }
}

public sealed record EncoderCapabilityCacheIdentity(
    string GpuName,
    string NvidiaDriverVersion,
    string FFmpegVersion,
    string FFmpegPath);

internal sealed record EncoderCapabilityCacheDocument(
    int SchemaVersion,
    EncoderCapabilityCacheIdentity Identity,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> ListedEncoderIds,
    IReadOnlyList<EncoderCapability> Capabilities);

/// <summary>
/// Persistent capability snapshot. It is an optimization only: identity or
/// encoder-list changes invalidate it, and an NVENC failure is never reused.
/// </summary>
public sealed class EncoderCapabilityCache
{
    public const int CurrentSchemaVersion = 1;
    public const string CacheFileName = "encoder-capabilities.json";
    public static TimeSpan FreshnessWindow => TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cachePath;

    public EncoderCapabilityCache(string? cachePath = null) =>
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Video Compressor",
            "cache",
            CacheFileName);

    public string CachePath => _cachePath;

    public bool TryGet(
        EncoderCapabilityCacheIdentity identity,
        IReadOnlySet<string> listedEncoderIds,
        out EncoderCapabilitySet capabilities,
        out string missReason)
    {
        capabilities = EncoderCapabilitySet.SoftwareDefaults;
        missReason = "cache file not found";
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<EncoderCapabilityCacheDocument>(File.ReadAllText(_cachePath), JsonOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                missReason = "schema changed";
                return false;
            }
            if (!IdentityEquals(document.Identity, identity))
            {
                missReason = "GPU, NVIDIA driver, FFmpeg version, or FFmpeg path changed";
                return false;
            }
            if (document.Timestamp > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.UtcNow - document.Timestamp > FreshnessWindow)
            {
                missReason = "timestamp expired";
                return false;
            }
            if (!document.ListedEncoderIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(listedEncoderIds))
            {
                missReason = "FFmpeg encoder list changed";
                return false;
            }

            // A previous transient initialization failure may recover after a
            // reboot or runtime repair. Never let such a negative cache disable
            // NVENC, even when the rest of the identity appears unchanged.
            if (document.Capabilities.Any(capability =>
                    capability.Vendor == EncoderVendor.Nvidia &&
                    capability.IsSupportedByFfmpeg &&
                    !capability.IsUsable))
            {
                missReason = "cached NVENC failure requires a new smoke test";
                return false;
            }

            capabilities = new EncoderCapabilitySet(
                document.Capabilities,
                identity.FFmpegVersion,
                gpuName: identity.GpuName,
                nvidiaDriverVersion: identity.NvidiaDriverVersion,
                ffmpegPath: identity.FFmpegPath,
                detectedAt: document.Timestamp,
                fromCache: true);
            missReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            missReason = $"cache unreadable: {exception.Message}";
            return false;
        }
    }

    public async Task SaveAsync(
        EncoderCapabilityCacheIdentity identity,
        IReadOnlySet<string> listedEncoderIds,
        EncoderCapabilitySet capabilities,
        CancellationToken cancellationToken)
    {
        var document = new EncoderCapabilityCacheDocument(
            CurrentSchemaVersion,
            identity,
            capabilities.DetectedAt,
            listedEncoderIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            capabilities.Capabilities);
        var directory = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException("编码能力缓存目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_cachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IdentityEquals(EncoderCapabilityCacheIdentity left, EncoderCapabilityCacheIdentity right) =>
        string.Equals(left.GpuName, right.GpuName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NvidiaDriverVersion, right.NvidiaDriverVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FFmpegVersion, right.FFmpegVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizePath(left.FFmpegPath), NormalizePath(right.FFmpegPath), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
