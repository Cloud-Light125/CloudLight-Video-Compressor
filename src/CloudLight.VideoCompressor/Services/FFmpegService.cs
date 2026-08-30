using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record FFmpegRunResult(bool Succeeded, int ExitCode, string ErrorOutput);

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
        CancellationToken cancellationToken)
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
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        var processId = process.Id;
        using var registration = cancellationToken.Register(() => TryKill(process));
        var progressTask = ReadProgressAsync(process.StandardOutput, inputDuration, progress);
        var errorTask = ReadErrorAsync(process.StandardError, errorBuilder, inputDuration);
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
        if (!await WaitForReaderTasksWithinAsync(progressTask, errorTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new IOException("FFmpeg 已退出，但 stdout/stderr 读取未在时限内结束。");
        }

        return new FFmpegRunResult(process.ExitCode == 0, process.ExitCode, errorBuilder.ToString());
    }

    private static async Task ReadProgressAsync(StreamReader reader, InputDuration inputDuration, IProgress<EncodingProgress>? receiver)
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

            receiver?.Report(CreateProgress(values, inputDuration.Seconds, line.EndsWith("end", StringComparison.OrdinalIgnoreCase)));
            values.Clear();
        }
    }

    private static EncodingProgress CreateProgress(IReadOnlyDictionary<string, string> values, double? durationSeconds, bool isEnd)
    {
        var outTimeMicroseconds = GetLong(values, "out_time_us") ?? GetLong(values, "out_time_ms") ?? 0;
        var seconds = outTimeMicroseconds / 1_000_000d;
        var speed = values.TryGetValue("speed", out var speedText) ? speedText : null;
        var speedValue = ParseSpeed(speed);
        var percent = durationSeconds is > 0 ? Math.Clamp(seconds / durationSeconds.Value * 100d, 0, isEnd ? 100 : 99.9) : 0;
        if (isEnd)
        {
            percent = 100;
        }

        TimeSpan? remaining = null;
        if (durationSeconds is > 0 && speedValue is > 0 && seconds > 0)
        {
            remaining = TimeSpan.FromSeconds(Math.Max(0, (durationSeconds.Value - seconds) / speedValue.Value));
        }

        return new EncodingProgress(percent, GetDouble(values, "fps"), speed, GetLong(values, "total_size"), remaining);
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
}
