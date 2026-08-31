using System.Globalization;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed record AutoEncoderSelectionRequest(
    VideoCodecKind RequestedCodec,
    CompressionProfile Profile,
    EncoderTuningPreset TuningPreset,
    EncoderCapabilitySet? Capabilities = null,
    EncoderBenchmarkSnapshot? Benchmark = null,
    int? VideoWidth = null,
    int? VideoHeight = null,
    double? Fps = null,
    string? SourceCodec = null,
    int TargetBitDepth = 8,
    long? ExpectedOutputBitrate = null,
    bool HardwareOnly = false,
    PerformanceMode PerformanceMode = PerformanceMode.Automatic);

public sealed record AutoEncoderDecision(
    VideoEncoder SelectedEncoder,
    string Reason,
    BenchmarkConfidence Confidence,
    IReadOnlyList<VideoEncoder> FallbackChain,
    IReadOnlyDictionary<VideoEncoder, double> Scores,
    IReadOnlyDictionary<VideoEncoder, double?> BenchmarkSpeeds,
    VideoCodecKind TargetCodec,
    int TargetBitDepth,
    bool UsedBenchmark)
{
    public string ConfidenceDisplay => Confidence.GetDescription();
    public double SelectedScore => Scores.TryGetValue(SelectedEncoder, out var score) ? score : 0;
}

/// <summary>
/// Explainable Auto encoder selection. It scores only encoders that passed the
/// current capability probe, then uses a local benchmark as a speed signal.
/// The service never changes the requested codec or bit depth.
/// </summary>
public sealed class AutoEncoderSelectionService
{
    public AutoEncoderDecision Select(AutoEncoderSelectionRequest request)
    {
        var candidates = GetCandidates(request).ToArray();
        var usedBenchmark = request.Benchmark?.Results.Any(result => result.Success) == true;
        var confidence = request.Benchmark is null
            ? BenchmarkConfidence.Unknown
            : request.Capabilities is not null
                ? request.Benchmark.ConfidenceFor(MachineFingerprintService.Create(request.Capabilities))
                : usedBenchmark ? BenchmarkConfidence.Low : BenchmarkConfidence.Unknown;

        if (candidates.Length == 0)
        {
            var software = EncoderSelectionResolver.SoftwareEncoder(request.RequestedCodec);
            return new AutoEncoderDecision(
                software,
                $"没有通过当前能力检测的 {request.RequestedCodec.GetDescription()} {request.TargetBitDepth}-bit 编码器，已保留 Codec/BitDepth 并阻止自动切换到其他格式。",
                BenchmarkConfidence.Unknown,
                [],
                new Dictionary<VideoEncoder, double> { [software] = 0 },
                new Dictionary<VideoEncoder, double?> { [software] = null },
                request.RequestedCodec,
                request.TargetBitDepth,
                false);
        }

        var scored = candidates
            .Select(encoder => Score(encoder, request))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Encoder)
            .ToArray();
        var selected = scored[0];
        var fallback = scored.Skip(1).Select(item => item.Encoder).ToArray();
        var speeds = scored.ToDictionary(item => item.Encoder, item => item.BenchmarkSpeed);
        var scores = scored.ToDictionary(item => item.Encoder, item => item.Score);
        var reason = BuildReason(request, selected, scored, confidence, usedBenchmark);

        return new AutoEncoderDecision(
            selected.Encoder,
            reason,
            confidence,
            fallback,
            scores,
            speeds,
            request.RequestedCodec,
            request.TargetBitDepth,
            usedBenchmark);
    }

    private static IEnumerable<VideoEncoder> GetCandidates(AutoEncoderSelectionRequest request)
    {
        foreach (var definition in EncoderCatalog.Definitions.Where(definition =>
                     definition.Codec == request.RequestedCodec &&
                     (!request.HardwareOnly || definition.IsHardware)))
        {
            var supported = request.Capabilities?.IsUsable(definition.Encoder, request.TargetBitDepth)
                ?? (!definition.IsHardware && EncoderStrategyCatalog.Get(definition.Encoder).SupportsBitDepth(request.TargetBitDepth));
            if (supported)
            {
                yield return definition.Encoder;
            }
        }
    }

    private static EncoderScore Score(VideoEncoder encoder, AutoEncoderSelectionRequest request)
    {
        var definition = EncoderCatalog.Get(encoder);
        var speed = FindBenchmarkSpeed(encoder, request);
        var heuristicSpeed = HeuristicSpeed(encoder, request);
        var benchmarkInfluence = BenchmarkInfluence(request);
        var effectiveSpeed = speed is { } measuredSpeed
            ? measuredSpeed * benchmarkInfluence + heuristicSpeed * (1 - benchmarkInfluence)
            : heuristicSpeed;
        var maxSpeed = request.Benchmark?.Results
            .Where(result => result.Success && MatchesWorkload(result, request))
            .Select(result => result.AverageSpeed)
            .Where(value => value > 0)
            .DefaultIfEmpty()
            .Max() ?? 0;
        if (maxSpeed <= 0)
        {
            maxSpeed = request.VideoWidth >= 3840 || request.Fps >= 60 ? 2.4 : 2.0;
        }

        var speedScore = Math.Clamp(effectiveSpeed / maxSpeed * 100, 0, 100);
        var highResolution = request.VideoWidth >= 3840 || request.Fps >= 60;
        var requiredSpeed = highResolution ? 0.7 : 0.5;
        var suitability = Math.Clamp(effectiveSpeed / requiredSpeed * 100, 0, 100);
        var efficiency = EncoderEfficiency(encoder, request.RequestedCodec);
        if (request.TuningPreset == EncoderTuningPreset.HighQuality)
        {
            efficiency += definition.IsHardware ? 0 : 5;
        }
        else if (request.TuningPreset is EncoderTuningPreset.Fast or EncoderTuningPreset.VeryFast)
        {
            efficiency += definition.IsHardware ? 3 : -2;
        }

        // A very slow software candidate can still have the best compression
        // efficiency on paper, but it is not a practical 4K60 choice. Keep a
        // smooth penalty instead of an absolute encoder rule so a sufficiently
        // fast CPU remains eligible for high-quality work.
        if (highResolution && !definition.IsHardware && effectiveSpeed < requiredSpeed)
        {
            var viability = Math.Clamp(effectiveSpeed / requiredSpeed, 0.15, 1);
            efficiency *= 0.55 + 0.45 * viability;
        }

        var (efficiencyWeight, speedWeight, suitabilityWeight) = PreferenceWeights(request);
        if (request.PerformanceMode == PerformanceMode.LowEndStable && definition.IsHardware)
        {
            suitability = Math.Min(100, suitability + 8);
        }

        var score = Math.Clamp(
            efficiency * efficiencyWeight +
            speedScore * speedWeight +
            suitability * suitabilityWeight,
            0,
            100);
        return new EncoderScore(encoder, score, speed, effectiveSpeed, speedScore, suitability, efficiency);
    }

    private static (double Efficiency, double Speed, double Suitability) PreferenceWeights(
        AutoEncoderSelectionRequest request)
    {
        if (request.PerformanceMode == PerformanceMode.SpeedPriority)
        {
            return (0.20, 0.65, 0.15);
        }

        var preference = request.Profile switch
        {
            CompressionProfile.HighQuality or CompressionProfile.SpaceSaving => SpeedVsEfficiencyPreference.Efficiency,
            CompressionProfile.RemotePlayback => SpeedVsEfficiencyPreference.Balanced,
            _ => SpeedVsEfficiencyPreference.Balanced
        };
        return request.TuningPreset switch
        {
            EncoderTuningPreset.HighQuality when preference == SpeedVsEfficiencyPreference.Efficiency => (0.75, 0.15, 0.10),
            EncoderTuningPreset.VeryFast => (0.15, 0.70, 0.15),
            EncoderTuningPreset.Fast => (0.25, 0.60, 0.15),
            _ when preference == SpeedVsEfficiencyPreference.Speed => (0.20, 0.65, 0.15),
            _ when preference == SpeedVsEfficiencyPreference.Efficiency => (0.60, 0.25, 0.15),
            _ => (0.40, 0.45, 0.15)
        };
    }

    private static double? FindBenchmarkSpeed(VideoEncoder encoder, AutoEncoderSelectionRequest request)
    {
        if (request.Benchmark is null)
        {
            return null;
        }

        return request.Benchmark.Results
            .Where(result => result.Success &&
                             (result.Encoder == encoder ||
                              string.Equals(result.EncoderId, CompressionPlan.FfmpegEncoderName(encoder), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(result => WorkloadDistance(result, request))
            .Select(result => result.AverageSpeed > 0 ? result.AverageSpeed : result.AverageFps / Math.Max(1, result.Fps))
            .FirstOrDefault();
    }

    private static double BenchmarkInfluence(AutoEncoderSelectionRequest request)
    {
        if (request.Benchmark is null || !request.Benchmark.Results.Any(result => result.Success))
        {
            return 0;
        }

        if (request.Capabilities is not null &&
            request.Benchmark.ConfidenceFor(MachineFingerprintService.Create(request.Capabilities)) == BenchmarkConfidence.High)
        {
            return 1;
        }

        // A stale snapshot remains useful as a reference, but it must not
        // outweigh the conservative local heuristic.
        return 0.5;
    }

    private static bool MatchesWorkload(EncoderBenchmarkResult result, AutoEncoderSelectionRequest request) =>
        result.Codec == request.RequestedCodec &&
        (request.VideoWidth is null || Math.Abs(result.Width - request.VideoWidth.Value) <= Math.Max(64, request.VideoWidth.Value * 0.25)) &&
        (request.Fps is null || Math.Abs(result.Fps - request.Fps.Value) <= 15);

    private static double WorkloadDistance(EncoderBenchmarkResult result, AutoEncoderSelectionRequest request)
    {
        var pixelDistance = request.VideoWidth is > 0 && request.VideoHeight is > 0
            ? Math.Abs(Math.Log(Math.Max(1, result.Width * (double)result.Height) /
                                (request.VideoWidth.Value * (double)request.VideoHeight.Value)))
            : 0;
        var fpsDistance = request.Fps is > 0 ? Math.Abs(result.Fps - request.Fps.Value) / request.Fps.Value : 0;
        var codecDistance = result.Codec == request.RequestedCodec ? 0 : 10;
        return pixelDistance + fpsDistance + codecDistance;
    }

    private static double HeuristicSpeed(VideoEncoder encoder, AutoEncoderSelectionRequest request)
    {
        var highResolution = request.VideoWidth >= 3840 || request.Fps >= 60;
        if (EncoderCatalog.Get(encoder).IsHardware)
        {
            return highResolution ? 1.8 : 2.4;
        }

        return highResolution ? 0.35 : 1.0;
    }

    private static double EncoderEfficiency(VideoEncoder encoder, VideoCodecKind codec)
    {
        if (!EncoderCatalog.Get(encoder).IsHardware)
        {
            return codec == VideoCodecKind.H265 ? 100 : 92;
        }

        return EncoderCatalog.Get(encoder).Vendor switch
        {
            EncoderVendor.Nvidia => 87,
            EncoderVendor.Intel => 84,
            EncoderVendor.Amd => 82,
            _ => 80
        };
    }

    private static string BuildReason(
        AutoEncoderSelectionRequest request,
        EncoderScore selected,
        IReadOnlyList<EncoderScore> scored,
        BenchmarkConfidence confidence,
        bool usedBenchmark)
    {
        var source = $"{request.VideoWidth?.ToString(CultureInfo.InvariantCulture) ?? "未知"}×" +
                     $"{request.VideoHeight?.ToString(CultureInfo.InvariantCulture) ?? "未知"} " +
                     $"{request.Fps?.ToString("0.##", CultureInfo.InvariantCulture) ?? "未知"} FPS";
        var sourceCodec = string.IsNullOrWhiteSpace(request.SourceCodec) ? "源编码未知" : $"源编码 {request.SourceCodec}";
        var measurements = string.Join("，", scored.Select(item =>
            $"{EncoderCatalog.Get(item.Encoder).DisplayName} {(item.BenchmarkSpeed is > 0 ? $"约 {item.BenchmarkSpeed.Value:0.##}x" : "暂无基准")}"));
        var benchmarkText = usedBenchmark
            ? $"本机基准（{confidence.GetDescription()}置信度）为：{measurements}"
            : "尚未进行本机性能测试，自动模式使用默认策略";
        var policy = request.Profile.GetDescription();
        var tuning = request.TuningPreset.GetDescription();
        var selectedName = EncoderCatalog.Get(selected.Encoder).DisplayName;
        var slowSoftware = selected.Encoder is not (VideoEncoder.Libx264 or VideoEncoder.Libx265 or VideoEncoder.LibsvtAv1) &&
                           scored.Any(item => !EncoderCatalog.Get(item.Encoder).IsHardware &&
                                               item.BenchmarkSpeed is < 0.5);
        var explanation = slowSoftware ? "软件编码预计耗时过长，已选择硬件编码以兼顾质量和处理时间。" : "综合压缩效率、预计速度和当前分辨率/FPS后选择。";
        return $"当前视频为 {source}，{sourceCodec}，目标 {request.RequestedCodec.GetDescription()} {request.TargetBitDepth}-bit；" +
               $"{benchmarkText}；当前使用“{policy} / {tuning}”策略，选择 {selectedName}。" +
               $" {explanation}（Auto score {selected.Score:0.0}/100）";
    }

    private sealed record EncoderScore(
        VideoEncoder Encoder,
        double Score,
        double? BenchmarkSpeed,
        double EffectiveSpeed,
        double SpeedScore,
        double Suitability,
        double Efficiency);
}
