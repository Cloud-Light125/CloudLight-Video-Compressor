using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record VmafCapability(bool IsAvailable, string Message, DateTimeOffset CheckedAt);

public sealed class VmafCapabilityService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, VmafCapability> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VmafCapability> DetectAsync(FFmpegTools tools, CancellationToken cancellationToken)
    {
        var cacheKey = tools.FFmpegPath;
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tools.FFmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-filters" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            using var tracked = MediaProcessRegistry.Register(process);
            using var registration = timeout.Token.Register(() => MediaProcessRegistry.TryTerminate(process));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
            var available = process.ExitCode == 0 && Regex.IsMatch(text, @"(?m)^\s*\.\.\s+libvmaf\s+", RegexOptions.IgnoreCase);
            var result = new VmafCapability(
                available,
                available ? "当前 FFmpeg 支持 libvmaf。" : "当前 FFmpeg 不支持 VMAF 质量校准。",
                DateTimeOffset.UtcNow);
            _cache[cacheKey] = result;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MediaProcessRegistry.TryTerminate(process);
            var result = new VmafCapability(false, $"无法检测 VMAF：{exception.Message}", DateTimeOffset.UtcNow);
            _cache[cacheKey] = result;
            return result;
        }
    }
}

public static class VmafSampleSelector
{
    public const int SamplingSchemaVersion = 2;

    public static IReadOnlyList<VmafSample> Select(
        double durationSeconds,
        int sampleDurationSeconds = 8,
        int maximumSamples = 3)
    {
        if (durationSeconds <= 0 || sampleDurationSeconds <= 0 || maximumSamples <= 0)
        {
            return [];
        }

        var length = Math.Min(sampleDurationSeconds, Math.Max(1, durationSeconds));
        if (durationSeconds <= length * 1.5 || maximumSamples == 1)
        {
            return [new VmafSample(0, length)];
        }

        double[] starts = maximumSamples == 2
            ? new[] { 0d, Math.Max(0, durationSeconds - length) }
            : new[] { 0d, Math.Max(0, durationSeconds / 2d - length / 2d), Math.Max(0, durationSeconds - length) };
        return starts
            .Distinct()
            .Select(start => new VmafSample(Math.Round(start, 3), Math.Min(length, durationSeconds - start)))
            .Where(sample => sample.DurationSeconds > 0.5)
            .ToArray();
    }

    /// <summary>
    /// Selects representative windows from a bounded complexity signal. The
    /// selector prefers low/middle/high-complexity content over fixed
    /// start/middle/end positions and ignores non-informative windows.
    /// </summary>
    public static IReadOnlyList<VmafSample> SelectComplexityAware(
        double durationSeconds,
        int sampleDurationSeconds,
        int maximumSamples,
        IReadOnlyList<VmafComplexitySignal>? signals)
    {
        var fallback = Select(durationSeconds, sampleDurationSeconds, maximumSamples);
        if (signals is null || signals.Count == 0)
        {
            return fallback;
        }

        var length = Math.Min(sampleDurationSeconds, Math.Max(1, durationSeconds));
        var usable = signals
            .Where(signal => signal.IsInformative &&
                             signal.DurationSeconds > 0.5 &&
                             signal.StartSeconds >= 0 &&
                             signal.StartSeconds < durationSeconds &&
                             double.IsFinite(signal.Score))
            .OrderBy(signal => signal.StartSeconds)
            .ToArray();
        if (usable.Length == 0)
        {
            return fallback;
        }

        var interior = usable
            .Where(signal => signal.StartSeconds >= length * 0.25 &&
                             signal.StartSeconds + signal.DurationSeconds <= durationSeconds - length * 0.25)
            .ToArray();
        var pool = interior.Length >= Math.Min(maximumSamples, 2) ? interior : usable;
        var ordered = pool.OrderBy(signal => signal.Score).ToArray();
        var targetPercentiles = maximumSamples switch
        {
            1 => new[] { 0.50 },
            2 => new[] { 0.25, 0.75 },
            _ => new[] { 0.15, 0.50, 0.85 }
        };
        var selected = new List<VmafSample>();
        foreach (var percentile in targetPercentiles)
        {
            var targetIndex = (int)Math.Round((ordered.Length - 1) * percentile);
            var candidate = ordered
                .Select((signal, index) => (signal, index))
                .OrderBy(item => Math.Abs(item.index - targetIndex))
                .ThenBy(item => item.signal.StartSeconds)
                .FirstOrDefault(item => selected.All(existing =>
                    Math.Abs(existing.StartSeconds - item.signal.StartSeconds) >= length * 0.45))
                .signal;
            if (candidate is null)
            {
                continue;
            }

            var rank = Array.IndexOf(ordered, candidate);
            var complexity = rank < (ordered.Length - 1) / 3d
                ? VmafComplexityClass.Low
                : rank > (ordered.Length - 1) * 2d / 3d
                    ? VmafComplexityClass.High
                    : VmafComplexityClass.Medium;
            selected.Add(new VmafSample(
                Math.Round(Math.Clamp(candidate.StartSeconds, 0, Math.Max(0, durationSeconds - length)), 3),
                Math.Min(length, durationSeconds - candidate.StartSeconds),
                complexity,
                candidate.Score,
                candidate.Signal));
        }

        return selected.Count == 0
            ? fallback
            : selected
                .Where(sample => sample.DurationSeconds > 0.5)
                .OrderBy(sample => sample.StartSeconds)
                .ToArray();
    }
}

/// <summary>
/// Best-effort, bounded FFmpeg analysis used only to improve VMAF sample
/// placement. Failure intentionally falls back to VmafSampleSelector.Select.
/// </summary>
public sealed class VmafComplexityAnalyzer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaximumOutputCharacters = 256_000;

    public async Task<IReadOnlyList<VmafComplexitySignal>> AnalyzeAsync(
        VideoFileInfo source,
        FFmpegTools tools,
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
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(source.FullPath);
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add("fps=1,scale=320:-2:force_original_aspect_ratio=decrease,signalstats,metadata=print:file=-");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("NUL");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            using var tracked = MediaProcessRegistry.Register(process);
            using var registration = timeout.Token.Register(() => MediaProcessRegistry.TryTerminate(process));
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return [];
            }

            var standardOutput = await stdout.ConfigureAwait(false);
            var standardError = await stderr.ConfigureAwait(false);
            return ParseSignals(standardOutput + Environment.NewLine + standardError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                            System.ComponentModel.Win32Exception or TimeoutException)
        {
            MediaProcessRegistry.TryTerminate(process);
            DiagnosticLog.Write("vmaf", $"复杂度分析失败，使用固定抽样：{exception.Message}");
            return [];
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return builder.ToString();
            }

            if (builder.Length < MaximumOutputCharacters)
            {
                var remaining = MaximumOutputCharacters - builder.Length;
                builder.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                if (builder.Length < MaximumOutputCharacters)
                {
                    builder.AppendLine();
                }
            }
        }
    }

    private static IReadOnlyList<VmafComplexitySignal> ParseSignals(string text)
    {
        var signals = new List<VmafComplexitySignal>();
        var currentTime = 0d;
        var currentYMax = 255d;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var time = Regex.Match(line, @"pts_time[:=](?<value>-?[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (time.Success &&
                double.TryParse(time.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTime))
            {
                currentTime = Math.Max(0, parsedTime);
            }

            var yMax = Regex.Match(line, @"(?:YMAX|lavfi\.signalstats\.YMAX)=(?<value>[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (yMax.Success &&
                double.TryParse(yMax.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedYMax))
            {
                currentYMax = parsedYMax;
            }

            var yDif = Regex.Match(line, @"(?:YDIF|lavfi\.signalstats\.YDIF)=(?<value>[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
            if (yDif.Success &&
                double.TryParse(yDif.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                signals.Add(new VmafComplexitySignal(
                    currentTime,
                    1,
                    score,
                    "signalstats.YDIF",
                    currentYMax > 16));
            }
        }

        return signals;
    }
}

public sealed class VmafCalibrationCache
{
    private readonly ConcurrentDictionary<string, VmafCalibrationResult> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out VmafCalibrationResult result) => _entries.TryGetValue(key, out result!);

    public void Set(string key, VmafCalibrationResult result) => _entries[key] = result;
}

/// <summary>
/// Optional bounded quality search. It samples only representative segments,
/// never the entire source, and keeps the result in a cache keyed by the
/// source fingerprint and plan inputs.
/// </summary>
public sealed class VmafQualityCalibrationService
{
    public const int CurrentSamplingSchemaVersion = VmafSampleSelector.SamplingSchemaVersion;
    private static readonly SemaphoreSlim CalibrationGate = new(1, 1);
    private readonly VmafCapabilityService _capabilityService;
    private readonly VmafCalibrationCache _cache;
    private readonly VmafComplexityAnalyzer _complexityAnalyzer;

    public VmafQualityCalibrationService(
        VmafCapabilityService? capabilityService = null,
        VmafCalibrationCache? cache = null,
        VmafComplexityAnalyzer? complexityAnalyzer = null)
    {
        _capabilityService = capabilityService ?? new VmafCapabilityService();
        _cache = cache ?? new VmafCalibrationCache();
        _complexityAnalyzer = complexityAnalyzer ?? new VmafComplexityAnalyzer();
    }

    public async Task<VmafCalibrationResult> CalibrateAsync(
        VideoFileInfo source,
        AppSettings settings,
        VideoEncoder encoder,
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        await CalibrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CalibrateCoreAsync(source, settings, encoder, tools, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CalibrationGate.Release();
        }
    }

    private async Task<VmafCalibrationResult> CalibrateCoreAsync(
        VideoFileInfo source,
        AppSettings settings,
        VideoEncoder encoder,
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var capability = await _capabilityService.DetectAsync(tools, cancellationToken).ConfigureAwait(false);
        if (!capability.IsAvailable)
        {
            return VmafCalibrationResult.Unavailable(capability.Message);
        }
        if (source.DurationSeconds is not > 0)
        {
            return VmafCalibrationResult.Unavailable("缺少源文件时长，无法进行 VMAF 抽样。 ");
        }

        var key = BuildCacheKey(source, settings, encoder);
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var bitDepthDecision = BitDepthPolicyResolver.Resolve(source, settings.BitDepthPolicy, encoder);

        IReadOnlyList<VmafComplexitySignal> complexitySignals;
        try
        {
            complexitySignals = await _complexityAnalyzer.AnalyzeAsync(source, tools, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("vmaf", $"复杂度分析异常，使用固定抽样：{exception.Message}");
            complexitySignals = [];
        }

        var samples = VmafSampleSelector.SelectComplexityAware(
            source.DurationSeconds.Value,
            settings.QualityCalibrationSampleSeconds,
            Math.Min(3, settings.QualityCalibrationCandidateCount),
            complexitySignals);
        if (samples.Count == 0)
        {
            return VmafCalibrationResult.Unavailable("没有可用的代表性片段。 ");
        }

        var qualities = CandidateQualities(settings.QualityCalibrationCandidateCount);
        var measurements = new List<VmafMeasurement>();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $".clvc-vmaf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var quality in qualities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var sample in samples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidatePath = Path.Combine(temporaryDirectory, $"candidate-{quality:0}-{sample.StartSeconds:0.###}.mp4");
                    var logPath = Path.Combine(temporaryDirectory, $"vmaf-{quality:0}-{sample.StartSeconds:0.###}.json");
                    var encoded = await EncodeSampleAsync(
                        source,
                        encoder,
                        quality,
                        sample,
                        bitDepthDecision,
                        candidatePath,
                        settings.EncoderTuningPreset,
                        tools,
                        cancellationToken).ConfigureAwait(false);
                    if (!encoded)
                    {
                        continue;
                    }

                    var score = await MeasureVmafAsync(source.FullPath, candidatePath, sample, logPath, tools, cancellationToken).ConfigureAwait(false);
                    if (score is null)
                    {
                        continue;
                    }

                    var encodedBytes = File.Exists(candidatePath) ? (long?)new FileInfo(candidatePath).Length : null;
                    measurements.Add(new VmafMeasurement(sample, encoder, quality, score.Value, encodedBytes));
                }
            }

            if (measurements.Count == 0)
            {
                return VmafCalibrationResult.Unavailable("VMAF 抽样编码或测量失败，已回退到普通规划。 ");
            }

            var target = ProfileVmafTarget(settings);
            var grouped = measurements
                .GroupBy(measurement => measurement.Quality)
                .Select(group => new
                {
                    Quality = group.Key,
                    Score = group.Average(measurement => measurement.Score),
                    BytesPerSecond = group
                        .Where(measurement => measurement.EncodedBytes is not null)
                        .Select(measurement => measurement.EncodedBytes!.Value * 8d / measurement.Sample.DurationSeconds)
                        .DefaultIfEmpty()
                        .Average()
                })
                .OrderBy(item => item.Quality)
                .ToArray();
            // Higher CRF/CQ usually means fewer bits. Select the most
            // compressed tested quality that still clears the requested target.
            var selected = grouped.Where(item => item.Score >= target).OrderByDescending(item => item.Quality).FirstOrDefault();
            var result = new VmafCalibrationResult(
                true,
                selected is null
                    ? $"VMAF 抽样完成，但没有候选参数达到目标 {target:0.0}；已使用最接近的候选。"
                    : $"VMAF 抽样完成：目标 {target:0.0}，选择质量参数 {selected.Quality:0.##}。",
                samples,
                measurements,
                selected?.Quality ?? grouped.OrderByDescending(item => item.Score).First().Quality,
                selected is { BytesPerSecond: > 0 } item ? (long)Math.Round(item.BytesPerSecond) : null,
                DateTimeOffset.UtcNow);
            _cache.Set(key, result);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                DiagnosticLog.Write("vmaf", $"无法清理临时目录：{temporaryDirectory}");
            }
            catch (UnauthorizedAccessException)
            {
                DiagnosticLog.Write("vmaf", $"无权清理临时目录：{temporaryDirectory}");
            }
        }
    }

    private static IReadOnlyList<double> CandidateQualities(int count)
    {
        var candidates = new List<double> { 18, 24, 30 };
        if (count >= 4)
        {
            candidates.Add(27);
        }
        if (count >= 5)
        {
            candidates.Add(33);
        }
        return candidates.OrderBy(value => value).Take(Math.Clamp(count, 3, 5)).ToArray();
    }

    private static async Task<bool> EncodeSampleAsync(
        VideoFileInfo source,
        VideoEncoder encoder,
        double quality,
        VmafSample sample,
        BitDepthDecision bitDepthDecision,
        string outputPath,
        EncoderTuningPreset tuningPreset,
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var plan = new CompressionPlan(
            false,
            encoder,
            CompressionMode.Crf,
            quality,
            EncoderTuningCatalog.Resolve(encoder, tuningPreset),
            null,
            null,
            null,
            AudioMode.Copy,
            192,
            ".mp4",
            [])
        {
            InputInfo = source,
            EncoderTuningPreset = tuningPreset,
            BitDepthPolicy = bitDepthDecision.Policy,
            TargetBitDepth = bitDepthDecision.TargetBitDepth,
            TargetPixelFormat = bitDepthDecision.TargetPixelFormat,
            TargetProfile = bitDepthDecision.TargetProfile,
            BitDepthDecision = bitDepthDecision
        };
        var arguments = plan.BuildArguments(source.FullPath, outputPath, false).ToList();
        arguments.InsertRange(3, ["-ss", sample.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-t", sample.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        var run = await RunProcessAsync(tools.FFmpegPath, arguments, cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1_024;
    }

    private static async Task<double?> MeasureVmafAsync(
        string sourcePath,
        string candidatePath,
        VmafSample sample,
        string logPath,
        FFmpegTools tools,
        CancellationToken cancellationToken)
    {
        var filter = "[0:v]trim=start=0:end=" + sample.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ",setpts=PTS-STARTPTS[ref];" +
                     "[1:v]setpts=PTS-STARTPTS[dist];[dist][ref]libvmaf=log_fmt=json:log_path=" + EscapeFilterPath(Path.GetFileName(logPath));
        var arguments = new[]
        {
            "-hide_banner", "-y", "-ss", sample.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", sourcePath,
            "-i", candidatePath, "-filter_complex", filter, "-an", "-f", "null", "NUL"
        };
        var run = await RunProcessAsync(
            tools.FFmpegPath,
            arguments,
            cancellationToken,
            Path.GetDirectoryName(logPath)).ConfigureAwait(false);
        if (run.ExitCode != 0 || !File.Exists(logPath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(logPath, cancellationToken).ConfigureAwait(false);
        var matches = Regex.Matches(text, @"""mean""\s*:\s*(?<score>[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        var values = matches
            .Select(match => double.TryParse(match.Groups["score"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ? score : double.NaN)
            .Where(score => !double.IsNaN(score))
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static async Task<(int ExitCode, string Error)> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var tracked = MediaProcessRegistry.Register(process);
        using var registration = cancellationToken.Register(() => MediaProcessRegistry.TryTerminate(process));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MediaProcessRegistry.TryTerminate(process);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return (process.ExitCode, error.Length > 2_000 ? error[^2_000..] : error);
    }

    private static string BuildCacheKey(VideoFileInfo source, AppSettings settings, VideoEncoder encoder)
    {
        var lastWrite = File.Exists(source.FullPath) ? File.GetLastWriteTimeUtc(source.FullPath).Ticks : 0;
        return string.Join('|', source.FullPath, source.FileSizeBytes, lastWrite, encoder, settings.CompressionProfile,
            settings.VmafTarget, settings.QualityCalibrationSampleSeconds, settings.QualityCalibrationCandidateCount,
            CurrentSamplingSchemaVersion, settings.EncoderTuningPreset, settings.BitDepthPolicy);
    }

    private static double ProfileVmafTarget(AppSettings settings) => settings.CompressionProfile switch
    {
        CompressionProfile.HighQuality => 96.5,
        CompressionProfile.SpaceSaving => 93,
        CompressionProfile.RemotePlayback => 94,
        _ => settings.VmafTarget
    };

    private static string EscapeFilterPath(string path) => path.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
}
