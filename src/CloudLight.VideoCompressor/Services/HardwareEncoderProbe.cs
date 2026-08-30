using System.Diagnostics;
using System.Text;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record HardwareEncoderProbeResult(bool IsUsable, string? Error, bool TimedOut = false);

/// <summary>
/// Performs a tiny encoder initialization test without touching a user file.
/// The process is registered with the same application-owned registry as normal
/// encodes so shutdown and cancellation have identical semantics.
/// </summary>
public sealed class HardwareEncoderProbe
{
    private readonly TimeSpan _timeout;

    public HardwareEncoderProbe(TimeSpan? timeout = null) =>
        _timeout = timeout.GetValueOrDefault(TimeSpan.FromSeconds(8));

    public async Task<HardwareEncoderProbeResult> ProbeAsync(
        FFmpegTools tools,
        VideoEncoder encoder,
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
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=128x72:r=1",
            "-frames:v", "2", "-an", "-c:v", CompressionPlan.FfmpegEncoderName(encoder),
            "-f", "null", "-"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        using var registration = timeoutCancellation.Token.Register(() => MediaProcessRegistry.TryTerminate(process));
        var outputTask = ReadBoundedAsync(process.StandardOutput, timeoutCancellation.Token);
        var errorTask = ReadBoundedAsync(process.StandardError, timeoutCancellation.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            MediaProcessRegistry.TryTerminate(process);
            await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await WaitForReadersWithinAsync(outputTask, errorTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new HardwareEncoderProbeResult(false, "硬件编码 smoke test 超时。", true);
        }

        if (!await WaitForReadersWithinAsync(outputTask, errorTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            return new HardwareEncoderProbeResult(false, "硬件编码 smoke test 的输出读取超时。", true);
        }

        var error = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? new HardwareEncoderProbeResult(true, null)
            : new HardwareEncoderProbeResult(false, TrimError(error));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 16_000;
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return builder.ToString();
            }

            if (builder.Length < maximumCharacters)
            {
                var remaining = maximumCharacters - builder.Length;
                builder.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                if (builder.Length < maximumCharacters)
                {
                    builder.AppendLine();
                }
            }
        }
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
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForReadersWithinAsync(Task<string> first, Task<string> second, TimeSpan timeout)
    {
        var readers = Task.WhenAll(first, second);
        if (await Task.WhenAny(readers, Task.Delay(timeout)).ConfigureAwait(false) != readers)
        {
            return false;
        }

        try
        {
            await readers.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The linked probe token can cancel the bounded reader after the process
            // has been terminated; the caller only needs the process result here.
        }
        return true;
    }

    private static string TrimError(string text) =>
        string.IsNullOrWhiteSpace(text) ? "未返回错误文本。" : text.Trim()[..Math.Min(text.Trim().Length, 1_000)];
}
