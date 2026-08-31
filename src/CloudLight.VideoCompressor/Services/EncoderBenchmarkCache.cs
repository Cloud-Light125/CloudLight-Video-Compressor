using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public static class MachineFingerprintService
{
    public static MachineFingerprint Create(EncoderCapabilitySet capabilities)
    {
        var encoders = capabilities.Capabilities
            .Where(capability => capability.IsUsable)
            .Select(capability => capability.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MachineFingerprint(
            Environment.ProcessorCount,
            capabilities.FFmpegVersion ?? "(unknown)",
            encoders,
            capabilities.CapabilityFingerprint);
    }
}

/// <summary>
/// Local atomic persistence for a complete benchmark snapshot. A cancelled or
/// partial run is never written over the last complete snapshot.
/// </summary>
public sealed class EncoderBenchmarkCache : IDisposable
{
    public const int CurrentSchemaVersion = 1;
    public const string CacheFileName = "benchmark.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _cachePath;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private EncoderBenchmarkSnapshot? _snapshot;
    private bool _loaded;
    private bool _dirty;
    private bool _disposed;
    private string? _lastLoadWarning;

    public EncoderBenchmarkCache(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight Video Compressor",
            CacheFileName);
    }

    public string CachePath => _cachePath;
    public string? LastLoadWarning => _lastLoadWarning;

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

            LoadCore();
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public EncoderBenchmarkSnapshot? GetBest(MachineFingerprint currentFingerprint)
    {
        ThrowIfDisposed();
        EnsureLoadedSynchronously();
        lock (_sync)
        {
            if (_snapshot is null || _snapshot.SchemaVersion != CurrentSchemaVersion || !_snapshot.IsComplete)
            {
                return null;
            }

            if (!_snapshot.Matches(currentFingerprint))
            {
                _lastLoadWarning = "当前 FFmpeg、驱动或编码器能力与 Benchmark 缓存不一致，旧结果已失效，请重新测试本机编码性能。";
                return null;
            }

            return _snapshot;
        }
    }

    public EncoderBenchmarkSnapshot? GetCurrent(EncoderCapabilitySet capabilities) =>
        GetBest(MachineFingerprintService.Create(capabilities));

    public void SetComplete(EncoderBenchmarkSnapshot snapshot)
    {
        ThrowIfDisposed();
        if (!snapshot.IsComplete || snapshot.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("只能保存完整且当前 schema 的 Benchmark 快照。", nameof(snapshot));
        }

        EnsureLoadedSynchronously();
        lock (_sync)
        {
            _snapshot = snapshot;
            _dirty = true;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a complete run atomically and only publishes it to the in-memory
    /// cache after the durable replace succeeds. This keeps cancellation or a
    /// disk error from making an unsaved result look trusted to the planner.
    /// </summary>
    public async Task SaveCompleteAsync(
        EncoderBenchmarkSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!snapshot.IsComplete || snapshot.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("只能保存完整且当前 schema 的 Benchmark 快照。", nameof(snapshot));
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        EncoderBenchmarkSnapshot? previous;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                previous = _snapshot;
            }

            await WriteSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                // If another caller published a newer pending snapshot while
                // the file was being written, keep that newer state dirty.
                if (ReferenceEquals(_snapshot, previous))
                {
                    _snapshot = snapshot;
                    _dirty = false;
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => SaveAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_loaded)
            {
                PersistAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("benchmark-cache", $"关闭时保存 Benchmark 缓存失败：{exception.Message}");
        }
        finally
        {
            _loadGate.Dispose();
            _writeGate.Dispose();
            _disposed = true;
        }
    }

    private void EnsureLoadedSynchronously()
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

            LoadCore();
            _loaded = true;
        }
    }

    private void LoadCore()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var snapshot = JsonSerializer.Deserialize<EncoderBenchmarkSnapshot>(
                File.ReadAllText(_cachePath),
                JsonOptions);
            if (snapshot?.SchemaVersion == CurrentSchemaVersion && snapshot.IsComplete && snapshot.Machine is not null)
            {
                lock (_sync)
                {
                    _snapshot = snapshot;
                }
            }
            else
            {
                _lastLoadWarning = "Benchmark 缓存 schema 已变化，已安全失效。请重新测试本机编码性能。";
                DiagnosticLog.Write("benchmark-cache", _lastLoadWarning);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _lastLoadWarning = "Benchmark 缓存损坏，已忽略并重新建立。";
            DiagnosticLog.Write("benchmark-cache", $"{_lastLoadWarning} {exception.Message}");
            lock (_sync)
            {
                _snapshot = null;
                _dirty = false;
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        EncoderBenchmarkSnapshot? snapshot;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!_dirty || _snapshot is null)
                {
                    return;
                }

                snapshot = _snapshot;
            }

            await WriteSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(_snapshot, snapshot))
                {
                    _dirty = false;
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteSnapshotAsync(
        EncoderBenchmarkSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Benchmark 缓存目录无效。 ");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_cachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_cachePath))
            {
                try
                {
                    File.Replace(temporaryPath, _cachePath, null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, _cachePath, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temporaryPath, _cachePath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, _cachePath);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EncoderBenchmarkCache));
        }
    }
}
