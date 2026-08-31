using System.Globalization;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public interface IEncoderStrategy
{
    VideoEncoder Encoder { get; }
    VideoCodecKind Codec { get; }
    EncoderVendor Vendor { get; }
    EncoderType Type { get; }
    IReadOnlyList<RateControlMode> SupportedRateControls { get; }
    IReadOnlyList<string> SupportedPresets { get; }
    IReadOnlyList<string> SupportedPixelFormats { get; }
    bool SupportsBitDepth(int bitDepth);
    IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan);
    void Validate(CompressionPlan plan);
}

public abstract class EncoderStrategyBase : IEncoderStrategy
{
    protected EncoderStrategyBase(
        VideoEncoder encoder,
        VideoCodecKind codec,
        EncoderVendor vendor,
        EncoderType type,
        IReadOnlyList<RateControlMode> supportedRateControls,
        IReadOnlyList<string> supportedPresets,
        IReadOnlyList<string> supportedPixelFormats)
    {
        Encoder = encoder;
        Codec = codec;
        Vendor = vendor;
        Type = type;
        SupportedRateControls = supportedRateControls;
        SupportedPresets = supportedPresets;
        SupportedPixelFormats = supportedPixelFormats;
    }

    public VideoEncoder Encoder { get; }
    public VideoCodecKind Codec { get; }
    public EncoderVendor Vendor { get; }
    public EncoderType Type { get; }
    public IReadOnlyList<RateControlMode> SupportedRateControls { get; }
    public IReadOnlyList<string> SupportedPresets { get; }
    public IReadOnlyList<string> SupportedPixelFormats { get; }

    public virtual bool SupportsBitDepth(int bitDepth)
    {
        var requiredDepth = bitDepth >= 10 ? 10 : 8;
        return SupportedPixelFormats.Any(format =>
        {
            var depth = BitDepthPolicyResolver.DetectPixelFormatBitDepth(format);
            return requiredDepth == 8 ? depth == 8 : depth >= requiredDepth;
        });
    }

    public abstract IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan);

    public virtual void Validate(CompressionPlan plan)
    {
        if (plan.Encoder != Encoder)
        {
            throw new ArgumentException($"计划编码器不是 {Encoder}。", nameof(plan));
        }

        if (!SupportedRateControls.Contains(plan.EffectiveRateControlMode))
        {
            throw new InvalidOperationException($"{EncoderCatalog.Get(Encoder).DisplayName} 不支持 {plan.EffectiveRateControlMode} 码率控制。 ");
        }
    }

    protected static string Quality(double value, double maximum = 51) =>
        Math.Clamp((int)Math.Round(value), 0, (int)maximum).ToString(CultureInfo.InvariantCulture);

    protected static void AddBitrateArguments(List<string> arguments, CompressionPlan plan, bool includeRc, string? rcValue = null)
    {
        var target = $"{Math.Max(10, (plan.TargetVideoBitrateBps ?? 10_000) / 1_000)}k";
        arguments.AddRange(["-b:v", target]);
        if (plan.MaxVideoBitrateBps is > 0 && plan.MaxVideoBitrateBps >= plan.TargetVideoBitrateBps)
        {
            arguments.AddRange(["-maxrate", $"{Math.Max(10, plan.MaxVideoBitrateBps.Value / 1_000)}k"]);
        }
        if (plan.BufferSizeBps is > 0)
        {
            arguments.AddRange(["-bufsize", $"{Math.Max(10, plan.BufferSizeBps.Value / 1_000)}k"]);
        }
        if (includeRc && rcValue is not null)
        {
            arguments.AddRange(["-rc", rcValue]);
        }
    }

    protected static RateControlMode GetRateControlMode(CompressionPlan plan, bool hardwareQualityMode, bool cqpMode = false) =>
        plan.Mode switch
        {
            CompressionMode.Crf when cqpMode => RateControlMode.ConstantQuantizer,
            CompressionMode.Crf when hardwareQualityMode => RateControlMode.ConstantQuality,
            CompressionMode.Crf => RateControlMode.ConstantRateFactor,
            CompressionMode.TargetSize when plan.IsTwoPass => RateControlMode.TargetSizeTwoPass,
            CompressionMode.TargetSize => RateControlMode.VariableBitrate,
            _ => RateControlMode.AverageBitrate
        };
}

public sealed class X264EncoderStrategy : EncoderStrategyBase
{
    public X264EncoderStrategy() : base(
        VideoEncoder.Libx264,
        VideoCodecKind.H264,
        EncoderVendor.Cpu,
        EncoderType.Software,
        [RateControlMode.ConstantRateFactor, RateControlMode.AverageBitrate, RateControlMode.TargetSizeTwoPass],
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"],
        ["yuv420p", "nv12", "yuv420p10le"])
    {
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string> { "-preset", plan.EncodingPreset };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-crf", Quality(plan.Crf)]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, false);
        }
        return arguments;
    }
}

public sealed class X265EncoderStrategy : EncoderStrategyBase
{
    public X265EncoderStrategy() : base(
        VideoEncoder.Libx265,
        VideoCodecKind.H265,
        EncoderVendor.Cpu,
        EncoderType.Software,
        [RateControlMode.ConstantRateFactor, RateControlMode.AverageBitrate, RateControlMode.TargetSizeTwoPass],
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"],
        ["yuv420p", "yuv420p10le", "yuv422p10le", "yuv444p10le"])
    {
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string> { "-preset", plan.EncodingPreset };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-crf", Quality(plan.Crf)]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, false);
        }
        return arguments;
    }
}

public sealed class NvencEncoderStrategy : EncoderStrategyBase
{
    public NvencEncoderStrategy(VideoEncoder encoder)
        : base(
            encoder,
            EncoderCatalog.Get(encoder).Codec,
            EncoderVendor.Nvidia,
            EncoderType.Hardware,
            [RateControlMode.ConstantQuality, RateControlMode.VariableBitrate, RateControlMode.AverageBitrate],
            ["p1", "p2", "p3", "p4", "p5", "p6", "p7"],
            ["yuv420p", "nv12", "p010le", "cuda"])
    {
        if (encoder is not (VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc))
        {
            throw new ArgumentException("NVENC strategy 只支持 H.264/H.265 NVENC。", nameof(encoder));
        }
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string> { "-preset", NvencPreset(plan.EncodingPreset) };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-rc", "vbr", "-cq", Quality(plan.Crf), "-b:v", "0"]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, true, "vbr");
        }
        return arguments;
    }

    private static string NvencPreset(string preset) => preset.ToLowerInvariant() switch
    {
        "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7" => preset.ToLowerInvariant(),
        "ultrafast" or "superfast" or "veryfast" => "p1",
        "faster" or "fast" => "p3",
        "slow" => "p5",
        "slower" => "p6",
        "veryslow" => "p7",
        _ => "p4"
    };
}

public sealed class QsvEncoderStrategy : EncoderStrategyBase
{
    public QsvEncoderStrategy(VideoEncoder encoder)
        : base(
            encoder,
            EncoderCatalog.Get(encoder).Codec,
            EncoderVendor.Intel,
            EncoderType.Hardware,
            [RateControlMode.ConstantQuality, RateControlMode.VariableBitrate, RateControlMode.AverageBitrate],
            ["1", "2", "3", "4", "5", "6", "7"],
            encoder == VideoEncoder.HevcQsv
                ? ["nv12", "p010le", "p012le", "qsv"]
                : ["nv12", "qsv"])
    {
        if (encoder is not (VideoEncoder.H264Qsv or VideoEncoder.HevcQsv))
        {
            throw new ArgumentException("QSV strategy 只支持 H.264/H.265 QSV。", nameof(encoder));
        }
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string> { "-preset", QsvPreset(plan.EncodingPreset) };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-global_quality", Quality(plan.Crf)]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, false);
        }
        return arguments;
    }

    private static string QsvPreset(string preset) => preset.ToLowerInvariant() switch
    {
        "1" or "2" or "3" or "4" or "5" or "6" or "7" => preset,
        "ultrafast" => "7",
        "superfast" => "6",
        "veryfast" => "5",
        "faster" or "fast" => "4",
        "slow" => "2",
        "slower" or "veryslow" => "1",
        _ => "3"
    };
}

public sealed class AmfEncoderStrategy : EncoderStrategyBase
{
    public AmfEncoderStrategy(VideoEncoder encoder)
        : base(
            encoder,
            EncoderCatalog.Get(encoder).Codec,
            EncoderVendor.Amd,
            EncoderType.Hardware,
            [RateControlMode.ConstantQuantizer, RateControlMode.QualityVariableBitrate, RateControlMode.VariableBitrate],
        ["quality", "high_quality"],
            ["nv12", "yuv420p", "p010le", "amf"])
    {
        if (encoder is not (VideoEncoder.H264Amf or VideoEncoder.HevcAmf))
        {
            throw new ArgumentException("AMF strategy 只支持 H.264/H.265 AMF。", nameof(encoder));
        }
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string>
        {
            "-quality",
            plan.EncodingPreset.Equals("high_quality", StringComparison.OrdinalIgnoreCase) ||
            plan.EncodingPreset.Equals("3", StringComparison.OrdinalIgnoreCase)
                ? "3"
                : "2"
        };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-rc", "cqp", "-qp_i", Quality(plan.Crf), "-qp_p", Quality(plan.Crf)]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, true, "vbr_peak");
        }
        return arguments;
    }
}

public sealed class SvtAv1EncoderStrategy : EncoderStrategyBase
{
    public SvtAv1EncoderStrategy() : base(
        VideoEncoder.LibsvtAv1,
        VideoCodecKind.Av1,
        EncoderVendor.Cpu,
        EncoderType.Software,
        [RateControlMode.ConstantRateFactor, RateControlMode.AverageBitrate, RateControlMode.TargetSizeTwoPass],
        ["0", "2", "4", "6", "8", "10", "12", "13"],
        ["yuv420p", "yuv420p10le"])
    {
    }

    public override IReadOnlyList<string> BuildVideoArguments(CompressionPlan plan)
    {
        Validate(plan);
        var arguments = new List<string> { "-preset", SvtPreset(plan.EncodingPreset) };
        if (plan.Mode == CompressionMode.Crf)
        {
            arguments.AddRange(["-crf", Quality(plan.Crf, 63)]);
        }
        else
        {
            AddBitrateArguments(arguments, plan, false);
        }
        return arguments;
    }

    private static string SvtPreset(string preset) => preset.ToLowerInvariant() switch
    {
        "0" or "2" or "4" or "6" or "8" or "10" or "12" or "13" => preset,
        "ultrafast" => "13",
        "superfast" => "12",
        "veryfast" => "10",
        "faster" => "8",
        "fast" => "7",
        "slow" => "4",
        "slower" => "2",
        "veryslow" => "0",
        _ => "6"
    };
}

public static class EncoderStrategyCatalog
{
    private static readonly IReadOnlyDictionary<VideoEncoder, IEncoderStrategy> Strategies =
        new Dictionary<VideoEncoder, IEncoderStrategy>
        {
            [VideoEncoder.Libx264] = new X264EncoderStrategy(),
            [VideoEncoder.Libx265] = new X265EncoderStrategy(),
            [VideoEncoder.H264Nvenc] = new NvencEncoderStrategy(VideoEncoder.H264Nvenc),
            [VideoEncoder.HevcNvenc] = new NvencEncoderStrategy(VideoEncoder.HevcNvenc),
            [VideoEncoder.H264Qsv] = new QsvEncoderStrategy(VideoEncoder.H264Qsv),
            [VideoEncoder.HevcQsv] = new QsvEncoderStrategy(VideoEncoder.HevcQsv),
            [VideoEncoder.H264Amf] = new AmfEncoderStrategy(VideoEncoder.H264Amf),
            [VideoEncoder.HevcAmf] = new AmfEncoderStrategy(VideoEncoder.HevcAmf),
            [VideoEncoder.LibsvtAv1] = new SvtAv1EncoderStrategy()
        };

    public static IEncoderStrategy Get(VideoEncoder encoder) =>
        Strategies.TryGetValue(encoder, out var strategy)
            ? strategy
            : throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "未注册的编码器策略。");

    public static IReadOnlyList<IEncoderStrategy> All => Strategies.Values.ToArray();
}
