using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record MediaProbeLookup(
    VideoFileInfo Info,
    bool CacheHit,
    string? CacheMissReason = null);

public sealed record MediaProbeCacheStatistics(
    int EntryCount,
    int CacheHits,
    int CacheMisses,
    int ActualProbes);

internal sealed class MediaProbeCacheDocument
{
    public int CacheSchemaVersion { get; set; }
    public List<MediaProbeCacheEntry> Entries { get; set; } = [];
}

internal sealed class MediaProbeCacheEntry
{
    public int CacheSchemaVersion { get; set; }
    public string NormalizedFullPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string ProbeToolVersion { get; set; } = string.Empty;
    public DateTimeOffset ProbeTime { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public VideoFileInfo? Info { get; set; }
    public MediaHealthStatus HealthStatus { get; set; }
    public HealthCheckLevel HealthCheckLevel { get; set; }
    public DateTimeOffset? HealthCheckedAt { get; set; }
    public string? HealthCheckMessage { get; set; }
    public string? HealthCheckFingerprint { get; set; }
}

/// <summary>
/// Small, process-local persistent cache for ffprobe results. It deliberately
/// keys by file identity and invalidates safely when either the cache schema or
/// ffprobe tool version changes.
/// </summary>
public sealed class MediaProbeCache : IDisposable
{
    public const int CurrentCacheSchemaVersion = 1;
    public const string CacheFileName = "media-probe-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cachePath;
    private readonly int _maximumEntries;
    private readonly TimeSpan _missingPathRetention;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyGates = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, MediaProbeCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _saveTimer;
    private bool _loaded;
    private bool _dirty;
    private long _changeVersion;
    private int _cacheHits;
    private int _cacheMisses;
    private int _actualProbes;
    private string? _lastLoadWarning;
    private bool _disposed;

    public MediaProbeCache(
        string? cachePath = null,
        int maximumEntries = 20_000,
        TimeSpan? missingPathRetention = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Video Compressor",
            "cache",
            CacheFileName);
        _maximumEntries = Math.Max(100, maximumEntries);
        _missingPathRetention = missingPathRetention ?? TimeSpan.FromDays(30);
    }

    public string CachePath => _cachePath;
    public string? LastLoadWarning => _lastLoadWarning;

    public async Task<MediaProbeLookup> GetOrProbeAsync(
        FFmpegTools tools,
        string path,
        FFprobeService probeService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probeService);
        string? toolVersion;
        try
        {
            toolVersion = await probeService.GetToolVersionAsync(tools, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("probe-cache", $"无法读取 ffprobe 版本，将使用未知版本标记：{exception.Message}");
            toolVersion = null;
        }

        return await GetOrProbeAsync(
            path,
            toolVersion,
            token => probeService.ProbeAsync(tools, path, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaProbeLookup> GetOrProbeAsync(
        string path,
        string? probeToolVersion,
        Func<CancellationToken, Task<VideoFileInfo>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ThrowIfDisposed();
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var fingerprint = MediaFileFingerprint.FromFile(path);
        var normalizedToolVersion = NormalizeToolVersion(probeToolVersion);
        if (TryGetHit(fingerprint, normalizedToolVersion, out var cached, out _))
        {
            Interlocked.Increment(ref _cacheHits);
            return new MediaProbeLookup(cached, true);
        }

        var keyGate = _keyGates.GetOrAdd(fingerprint.NormalizedFullPath, _ => new SemaphoreSlim(1, 1));
        await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetHit(fingerprint, normalizedToolVersion, out cached, out _))
            {
                Interlocked.Increment(ref _cacheHits);
                return new MediaProbeLookup(cached, true);
            }

            TryGetHit(fingerprint, normalizedToolVersion, out _, out var missReason);
            Interlocked.Increment(ref _cacheMisses);
            Interlocked.Increment(ref _actualProbes);
            DiagnosticLog.Write(
                "probe-cache",
                $"CacheMiss：{fingerprint.NormalizedFullPath}；原因：{missReason}");
            var probed = await probe(cancellationToken).ConfigureAwait(false);
            var normalizedInfo = probed.WithFileIdentity(fingerprint);
            StoreProbe(fingerprint, normalizedToolVersion, normalizedInfo);
            return new MediaProbeLookup(normalizedInfo, false, missReason);
        }
        finally
        {
            keyGate.Release();
        }
    }

    public async Task<VideoFileInfo?> TryGetCachedInfoAsync(
        string path,
        string? probeToolVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            return null;
        }

        var fingerprint = MediaFileFingerprint.FromFile(path);
        return TryGetHit(fingerprint, NormalizeToolVersion(probeToolVersion), out var info, out _)
            ? info
            : null;
    }

    public bool TryGetCachedHealth(
        MediaFileFingerprint fingerprint,
        string? probeToolVersion,
        HealthCheckLevel requestedLevel,
        out MediaHealthCheckResult result)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(fingerprint.NormalizedFullPath, out var entry) ||
                !IsIdentityMatch(entry, fingerprint, NormalizeToolVersion(probeToolVersion)) ||
                entry.HealthStatus == MediaHealthStatus.NotChecked ||
                entry.HealthCheckLevel < requestedLevel ||
                !string.Equals(entry.HealthCheckFingerprint, fingerprint.StableValue, StringComparison.Ordinal))
            {
                result = null!;
                return false;
            }

            result = new MediaHealthCheckResult(
                entry.HealthStatus,
                entry.HealthCheckLevel,
                entry.HealthCheckMessage ?? "已通过健康检查。",
                entry.HealthCheckedAt ?? DateTimeOffset.MinValue,
                fingerprint,
                CacheHit: true);
            return true;
        }
    }

    public void SetHealth(
        MediaFileFingerprint fingerprint,
        string? probeToolVersion,
        VideoFileInfo info,
        MediaHealthCheckResult health)
    {
        ThrowIfDisposed();
        var normalizedToolVersion = NormalizeToolVersion(probeToolVersion);
        lock (_sync)
        {
            if (!_entries.TryGetValue(fingerprint.NormalizedFullPath, out var entry) ||
                !IsIdentityMatch(entry, fingerprint, normalizedToolVersion))
            {
                entry = CreateEntry(fingerprint, normalizedToolVersion, info.WithFileIdentity(fingerprint));
                _entries[fingerprint.NormalizedFullPath] = entry;
            }

            entry.Info = entry.Info?.WithHealthCheck(health) ?? info.WithFileIdentity(fingerprint).WithHealthCheck(health);
            entry.HealthStatus = health.Status;
            entry.HealthCheckLevel = health.Level;
            entry.HealthCheckedAt = health.CheckedAt;
            entry.HealthCheckMessage = health.Message;
            entry.HealthCheckFingerprint = fingerprint.StableValue;
            entry.LastSeenAt = DateTimeOffset.UtcNow;
            MarkDirtyLocked();
        }
    }

    /// <summary>Stores a result that was just verified by a real ffprobe run.</summary>
    public void StoreVerifiedProbe(
        string path,
        string? probeToolVersion,
        VideoFileInfo info)
    {
        ThrowIfDisposed();
        if (!File.Exists(path))
        {
            return;
        }

        var fingerprint = MediaFileFingerprint.FromFile(path);
        StoreProbe(fingerprint, NormalizeToolVersion(probeToolVersion), info.WithFileIdentity(fingerprint));
    }

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _actualProbes, 0);
    }

    public MediaProbeCacheStatistics GetStatistics()
    {
        int entries;
        lock (_sync)
        {
            entries = _entries.Count;
        }

        return new MediaProbeCacheStatistics(
            entries,
            Volatile.Read(ref _cacheHits),
            Volatile.Read(ref _cacheMisses),
            Volatile.Read(ref _actualProbes));
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_cachePath))
                {
                    await using var stream = File.OpenRead(_cachePath);
                    var document = await JsonSerializer.DeserializeAsync<MediaProbeCacheDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (document?.CacheSchemaVersion == CurrentCacheSchemaVersion)
                    {
                        var loaded = new Dictionary<string, MediaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in document.Entries ?? [])
                        {
                            if (string.IsNullOrWhiteSpace(entry.NormalizedFullPath) || entry.Info is null)
                            {
                                continue;
                            }

                            entry.NormalizedFullPath = MediaFileFingerprint.NormalizePath(entry.NormalizedFullPath);
                            entry.CacheSchemaVersion = CurrentCacheSchemaVersion;
                            loaded[entry.NormalizedFullPath] = entry;
                        }

                        lock (_sync)
                        {
                            _entries = loaded;
                        }
                    }
                    else
                    {
                        _lastLoadWarning = "媒体探测缓存 schema 已变化，已安全失效。";
                        DiagnosticLog.Write("probe-cache", _lastLoadWarning);
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _lastLoadWarning = "媒体探测缓存损坏，已忽略并重新建立。";
                DiagnosticLog.Write("probe-cache", $"{_lastLoadWarning} {exception.Message}");
                lock (_sync)
                {
                    _entries = new Dictionary<string, MediaProbeCacheEntry>(StringComparer.OrdinalIgnoreCase);
                    _dirty = false;
                }
            }
            finally
            {
                _loaded = true;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Removes entries that have been absent from a normal scan for a long time.
    /// It never scans the disk by itself.
    /// </summary>
    public void PruneStalePaths(IEnumerable<string> pathsSeenInScan)
    {
        ThrowIfDisposed();
        var seen = pathsSeenInScan
            .Select(MediaFileFingerprint.NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var threshold = DateTimeOffset.UtcNow - _missingPathRetention;
        lock (_sync)
        {
            var stale = _entries.Values
                .Where(entry => !seen.Contains(entry.NormalizedFullPath) && entry.LastSeenAt < threshold)
                .Select(entry => entry.NormalizedFullPath)
                .ToArray();
            foreach (var path in stale)
            {
                _entries.Remove(path);
            }

            TrimEntryCountLocked();
            if (stale.Length > 0)
            {
                MarkDirtyLocked();
            }
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

        _disposed = true;
        _saveTimer?.Dispose();
        try
        {
            PersistAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            DiagnosticLog.Write("probe-cache", $"关闭时保存媒体探测缓存失败：{exception.Message}");
        }

        foreach (var gate in _keyGates.Values)
        {
            gate.Dispose();
        }

        _loadGate.Dispose();
        _writeGate.Dispose();
    }

    private void StoreProbe(MediaFileFingerprint fingerprint, string probeToolVersion, VideoFileInfo info)
    {
        lock (_sync)
        {
            var oldHealth = _entries.TryGetValue(fingerprint.NormalizedFullPath, out var old) && IsIdentityMatch(old, fingerprint, probeToolVersion)
                ? old
                : null;
            var entry = CreateEntry(fingerprint, probeToolVersion, info);
            if (oldHealth is not null)
            {
                entry.HealthStatus = oldHealth.HealthStatus;
                entry.HealthCheckLevel = oldHealth.HealthCheckLevel;
                entry.HealthCheckedAt = oldHealth.HealthCheckedAt;
                entry.HealthCheckMessage = oldHealth.HealthCheckMessage;
                entry.HealthCheckFingerprint = oldHealth.HealthCheckFingerprint;
                entry.Info = oldHealth.Info?.WithFileIdentity(fingerprint) ?? info;
            }

            _entries[fingerprint.NormalizedFullPath] = entry;
            TrimEntryCountLocked();
            MarkDirtyLocked();
        }
    }

    private static MediaProbeCacheEntry CreateEntry(
        MediaFileFingerprint fingerprint,
        string probeToolVersion,
        VideoFileInfo info) => new()
    {
        CacheSchemaVersion = CurrentCacheSchemaVersion,
        NormalizedFullPath = fingerprint.NormalizedFullPath,
        FileSizeBytes = fingerprint.FileSizeBytes,
        LastWriteTimeUtc = fingerprint.LastWriteTimeUtc,
        ProbeToolVersion = probeToolVersion,
        ProbeTime = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        Info = info
    };

    private bool TryGetHit(
        MediaFileFingerprint fingerprint,
        string probeToolVersion,
        out VideoFileInfo info,
        out string missReason)
    {
        lock (_sync)
        {
            missReason = string.Empty;
            if (!_entries.TryGetValue(fingerprint.NormalizedFullPath, out var entry))
            {
                info = null!;
                missReason = "未找到缓存记录";
                return false;
            }

            if (entry.CacheSchemaVersion != CurrentCacheSchemaVersion)
            {
                info = null!;
                missReason = "缓存 schema 已变化";
                return false;
            }

            if (!string.Equals(entry.ProbeToolVersion, probeToolVersion, StringComparison.OrdinalIgnoreCase))
            {
                info = null!;
                missReason = "ffprobe 版本已变化";
                return false;
            }

            if (entry.FileSizeBytes != fingerprint.FileSizeBytes)
            {
                info = null!;
                missReason = "文件大小已变化";
                return false;
            }

            if (entry.LastWriteTimeUtc != fingerprint.LastWriteTimeUtc)
            {
                info = null!;
                missReason = "文件修改时间已变化";
                return false;
            }

            if (entry.Info is null)
            {
                info = null!;
                missReason = "缓存内容不完整";
                return false;
            }

            entry.LastSeenAt = DateTimeOffset.UtcNow;
            info = entry.Info.WithFileIdentity(fingerprint);
            if (entry.HealthStatus != MediaHealthStatus.NotChecked &&
                string.Equals(entry.HealthCheckFingerprint, fingerprint.StableValue, StringComparison.Ordinal))
            {
                info = info.WithHealthCheck(new MediaHealthCheckResult(
                    entry.HealthStatus,
                    entry.HealthCheckLevel,
                    entry.HealthCheckMessage ?? "已通过健康检查。",
                    entry.HealthCheckedAt ?? DateTimeOffset.MinValue,
                    fingerprint));
            }

            return true;
        }
    }

    private static bool IsIdentityMatch(MediaProbeCacheEntry entry, MediaFileFingerprint fingerprint, string probeToolVersion) =>
        entry.CacheSchemaVersion == CurrentCacheSchemaVersion &&
        string.Equals(entry.NormalizedFullPath, fingerprint.NormalizedFullPath, StringComparison.OrdinalIgnoreCase) &&
        entry.FileSizeBytes == fingerprint.FileSizeBytes &&
        entry.LastWriteTimeUtc == fingerprint.LastWriteTimeUtc &&
        string.Equals(entry.ProbeToolVersion, probeToolVersion, StringComparison.OrdinalIgnoreCase);

    private void TrimEntryCountLocked()
    {
        if (_entries.Count <= _maximumEntries)
        {
            return;
        }

        foreach (var entry in _entries.Values
                     .OrderBy(item => item.LastSeenAt)
                     .Take(_entries.Count - _maximumEntries)
                     .ToArray())
        {
            _entries.Remove(entry.NormalizedFullPath);
        }
    }

    private void MarkDirtyLocked()
    {
        _dirty = true;
        Interlocked.Increment(ref _changeVersion);
        _saveTimer ??= new System.Threading.Timer(static state => _ = ((MediaProbeCache)state!).PersistFromTimerAsync(), this, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(350, Timeout.Infinite);
    }

    private async Task PersistFromTimerAsync()
    {
        try
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            DiagnosticLog.Write("probe-cache", $"延迟保存媒体探测缓存失败：{exception.Message}");
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            return;
        }

        MediaProbeCacheDocument document;
        long version;
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            document = new MediaProbeCacheDocument
            {
                CacheSchemaVersion = CurrentCacheSchemaVersion,
                Entries = _entries.Values.Select(CloneEntry).ToList()
            };
            version = _changeVersion;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _cachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                AtomicReplace(temporaryPath, _cachePath);
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
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static MediaProbeCacheEntry CloneEntry(MediaProbeCacheEntry entry) => new()
    {
        CacheSchemaVersion = entry.CacheSchemaVersion,
        NormalizedFullPath = entry.NormalizedFullPath,
        FileSizeBytes = entry.FileSizeBytes,
        LastWriteTimeUtc = entry.LastWriteTimeUtc,
        ProbeToolVersion = entry.ProbeToolVersion,
        ProbeTime = entry.ProbeTime,
        LastSeenAt = entry.LastSeenAt,
        Info = entry.Info,
        HealthStatus = entry.HealthStatus,
        HealthCheckLevel = entry.HealthCheckLevel,
        HealthCheckedAt = entry.HealthCheckedAt,
        HealthCheckMessage = entry.HealthCheckMessage,
        HealthCheckFingerprint = entry.HealthCheckFingerprint
    };

    private static void AtomicReplace(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
                // MoveFileEx-style replacement is the safe fallback on file systems
                // that do not expose File.Replace.
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeToolVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim();

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MediaProbeCache));
        }
    }
}
