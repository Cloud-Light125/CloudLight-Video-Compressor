using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class FFprobeService
{
    private static readonly TimeSpan CancellationExitTimeout = TimeSpan.FromSeconds(8);

    public async Task<VideoFileInfo> ProbeAsync(FFmpegTools tools, string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(path);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        using var registration = cancellationToken.Register(() => TryKill(process));
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            if (!await WaitForExitWithinAsync(process, CancellationExitTimeout))
            {
                throw new OperationCanceledException("ffprobe 在取消请求后仍未退出；未执行任何源文件移动。", cancellationToken);
            }
            await WaitForReaderTasksWithinAsync(standardOutput, standardError, CancellationExitTimeout);
            throw;
        }
        if (!await WaitForReaderTasksWithinAsync(standardOutput, standardError, TimeSpan.FromSeconds(5)))
        {
            throw new IOException("ffprobe 已退出，但 stdout/stderr 读取未在时限内结束。");
        }
        var output = await standardOutput;
        var error = await standardError;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 无法读取“{path}”：{TrimError(error)}");
        }

        try
        {
            return ParseProbeJson(path, output);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"ffprobe 返回了无法解析的数据：{exception.Message}", exception);
        }
    }

    public VideoFileInfo ParseProbeJson(string path, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array
            ? streamsElement.EnumerateArray().ToArray()
            : [];
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;

        var file = new FileInfo(path);
        var video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video");
        var audioStreams = streams.Where(stream => GetString(stream, "codec_type") == "audio").ToArray();
        var subtitleCodecs = streams.Where(stream => GetString(stream, "codec_type") == "subtitle")
            .Select(stream => GetString(stream, "codec_name"))
            .Where(codec => !string.IsNullOrWhiteSpace(codec))
            .Cast<string>()
            .ToArray();

        var duration = GetDouble(format, "duration") ?? GetDouble(video, "duration");
        var audioBitrates = audioStreams.Select(stream => GetLong(stream, "bit_rate")).Where(value => value is not null).Cast<long>().ToArray();
        long? audioBitrate = audioBitrates.Length == 0 ? null : audioBitrates.Sum();
        var formatBitrate = GetLong(format, "bit_rate");
        var videoBitrate = GetLong(video, "bit_rate");
        if (videoBitrate is null && formatBitrate is not null && audioBitrate is not null)
        {
            videoBitrate = Math.Max(0, formatBitrate.Value - audioBitrate.Value);
        }

        var totalBitrate = formatBitrate;
        if (totalBitrate is null && duration is > 0 && file.Exists)
        {
            totalBitrate = (long)Math.Round(file.Length * 8d / duration.Value);
        }

        return new VideoFileInfo
        {
            FileName = file.Name,
            FullPath = file.FullName,
            Extension = file.Extension,
            FileSizeBytes = file.Exists ? file.Length : 0,
            DurationSeconds = duration,
            VideoCodec = GetString(video, "codec_name"),
            VideoBitrateBps = videoBitrate,
            TotalBitrateBps = totalBitrate,
            Width = GetInt(video, "width"),
            Height = GetInt(video, "height"),
            FrameRate = ParseFrameRate(GetString(video, "avg_frame_rate") ?? GetString(video, "r_frame_rate")),
            AudioCodec = audioStreams.Select(stream => GetString(stream, "codec_name")).FirstOrDefault(codec => codec is not null),
            AudioBitrateBps = audioBitrate,
            AudioTrackCount = audioStreams.Length,
            SubtitleCodecs = subtitleCodecs
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property)
    {
        var text = GetString(element, property);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value) && value.TryGetInt64(out parsed)
            ? parsed
            : null;
    }

    private static int? GetInt(JsonElement element, string property)
    {
        var value = GetLong(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        var text = GetString(element, property);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static double? ParseFrameRate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "0/0")
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
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
            if (await Task.WhenAny(exitTask, Task.Delay(timeout)) != exitTask)
            {
                return false;
            }

            await exitTask;
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
        if (await Task.WhenAny(readers, Task.Delay(timeout)) != readers)
        {
            return false;
        }

        await readers;
        return true;
    }

    private static string TrimError(string error) => string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim()[..Math.Min(error.Trim().Length, 1_000)];
}
