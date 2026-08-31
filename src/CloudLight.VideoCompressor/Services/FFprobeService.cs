using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class FFprobeService
{
    private static readonly TimeSpan CancellationExitTimeout = TimeSpan.FromSeconds(8);
    private readonly ConcurrentDictionary<string, Task<string?>> _toolVersionCache = new(StringComparer.OrdinalIgnoreCase);

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
        startInfo.ArgumentList.Add("-show_chapters");
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
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("ffprobe 已因取消请求终止。", cancellationToken);
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

    public Task<string?> GetToolVersionAsync(FFmpegTools tools, CancellationToken cancellationToken)
    {
        var key = tools.FFprobePath;
        try
        {
            var file = new FileInfo(tools.FFprobePath);
            key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("ffprobe", $"无法读取 ffprobe 文件身份：{exception.Message}");
        }

        var task = _toolVersionCache.GetOrAdd(key, _ => GetToolVersionCoreAsync(tools, CancellationToken.None));
        return task.WaitAsync(cancellationToken);
    }

    private async Task<string?> GetToolVersionCoreAsync(FFmpegTools tools, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-version");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var trackedProcess = MediaProcessRegistry.Register(process);
        using var registration = timeout.Token.Register(() => TryKill(process));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitWithinAsync(process, CancellationExitTimeout).ConfigureAwait(false);
            await WaitForReaderTasksWithinAsync(outputTask, errorTask, CancellationExitTimeout).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("读取 ffprobe 版本超时。");
            }

            throw;
        }

        if (!await WaitForReaderTasksWithinAsync(outputTask, errorTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            throw new IOException("ffprobe 已退出，但版本信息读取未在时限内结束。");
        }

        var text = (await outputTask.ConfigureAwait(false)) + Environment.NewLine + (await errorTask.ConfigureAwait(false));
        return ExtractToolVersion(text);
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
        var video = streams.FirstOrDefault(stream =>
            GetString(stream, "codec_type") == "video" && !IsAttachedPicture(stream));
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video");
        }
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

        var mediaStreams = streams.Select(ParseStreamInfo).ToArray();
        var formatMetadata = GetMetadata(format, "tags");
        var chapterCount = root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array
            ? chapters.GetArrayLength()
            : 0;
        var primaryVideoIndex = mediaStreams
            .FirstOrDefault(stream => stream.StreamType == MediaStreamType.Video && !stream.IsAttachedPicture)
            ?.StreamIndex;

        return new VideoFileInfo
        {
            FileName = file.Name,
            FullPath = file.FullName,
            Extension = file.Extension,
            FileSizeBytes = file.Exists ? file.Length : 0,
            LastWriteTimeUtc = file.Exists ? file.LastWriteTimeUtc : default,
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
            SubtitleCodecs = subtitleCodecs,
            SubtitleTrackCount = mediaStreams.Count(stream => stream.StreamType == MediaStreamType.Subtitle),
             ChapterCount = chapterCount,
             Container = GetString(format, "format_name")?.Split(',')[0],
             PixelFormat = GetString(video, "pix_fmt"),
             BitDepth = GetInt(video, "bits_per_raw_sample") ??
                        GetInt(video, "bits_per_coded_sample") ??
                        BitDepthPolicyResolver.DetectPixelFormatBitDepth(GetString(video, "pix_fmt")),
             VideoProfile = GetString(video, "profile"),
             ColorPrimaries = GetString(video, "color_primaries"),
             ColorTransfer = GetString(video, "color_transfer"),
             ColorSpace = GetString(video, "colorspace"),
             ColorRange = GetString(video, "color_range"),
             MasteringDisplayMetadata = GetSideDataMetadata(video, "mastering display"),
             ContentLightMetadata = GetSideDataMetadata(video, "content light"),
             PrimaryVideoStreamIndex = primaryVideoIndex,
            Streams = mediaStreams,
            Metadata = formatMetadata
        };
    }

    private static MediaStreamInfo ParseStreamInfo(JsonElement stream)
    {
        var type = GetString(stream, "codec_type")?.ToLowerInvariant() switch
        {
            "video" => MediaStreamType.Video,
            "audio" => MediaStreamType.Audio,
            "subtitle" => MediaStreamType.Subtitle,
            "attachment" => MediaStreamType.Attachment,
            "data" => MediaStreamType.Data,
            _ => MediaStreamType.Unknown
        };
        var disposition = GetIntDictionary(stream, "disposition");
        var metadata = GetMetadata(stream, "tags");
        metadata.TryGetValue("language", out var language);
        metadata.TryGetValue("title", out var title);
        return new MediaStreamInfo(
            GetInt(stream, "index") ?? -1,
            type,
            GetString(stream, "codec_name"),
            language,
            title,
            disposition.TryGetValue("default", out var isDefault) && isDefault != 0,
            disposition.TryGetValue("forced", out var isForced) && isForced != 0,
            disposition,
            GetLong(stream, "bit_rate"),
            GetInt(stream, "channels"),
             GetInt(stream, "sample_rate"),
             GetString(stream, "pix_fmt"),
             GetInt(stream, "bits_per_raw_sample") ??
             GetInt(stream, "bits_per_coded_sample") ??
             BitDepthPolicyResolver.DetectPixelFormatBitDepth(GetString(stream, "pix_fmt")),
             metadata,
             GetString(stream, "profile"),
             GetString(stream, "color_primaries"),
             GetString(stream, "color_transfer"),
             GetString(stream, "colorspace"),
             GetString(stream, "color_range"),
             GetSideDataMetadata(stream, "mastering display"),
             GetSideDataMetadata(stream, "content light"));
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        if (!stream.TryGetProperty("disposition", out var disposition) || disposition.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return GetInt(disposition, "attached_pic") is > 0;
    }

    private static IReadOnlyDictionary<string, int> GetIntDictionary(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateObject())
        {
            var number = item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var integer)
                ? integer
                : int.TryParse(item.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
                    ? integer
                    : 0;
            result[item.Name] = number;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> GetMetadata(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return value.EnumerateObject()
            .Where(item => item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .ToDictionary(item => item.Name, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> GetSideDataMetadata(JsonElement stream, string typeFragment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!stream.TryGetProperty("side_data_list", out var sideData) ||
            sideData.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in sideData.EnumerateArray())
        {
            var type = GetString(item, "side_data_type");
            if (string.IsNullOrWhiteSpace(type) ||
                !type.Contains(typeFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or
                    JsonValueKind.True or JsonValueKind.False)
                {
                    result[property.Name] = property.Value.ToString();
                }
            }
        }

        return result;
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

        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out parsed)
                ? parsed
                : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : null
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
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed)
            ? parsed
            : null;
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

    private static string? ExtractToolVersion(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .FirstOrDefault(item => item.StartsWith("ffprobe version ", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return null;
        }

        var version = line["ffprobe version ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        // Keep the distribution/build suffix as part of the identity. A
        // patched vendor build can change probing behavior while retaining
        // the same upstream semantic version.
        return string.IsNullOrWhiteSpace(version) ? null : version.TrimStart('n', 'N');
    }
}
