using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record FFmpegTools(string FFmpegPath, string FFprobePath);
public sealed record FFmpegCapabilities(IReadOnlySet<VideoEncoder> Encoders, string? Version);

public sealed class FFmpegLocator
{
    private readonly string _applicationBaseDirectory;
    private readonly Func<string?> _pathProvider;

    public FFmpegLocator(string? applicationBaseDirectory = null, Func<string?>? pathProvider = null)
    {
        _applicationBaseDirectory = applicationBaseDirectory ?? AppContext.BaseDirectory;
        _pathProvider = pathProvider ?? (() => Environment.GetEnvironmentVariable("PATH"));
    }

    public FFmpegTools? Locate(string? configuredPath)
    {
        var candidateDirectories = new List<string>();

        // A path picked explicitly in Settings is an intentional advanced-user override. Without that opt-in,
        // the installed application's bundled tools are always selected before legacy locations and PATH.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidateDirectories.Add(File.Exists(configuredPath)
                ? Path.GetDirectoryName(configuredPath) ?? configuredPath
                : configuredPath);
        }

        candidateDirectories.Add(Path.Combine(_applicationBaseDirectory, "ffmpeg"));
        candidateDirectories.Add(Path.Combine(_applicationBaseDirectory, "FFmpeg"));
        candidateDirectories.Add(_applicationBaseDirectory);

        var pathVariable = _pathProvider() ?? string.Empty;
        candidateDirectories.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var directory in candidateDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
                var ffprobe = Path.Combine(directory, "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                {
                    return new FFmpegTools(ffmpeg, ffprobe);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH element should not prevent later candidates from being checked.
            }
        }

        return null;
    }

    public async Task<IReadOnlySet<VideoEncoder>> GetAvailableEncodersAsync(FFmpegTools tools, CancellationToken cancellationToken) =>
        (await GetCapabilitiesAsync(tools, cancellationToken).ConfigureAwait(false)).Encoders;

    public async Task<FFmpegCapabilities> GetCapabilitiesAsync(
        FFmpegTools tools,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-encoders");

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(timeout.GetValueOrDefault(TimeSpan.FromSeconds(10)));
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        using var registration = operationCancellation.Token.Register(() => TryKill(process));
        var outputTask = ReadBoundedAsync(process.StandardOutput, operationCancellation.Token);
        var errorTask = ReadBoundedAsync(process.StandardError, operationCancellation.Token);
        try
        {
            await process.WaitForExitAsync(operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitWithinAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("读取 FFmpeg 编码器列表超时。");
            }
            throw;
        }

        if (!await WaitForReaderTasksWithinAsync(outputTask, errorTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new IOException("FFmpeg 已退出，但编码器列表的 stdout/stderr 读取未在时限内结束。");
        }

        var text = (await outputTask.ConfigureAwait(false)) + Environment.NewLine + (await errorTask.ConfigureAwait(false));

        var result = new HashSet<VideoEncoder>();
        var map = new Dictionary<string, VideoEncoder>(StringComparer.OrdinalIgnoreCase)
        {
            ["libx264"] = VideoEncoder.Libx264,
            ["libx265"] = VideoEncoder.Libx265,
            ["h264_nvenc"] = VideoEncoder.H264Nvenc,
            ["hevc_nvenc"] = VideoEncoder.HevcNvenc,
            ["h264_qsv"] = VideoEncoder.H264Qsv,
            ["hevc_qsv"] = VideoEncoder.HevcQsv,
            ["h264_amf"] = VideoEncoder.H264Amf,
            ["hevc_amf"] = VideoEncoder.HevcAmf,
            ["libsvtav1"] = VideoEncoder.LibsvtAv1
        };
        var encoderIds = ParseEncoderIds(text);
        foreach (var entry in map)
        {
            if (encoderIds.Contains(entry.Key))
            {
                result.Add(entry.Value);
            }
        }

        return new FFmpegCapabilities(result, ExtractVersion(text));
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 256_000;
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

    private static IReadOnlySet<string> ParseEncoderIds(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*[A-Z.]{6}\s+(?<id>\S+)\s+", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                result.Add(match.Groups["id"].Value);
            }
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        MediaProcessRegistry.TryTerminate(process);
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (!process.HasExited)
            {
                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(timeout)).ConfigureAwait(false) != exitTask)
                {
                    return false;
                }
                await exitTask.ConfigureAwait(false);
            }

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

    private static string? ExtractVersion(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return null;
        }

        var version = line["ffmpeg version ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(version) ? null : version.TrimStart('n', 'N').Split('-')[0];
    }
}
