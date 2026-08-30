using System.Text.Json;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class CompressionHistoryService : IDisposable
{
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public CompressionHistoryService(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CloudLight",
            "CloudLight Video Compressor",
            "history.json");
    }

    public async Task<IReadOnlyList<CompressionHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_historyPath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                HistoryJsonContext.Default.ListCompressionHistoryEntry,
                cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("history", $"读取历史失败：{exception.Message}");
            return [];
        }
    }

    public async Task AppendAsync(CompressionHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.Insert(0, entry);
            if (entries.Count > 500)
            {
                entries.RemoveRange(500, entries.Count - 500);
            }

            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _historyPath + ".tmp";
            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        entries,
                        HistoryJsonContext.Default.ListCompressionHistoryEntry,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, _historyPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // History is diagnostic metadata; it must never turn a successful
            // compression into a failed file transaction.
            DiagnosticLog.Write("history", $"写入历史失败：{exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
