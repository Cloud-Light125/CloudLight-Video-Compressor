using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record EncoderHelpProbeResult(
    bool Succeeded,
    IReadOnlyList<string> SupportedPixelFormats,
    IReadOnlyList<string> SupportedPresets,
    IReadOnlyList<int> SupportedBitDepths,
    IReadOnlyList<string> SupportedProfiles,
    string? Error = null);

public sealed class EncoderHelpProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<EncoderHelpProbeResult> ProbeAsync(
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
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add($"encoder={CompressionPlan.FfmpegEncoderName(encoder)}");

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(Timeout);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var tracked = MediaProcessRegistry.Register(process);
        using var registration = timeoutCancellation.Token.Register(() => MediaProcessRegistry.TryTerminate(process));
        var outputTask = ReadBoundedAsync(process.StandardOutput, timeoutCancellation.Token);
        var errorTask = ReadBoundedAsync(process.StandardError, timeoutCancellation.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            MediaProcessRegistry.TryTerminate(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Failed("读取 encoder help 超时。 ");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return Failed(Trim(error));
        }

        return EncoderHelpParser.Parse(encoder, output + Environment.NewLine + error);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 128_000;
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

    private static EncoderHelpProbeResult Failed(string error) =>
        new(false, [], [], [], [], error);

    private static string Trim(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "未返回错误文本。" : value.Trim();
        return text.Length <= 1_000 ? text : text[..1_000];
    }
}

public static class EncoderHelpParser
{
    public static EncoderHelpProbeResult Parse(VideoEncoder encoder, string text)
    {
        var formats = ParsePixelFormats(text);
        if (formats.Count == 0)
        {
            // An encoder help response without a pixel-format section is not
            // enough evidence to claim 8-bit support. The detector may still
            // use the strategy defaults for a smoke-tested encoder, but the
            // parser itself must not manufacture a bit-depth capability.
            return new EncoderHelpProbeResult(
                true,
                formats,
                ParsePresets(encoder, text),
                [],
                ParseProfiles(encoder, text, []));
        }

        var presets = ParsePresets(encoder, text);
        var bitDepths = formats
            .Select(BitDepthPolicyResolver.DetectPixelFormatBitDepth)
            .Where(depth => depth is > 0)
            .Select(depth => depth!.Value >= 10 ? 10 : 8)
            .Distinct()
            .Order()
            .ToArray();

        var profiles = ParseProfiles(encoder, text, bitDepths);
        return new EncoderHelpProbeResult(
            true,
            formats,
            presets,
            bitDepths,
            profiles);
    }

    private static IReadOnlyList<string> ParsePixelFormats(string text)
    {
        var match = Regex.Match(
            text,
            @"(?im)^\s*Supported pixel formats:\s*(?<formats>[^\r\n]*)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return [];
        }

        return Regex.Matches(match.Groups["formats"].Value, @"(?<![\w.])[a-z][a-z0-9_]*(?=\s|$)", RegexOptions.IgnoreCase)
            .Select(item => item.Value.Trim())
            .Where(item => item.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ParsePresets(VideoEncoder encoder, string text)
    {
        var strategyPresets = EncoderStrategyCatalog.Get(encoder).SupportedPresets;
        return encoder switch
        {
            VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc =>
                strategyPresets.Where(preset => Regex.IsMatch(text, $@"(?m)^\s*{Regex.Escape(preset)}\s+", RegexOptions.IgnoreCase)).ToArray()
                    is { Length: > 0 } detected
                    ? detected
                    : strategyPresets,
            VideoEncoder.H264Amf or VideoEncoder.HevcAmf =>
                strategyPresets.Where(preset => text.Contains(preset, StringComparison.OrdinalIgnoreCase)).ToArray()
                    is { Length: > 0 } amfDetected
                    ? amfDetected
                    : strategyPresets,
            _ => strategyPresets
        };
    }

    private static IReadOnlyList<string> ParseProfiles(
        VideoEncoder encoder,
        string text,
        IReadOnlyList<int> bitDepths)
    {
        var definition = EncoderCatalog.Get(encoder);
        var profiles = new List<string>();
        if (definition.Codec == VideoCodecKind.H265)
        {
            profiles.Add("main");
            if (bitDepths.Contains(10))
            {
                profiles.Add("main10");
            }
        }
        else if (definition.Codec == VideoCodecKind.H264)
        {
            profiles.Add("high");
            if (bitDepths.Contains(10))
            {
                profiles.Add("high10");
            }
        }

        if (text.Contains("profile", StringComparison.OrdinalIgnoreCase) && profiles.Count == 0)
        {
            profiles.Add("unknown");
        }

        return profiles;
    }
}
