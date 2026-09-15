using System.Diagnostics;
using System.Text;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record HardwareEncoderProbeResult(
    bool IsUsable,
    string? Error,
    bool TimedOut = false,
    string? Command = null,
    string? StandardError = null,
    int? ExitCode = null);

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
        CancellationToken cancellationToken,
        int targetBitDepth = 8)
    {
        var ffmpegPath = Path.GetFullPath(tools.FFmpegPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var isNvenc = encoder is VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc;
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i" };
        arguments.Add(isNvenc
            ? "testsrc2=size=1280x720:rate=30"
            : "color=c=black:s=128x72:r=1");
        arguments.AddRange(["-frames:v", isNvenc ? "3" : "2", "-an", "-c:v", CompressionPlan.FfmpegEncoderName(encoder)]);
        if (targetBitDepth >= 10)
        {
            arguments.AddRange(["-vf", "format=p010le", "-pix_fmt", "p010le"]);
            arguments.AddRange(["-profile:v", EncoderCatalog.Get(encoder).Codec == VideoCodecKind.H265 ? "main10" : "high10"]);
        }
        arguments.AddRange(
        [
            "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null"
        ]);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        var command = FormatCommand(ffmpegPath, arguments);
        DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke command: {command}");

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

            DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke exit code: (timeout)");
            DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke stderr: (reader cancelled after timeout)");
            return new HardwareEncoderProbeResult(false, "硬件编码 smoke test 超时。", true, command, null, null);
        }

        if (!await WaitForReadersWithinAsync(outputTask, errorTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke exit code: {process.ExitCode}");
            DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke stderr: (read timeout)");
            return new HardwareEncoderProbeResult(false, "硬件编码 smoke test 的输出读取超时。", true, command, null, process.ExitCode);
        }

        var error = await errorTask.ConfigureAwait(false);
        DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke exit code: {process.ExitCode}");
        DiagnosticLog.Write("encoder-detect", $"{CompressionPlan.FfmpegEncoderName(encoder)} smoke stderr:{Environment.NewLine}{LogText(error)}");
        return process.ExitCode == 0
            ? new HardwareEncoderProbeResult(true, null, false, command, error, process.ExitCode)
            : new HardwareEncoderProbeResult(false, TrimError(error), false, command, error, process.ExitCode);
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

    private static string LogText(string text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "(empty)" : text.Trim();
        return value.Length <= 8_000 ? value : value[^8_000..];
    }

    private static string FormatCommand(string executable, IEnumerable<string> arguments) =>
        $"\"{executable}\" {string.Join(' ', arguments.Select(QuoteArgument))}";

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
}
