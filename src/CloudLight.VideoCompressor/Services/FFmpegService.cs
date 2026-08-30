using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record FFmpegRunResult(
    bool Succeeded,
    int ExitCode,
    string ErrorOutput,
    CompressionFailureKind FailureKind = CompressionFailureKind.None,
    string? FailureMessage = null);

public sealed class FFmpegCancellationTimeoutException : OperationCanceledException
{
    public FFmpegCancellationTimeoutException(int processId)
        : base($"FFmpeg 在取消请求后仍未退出（PID {processId}）。") => ProcessId = processId;

    public int ProcessId { get; }
}

public sealed class FFmpegService
{
    private static readonly TimeSpan CancellationExitTimeout = TimeSpan.FromSeconds(8);

    public async Task<FFmpegRunResult> RunAsync(
        FFmpegTools tools,
        IReadOnlyList<string> arguments,
        double? durationSeconds,
        IProgress<EncodingProgress>? progress,
        CancellationToken cancellationToken,
        EncoderProgressWatchdog? progressWatchdog = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var errorBuilder = new StringBuilder();
        var inputDuration = new InputDuration(durationSeconds);
        var watchdog = progressWatchdog ?? new EncoderProgressWatchdog();
        var progressState = new ProgressState();
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stallSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        var processId = process.Id;
        using var registration = cancellationToken.Register(() => TryKill(process));
        var progressTask = ReadProgressAsync(process.StandardOutput, inputDuration, progress, watchdog, progressState);
        var errorTask = ReadErrorAsync(process.StandardError, errorBuilder, inputDuration);
        var watchdogTask = MonitorWatchdogAsync(process, watchdog, stallSource, watchdogCancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do not return to the file-cleanup stage until the killed encoder has released its output handle.
            TryKill(process);
            if (!await WaitForExitWithinAsync(process, CancellationExitTimeout).ConfigureAwait(false))
            {
                throw new FFmpegCancellationTimeoutException(processId);
            }
            await WaitForReaderTasksWithinAsync(progressTask, errorTask, CancellationExitTimeout).ConfigureAwait(false);
            throw;
        }
        finally
        {
            watchdogCancellation.Cancel();
            try
            {
                await watchdogTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (watchdogCancellation.IsCancellationRequested)
            {
                // The watchdog is intentionally cancelled when FFmpeg exits or the caller cancels.
            }
        }
        if (!await WaitForReaderTasksWithinAsync(progressTask, errorTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new IOException("FFmpeg 已退出，但 stdout/stderr 读取未在时限内结束。");
        }

        var stalled = stallSource.Task.IsCompletedSuccessfully ? stallSource.Task.Result : null;
        return process.ExitCode == 0 && stalled is null
            ? new FFmpegRunResult(true, process.ExitCode, errorBuilder.ToString())
            : new FFmpegRunResult(
                false,
                process.ExitCode,
                CombineError(errorBuilder.ToString(), stalled),
                stalled is null ? ClassifyFailure(errorBuilder.ToString()) : CompressionFailureKind.EncoderStall,
                stalled);
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        InputDuration inputDuration,
        IProgress<EncodingProgress>? receiver,
        EncoderProgressWatchdog watchdog,
        ProgressState state)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            if (line.Length == 0)
            {
                continue;
            }

            var delimiter = line.IndexOf('=');
            if (delimiter <= 0)
            {
                continue;
            }

            values[line[..delimiter]] = line[(delimiter + 1)..];
            if (!line.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var progress = CreateProgress(values, inputDuration.Seconds, line.EndsWith("end", StringComparison.OrdinalIgnoreCase), state);
            watchdog.Observe(progress);
            receiver?.Report(progress);
            values.Clear();
        }
    }

    private static EncodingProgress CreateProgress(
        IReadOnlyDictionary<string, string> values,
        double? durationSeconds,
        bool isEnd,
        ProgressState state)
    {
        var outTimeMicroseconds = GetLong(values, "out_time_us") ?? GetLong(values, "out_time_ms") ?? 0;
        var seconds = outTimeMicroseconds / 1_000_000d;
        var speed = values.TryGetValue("speed", out var speedText) ? speedText : null;
        var percent = durationSeconds is > 0 ? Math.Clamp(seconds / durationSeconds.Value * 100d, 0, isEnd ? 100 : 99.9) : 0;
        if (isEnd)
        {
            percent = 100;
        }

        var now = DateTimeOffset.UtcNow;
        var eta = state.Eta.Update(seconds, durationSeconds, now);
        var stableRemaining = isEnd ? TimeSpan.Zero : eta.IsStable ? eta.Remaining : null;
        return new EncodingProgress(
            percent,
            GetDouble(values, "fps"),
            speed,
            GetLong(values, "total_size"),
            stableRemaining,
            seconds,
            durationSeconds,
            GetLong(values, "frame"),
            ParseBitrate(values.TryGetValue("bitrate", out var bitrate) ? bitrate : null),
            now - state.StartedAt,
            now,
            isEnd || eta.IsStable);
    }

    private static async Task ReadErrorAsync(StreamReader reader, StringBuilder destination, InputDuration inputDuration)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (destination.Length < 24_000)
            {
                var remaining = 24_000 - destination.Length;
                var appendedLength = Math.Min(line.Length, remaining);
                destination.Append(line, 0, appendedLength);
                if (appendedLength == line.Length && destination.Length < 23_999)
                {
                    destination.AppendLine();
                }
            }

            if (TryParseInputDuration(line, out var durationSeconds))
            {
                inputDuration.SetIfMissing(durationSeconds);
            }
        }
    }

    private static async Task MonitorWatchdogAsync(
        Process process,
        EncoderProgressWatchdog watchdog,
        TaskCompletionSource<string> stallSource,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    return;
                }

                var decision = watchdog.Check();
                if (decision.IsStalled)
                {
                    if (stallSource.TrySetResult(decision.Reason))
                    {
                        TryKill(process);
                    }

                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown after FFmpeg exits or the caller cancels.
        }
        catch (InvalidOperationException)
        {
            // The process handle was released while the watchdog was checking it.
        }
    }

    private static bool TryParseInputDuration(string line, out double durationSeconds)
    {
        durationSeconds = 0;
        var marker = line.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var durationStart = marker + "Duration:".Length;
        var durationEnd = line.IndexOf(',', durationStart);
        var value = line[durationStart..(durationEnd < 0 ? line.Length : durationEnd)].Trim();
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
        {
            return false;
        }

        durationSeconds = parsed.TotalSeconds;
        return true;
    }

    private static long? GetLong(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? GetDouble(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? ParseBitrate(string? bitrate)
    {
        if (string.IsNullOrWhiteSpace(bitrate))
        {
            return null;
        }

        var normalized = bitrate.Trim();
        var multiplier = 1d;
        if (normalized.EndsWith("kbits/s", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000d;
            normalized = normalized[..^"kbits/s".Length].Trim();
        }
        else if (normalized.EndsWith("mbits/s", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1_000_000d;
            normalized = normalized[..^"mbits/s".Length].Trim();
        }
        else if (normalized.EndsWith("bits/s", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"bits/s".Length].Trim();
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? (long)Math.Round(value * multiplier)
            : null;
    }

    private static string CombineError(string error, string? stallReason)
    {
        if (string.IsNullOrWhiteSpace(stallReason))
        {
            return error;
        }

        return string.IsNullOrWhiteSpace(error)
            ? $"编码看门狗：{stallReason}"
            : $"{error.TrimEnd()}\n编码看门狗：{stallReason}";
    }

    private static CompressionFailureKind ClassifyFailure(string error)
    {
        var text = error.ToLowerInvariant();
        if (text.Contains("no capable device") ||
            text.Contains("cannot load") ||
            text.Contains("failed to create") ||
            text.Contains("error while opening encoder") ||
            text.Contains("driver") ||
            text.Contains("initialization"))
        {
            return CompressionFailureKind.DeviceInitializationFailure;
        }

        if (text.Contains("resource busy") ||
            text.Contains("session limit") ||
            text.Contains("too many sessions") ||
            text.Contains("device unavailable"))
        {
            return CompressionFailureKind.HardwareSessionFailure;
        }

        if (text.Contains("unknown encoder") || text.Contains("encoder not found"))
        {
            return CompressionFailureKind.EncoderUnavailable;
        }

        if (text.Contains("permission denied") || text.Contains("access is denied"))
        {
            return CompressionFailureKind.PermissionFailure;
        }

        if (text.Contains("no space left") || text.Contains("disk full"))
        {
            return CompressionFailureKind.DiskSpaceFailure;
        }

        if (text.Contains("invalid data found") || text.Contains("moov atom not found"))
        {
            return CompressionFailureKind.SourceCorrupt;
        }

        return CompressionFailureKind.Unknown;
    }

    private static double? ParseSpeed(string? speed)
    {
        if (string.IsNullOrWhiteSpace(speed))
        {
            return null;
        }

        var normalized = speed.Trim().TrimEnd('x');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static void TryKill(Process process)
    {
        MediaProcessRegistry.TryTerminate(process);
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(timeout)).ConfigureAwait(false) != exitTask)
            {
                return false;
            }

            await exitTask.ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            // There is no longer an associated process, so it cannot retain the temporary output handle.
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process state cannot be confirmed; retain temporary output rather than risk deleting an active file.
            return false;
        }
    }

    private static async Task<bool> WaitForReaderTasksWithinAsync(Task first, Task second, TimeSpan timeout)
    {
        var readers = Task.WhenAll(first, second);
        if (await Task.WhenAny(readers, Task.Delay(timeout)).ConfigureAwait(false) != readers)
        {
            return false;
        }

        await readers.ConfigureAwait(false);
        return true;
    }

    private sealed class InputDuration
    {
        private readonly object _sync = new();
        private double? _seconds;

        public InputDuration(double? seconds) => _seconds = seconds is > 0 ? seconds : null;

        public double? Seconds
        {
            get
            {
                lock (_sync)
                {
                    return _seconds;
                }
            }
        }

        public void SetIfMissing(double seconds)
        {
            if (seconds <= 0)
            {
                return;
            }

            lock (_sync)
            {
                _seconds ??= seconds;
            }
        }
    }

    private sealed class ProgressState
    {
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public EtaCalculator Eta { get; } = new();
    }
}
