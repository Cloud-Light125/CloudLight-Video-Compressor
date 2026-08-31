using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>Persisted output of expensive Smart/VMAF planning work.</summary>
public sealed class CompressionResultCacheEntry
{
    public int CacheSchemaVersion { get; set; }
    public int PlannerSchemaVersion { get; set; }
    public string Key { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public string PlanRelevantSettingsHash { get; set; } = string.Empty;
    public bool? ShouldCompress { get; set; }
    public string? DecisionReason { get; set; }
    public VideoCodecKind? RecommendedCodec { get; set; }
    public long? EstimatedTargetBitrate { get; set; }
    public double? EstimatedSavings { get; set; }
    public SmartCompressionDecision? DecisionSnapshot { get; set; }
    public VmafCalibrationResult? VmafCalibrationResult { get; set; }
    public DateTimeOffset? CalibrationTimestamp { get; set; }
    public DateTimeOffset CachedAt { get; set; }
}

internal sealed class CompressionResultCacheDocument
{
    public int CacheSchemaVersion { get; set; }
    public int PlannerSchemaVersion { get; set; }
    public List<CompressionResultCacheEntry> Entries { get; set; } = [];
}

/// <summary>
/// A small JSON cache for decisions, not for encoder capabilities. The key
/// includes the source fingerprint and every setting that can affect planning.
/// </summary>
public sealed class CompressionResultCache : IDisposable
{
    public const int CurrentCacheSchemaVersion = 1;
    public const int CurrentPlannerSchemaVersion = 2;
    public const string CacheFileName = "compression-result-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cachePath;
    private readonly int _maximumEntries;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Dictionary<string, CompressionResultCacheEntry> _entries = new(StringComparer.Ordinal);
    private System.Threading.Timer? _saveTimer;
    private bool _loaded;
    private bool _dirty;
    private long _changeVersion;
    private bool _disposed;
    private string? _lastLoadWarning;

    public CompressionResultCache(string? cachePath = null, int maximumEntries = 20_000)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Video Compressor",
            "cache",
            CacheFileName);
        _maximumEntries = Math.Max(100, maximumEntries);
    }

    public string CachePath => _cachePath;
    public string? LastLoadWarning => _lastLoadWarning;

    public bool TryGet(VideoFileInfo source, AppSettings settings, out CompressionResultCacheEntry entry)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        var key = BuildKey(source, settings, out _, out _);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var found) &&
                found.CacheSchemaVersion == CurrentCacheSchemaVersion &&
                found.PlannerSchemaVersion == CurrentPlannerSchemaVersion)
            {
                entry = found;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public void Set(
        VideoFileInfo source,
        AppSettings settings,
        SmartCompressionDecision? decision = null,
        VmafCalibrationResult? calibration = null)
    {
        ThrowIfDisposed();
        EnsureLoaded();
        var key = BuildKey(source, settings, out var fingerprint, out var settingsHash);
        lock (_sync)
        {
            _entries.TryGetValue(key, out var previous);
            _entries[key] = new CompressionResultCacheEntry
            {
                CacheSchemaVersion = CurrentCacheSchemaVersion,
                PlannerSchemaVersion = CurrentPlannerSchemaVersion,
                Key = key,
                SourceFingerprint = fingerprint.StableValue,
                PlanRelevantSettingsHash = settingsHash,
                ShouldCompress = decision?.ShouldCompress ?? previous?.ShouldCompress,
                DecisionReason = decision?.Reason ?? previous?.DecisionReason,
                RecommendedCodec = decision?.TargetCodec ?? previous?.RecommendedCodec,
                EstimatedTargetBitrate = decision?.TargetVideoBitrateBps ?? previous?.EstimatedTargetBitrate,
                EstimatedSavings = decision?.ExpectedSavingRatio ?? previous?.EstimatedSavings,
                DecisionSnapshot = decision ?? previous?.DecisionSnapshot,
                VmafCalibrationResult = calibration ?? previous?.VmafCalibrationResult,
                CalibrationTimestamp = calibration?.CompletedAt ?? previous?.CalibrationTimestamp,
                CachedAt = DateTimeOffset.UtcNow
            };
            TrimEntryCountLocked();
            MarkDirtyLocked();
        }
    }

    public void InvalidateAll()
    {
        ThrowIfDisposed();
        EnsureLoaded();
        lock (_sync)
        {
            _entries.Clear();
            MarkDirtyLocked();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _saveTimer?.Dispose();
        try
        {
            if (_loaded)
            {
                PersistAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("result-cache", $"关闭时保存压缩结果缓存失败：{exception.Message}");
        }

        _disposed = true;
    }

    public static string BuildKey(
        VideoFileInfo source,
        AppSettings settings,
        out MediaFileFingerprint fingerprint,
        out string settingsHash)
    {
        fingerprint = MediaFileFingerprint.FromVideoInfo(source);
        var canonical = BuildCanonicalSettings(settings);
        settingsHash = Hash(canonical);
        return $"{Hash(fingerprint.StableValue)}:{settingsHash}";
    }

    private static string BuildCanonicalSettings(AppSettings settings)
    {
        var builder = new StringBuilder();
        Append(builder, "planner-schema", CurrentPlannerSchemaVersion);
        Append(builder, "compression-mode", settings.CompressionMode);
        Append(builder, "video-encoder", settings.VideoEncoder);
        Append(builder, "encoder-selection", settings.EncoderSelection);
        Append(builder, "target-codec", settings.TargetVideoCodec);
        Append(builder, "encoding-preset", settings.EncodingPreset);
        Append(builder, "encoder-tuning-preset", settings.EncoderTuningPreset);
        Append(builder, "bit-depth-policy", settings.BitDepthPolicy);
        Append(builder, "crf", settings.Crf.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "target-video-bitrate", settings.TargetVideoBitrateMbps.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "target-size", settings.TargetSize);
        Append(builder, "target-size-unit", settings.TargetSizeUnit);
        Append(builder, "resolution-limit", settings.ResolutionLimit);
        Append(builder, "custom-width", settings.CustomMaxWidth);
        Append(builder, "custom-height", settings.CustomMaxHeight);
        Append(builder, "fps-limit", settings.FpsLimit);
        Append(builder, "custom-fps", settings.CustomMaxFps.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "audio-mode", settings.AudioMode);
        Append(builder, "audio-bitrate", settings.AudioBitrateKbps);
        Append(builder, "output-container", settings.OutputContainer);
        Append(builder, "smart-preset", settings.SmartPreset);
        Append(builder, "compression-profile", settings.CompressionProfile);
        Append(builder, "smart-maximum-bitrate", settings.SmartMaximumVideoBitrateMbps.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "smart-minimum-saving", settings.SmartMinimumExpectedSavingRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "smart-quality-factor", settings.SmartQualityFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "remote-bandwidth", settings.RemotePlaybackBandwidthMbps.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "remote-safety", settings.RemotePlaybackSafetyRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "vmaf-enabled", settings.EnableAdvancedQualityCalibration);
        Append(builder, "vmaf-target", settings.VmafTarget.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, "vmaf-sample-seconds", settings.QualityCalibrationSampleSeconds);
        Append(builder, "vmaf-candidates", settings.QualityCalibrationCandidateCount);
        Append(builder, "vmaf-sampling-schema", VmafQualityCalibrationService.CurrentSamplingSchemaVersion);
        Append(builder, "discard-if-larger", settings.DiscardIfLarger);
        Append(builder, "rules", JsonSerializer.Serialize(settings.Rules, JsonOptions));
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, object? value) =>
        builder.Append(name).Append('=').Append(value?.ToString() ?? "<null>").Append('\n');

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_cachePath))
                {
                    var document = JsonSerializer.Deserialize<CompressionResultCacheDocument>(File.ReadAllText(_cachePath), JsonOptions);
                    if (document?.CacheSchemaVersion == CurrentCacheSchemaVersion &&
                        document.PlannerSchemaVersion == CurrentPlannerSchemaVersion)
                    {
                        var loaded = new Dictionary<string, CompressionResultCacheEntry>(StringComparer.Ordinal);
                        foreach (var entry in document.Entries ?? [])
                        {
                            if (!string.IsNullOrWhiteSpace(entry.Key))
                            {
                                loaded[entry.Key] = entry;
                            }
                        }

                        _entries = loaded;
                    }
                    else
                    {
                        _lastLoadWarning = "压缩结果缓存 schema 已变化，已安全失效。";
                        DiagnosticLog.Write("result-cache", _lastLoadWarning);
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                _entries = new Dictionary<string, CompressionResultCacheEntry>(StringComparer.Ordinal);
                _lastLoadWarning = "压缩结果缓存损坏，已忽略并重新建立。";
                DiagnosticLog.Write("result-cache", $"{_lastLoadWarning} {exception.Message}");
            }
            finally
            {
                _loaded = true;
            }
        }
    }

    private void TrimEntryCountLocked()
    {
        if (_entries.Count <= _maximumEntries)
        {
            return;
        }

        foreach (var key in _entries.Values
                     .OrderBy(entry => entry.CachedAt)
                     .Take(_entries.Count - _maximumEntries)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private void MarkDirtyLocked()
    {
        _dirty = true;
        _changeVersion++;
        _saveTimer ??= new System.Threading.Timer(static state => _ = ((CompressionResultCache)state!).PersistFromTimerAsync(), this, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(500, Timeout.Infinite);
    }

    private async Task PersistFromTimerAsync()
    {
        try
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            DiagnosticLog.Write("result-cache", $"延迟保存压缩结果缓存失败：{exception.Message}");
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            return;
        }

        CompressionResultCacheDocument document;
        long version;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!_dirty)
                {
                    return;
                }

                document = new CompressionResultCacheDocument
                {
                    CacheSchemaVersion = CurrentCacheSchemaVersion,
                    PlannerSchemaVersion = CurrentPlannerSchemaVersion,
                    Entries = _entries.Values.Select(CloneEntry).ToList()
                };
                version = _changeVersion;
            }

            var directory = Path.GetDirectoryName(_cachePath) ?? throw new InvalidOperationException("压缩结果缓存目录无效。");
            Directory.CreateDirectory(directory);
            var tempPath = $"{_cachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_cachePath))
                {
                    try
                    {
                        File.Replace(tempPath, _cachePath, null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(tempPath, _cachePath, overwrite: true);
                    }
                    catch (IOException)
                    {
                        File.Move(tempPath, _cachePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, _cachePath);
                }

                lock (_sync)
                {
                    if (_changeVersion == version)
                    {
                        _dirty = false;
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static CompressionResultCacheEntry CloneEntry(CompressionResultCacheEntry entry) => new()
    {
        CacheSchemaVersion = entry.CacheSchemaVersion,
        PlannerSchemaVersion = entry.PlannerSchemaVersion,
        Key = entry.Key,
        SourceFingerprint = entry.SourceFingerprint,
        PlanRelevantSettingsHash = entry.PlanRelevantSettingsHash,
        ShouldCompress = entry.ShouldCompress,
        DecisionReason = entry.DecisionReason,
        RecommendedCodec = entry.RecommendedCodec,
        EstimatedTargetBitrate = entry.EstimatedTargetBitrate,
        EstimatedSavings = entry.EstimatedSavings,
        DecisionSnapshot = entry.DecisionSnapshot,
        VmafCalibrationResult = entry.VmafCalibrationResult,
        CalibrationTimestamp = entry.CalibrationTimestamp,
        CachedAt = entry.CachedAt
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CompressionResultCache));
        }
    }
}
