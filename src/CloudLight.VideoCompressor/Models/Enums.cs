using System.ComponentModel;

namespace CloudLight.VideoCompressor.Models;

public enum VideoTaskStatus
{
    [Description("待处理")]
    Waiting,
    [Description("分析中")]
    Analyzing,
    [Description("符合条件")]
    Eligible,
    [Description("已跳过")]
    Skipped,
    [Description("排队中")]
    Queued,
    [Description("压缩中")]
    Compressing,
    [Description("验证中")]
    Verifying,
    [Description("正在提交输出")]
    Committing,
    [Description("已完成")]
    Completed,
    [Description("失败")]
    Failed,
    [Description("已取消")]
    Cancelled
}

public enum RuleField
{
    [Description("文件大小")]
    FileSize,
    [Description("文件名")]
    FileName,
    [Description("扩展名")]
    Extension,
    [Description("视频码率")]
    VideoBitrate,
    [Description("总码率")]
    TotalBitrate,
    [Description("宽度")]
    Width,
    [Description("高度")]
    Height,
    [Description("FPS")]
    FrameRate,
    [Description("视频编码")]
    VideoCodec,
    [Description("时长")]
    Duration
}

public enum RuleComparison
{
    [Description(">")]
    GreaterThan,
    [Description(">=")]
    GreaterOrEqual,
    [Description("<")]
    LessThan,
    [Description("<=")]
    LessOrEqual,
    [Description("=")]
    Equal,
    [Description("!=")]
    NotEqual
}

public enum RuleJoin
{
    [Description("AND")]
    And,
    [Description("OR")]
    Or
}

public enum CompressionMode
{
    [Description("CRF 质量模式")]
    Crf,
    [Description("指定视频码率")]
    Bitrate,
    [Description("指定目标文件大小")]
    TargetSize,
    [Description("智能自动压缩")]
    SmartAutomatic
}

public enum ConditionResultState
{
    [Description("待判断")]
    Pending,
    [Description("符合")]
    Matches,
    [Description("不符合")]
    DoesNotMatch,
    [Description("全部允许")]
    AllAllowed,
    [Description("判断失败")]
    Failed
}

public enum QueueFilter
{
    [Description("全部")]
    All,
    [Description("符合条件")]
    ConditionMatches,
    [Description("不符合条件")]
    ConditionNotMatches,
    [Description("处理中")]
    Processing,
    [Description("已完成")]
    Completed,
    [Description("失败")]
    Failed
}

public enum VideoCodecKind
{
    [Description("H.264")]
    H264,
    [Description("H.265 / HEVC")]
    H265,
    [Description("AV1")]
    Av1
}

/// <summary>
/// Codec is the bitstream format. It is intentionally separate from the
/// concrete FFmpeg encoder implementation below.
/// </summary>
public enum VideoCodec
{
    H264,
    Hevc,
    Av1
}

/// <summary>
/// Concrete encoder implementation used by FFmpeg. VideoEncoder is retained
/// as the persisted/legacy UI enum; this enum is the domain vocabulary used by
/// the new pipeline and maps one-to-one to it.
/// </summary>
public enum EncoderImplementation
{
    Libx264,
    Libx265,
    H264Nvenc,
    HevcNvenc,
    H264Qsv,
    HevcQsv,
    H264Amf,
    HevcAmf,
    LibsvtAv1
}

public enum EncoderType
{
    Software,
    Hardware
}

public enum RateControlMode
{
    ConstantRateFactor,
    ConstantQuality,
    ConstantQuantizer,
    AverageBitrate,
    VariableBitrate,
    TargetSizeTwoPass,
    QualityVariableBitrate
}

public enum CompressionProfile
{
    [Description("高质量")]
    HighQuality,
    [Description("平衡")]
    Balanced,
    [Description("节省空间")]
    SpaceSaving,
    [Description("远程播放")]
    RemotePlayback,
    [Description("自定义")]
    Custom
}

public enum AudioPolicy
{
    FollowSettings,
    CopyWhenPossible,
    PreferAac
}

public enum BandwidthPolicy
{
    None,
    RespectSafeTotalBudget
}

public enum SpeedVsEfficiencyPreference
{
    Efficiency,
    Balanced,
    Speed
}

/// <summary>
/// User-facing encoder effort. This is deliberately separate from the
/// compression profile: a profile describes the output goal while this value
/// describes how much encoder computation may be spent reaching that goal.
/// </summary>
public enum EncoderTuningPreset
{
    [Description("高质量")]
    HighQuality,
    [Description("平衡")]
    Balanced,
    [Description("快速")]
    Fast,
    [Description("极速")]
    VeryFast
}

public enum BitDepthPolicy
{
    [Description("自动")]
    Auto,
    [Description("8-bit")]
    EightBit,
    [Description("10-bit")]
    TenBit
}

public enum BenchmarkConfidence
{
    [Description("未知")]
    Unknown,
    [Description("低")]
    Low,
    [Description("中")]
    Medium,
    [Description("高")]
    High
}

public enum VmafComplexityClass
{
    [Description("未知复杂度")]
    Unknown,
    [Description("低复杂度")]
    Low,
    [Description("中复杂度")]
    Medium,
    [Description("高复杂度")]
    High
}

public enum PipelineStage
{
    Discover,
    Probe,
    Evaluate,
    Plan,
    Preview,
    Execute,
    Validate,
    Commit,
    Result
}

public enum CompressionAttemptStatus
{
    [Description("等待")]
    Pending,
    [Description("执行中")]
    Running,
    [Description("已完成")]
    Completed,
    [Description("失败")]
    Failed,
    [Description("进度停滞")]
    Stalled,
    [Description("已取消")]
    Cancelled
}

public enum CompressionFailureKind
{
    [Description("无")]
    None,
    [Description("编码器不可用")]
    EncoderUnavailable,
    [Description("设备初始化失败")]
    DeviceInitializationFailure,
    [Description("硬件会话失败")]
    HardwareSessionFailure,
    [Description("编码进度停滞")]
    EncoderStall,
    [Description("输出路径失败")]
    OutputPathFailure,
    [Description("磁盘空间不足")]
    DiskSpaceFailure,
    [Description("源文件损坏或不完整")]
    SourceCorrupt,
    [Description("权限失败")]
    PermissionFailure,
    [Description("用户取消")]
    UserCancelled,
    [Description("输出验证失败")]
    ValidationFailed,
    [Description("压缩结果已放弃")]
    ResultRejected,
    [Description("未知错误")]
    Unknown
}

public enum EncoderSelectionMode
{
    [Description("自动")]
    Automatic,
    [Description("CPU 软件编码")]
    CpuSoftware,
    [Description("硬件自动")]
    HardwareAutomatic,
    [Description("NVIDIA NVENC")]
    NvidiaNvenc,
    [Description("Intel Quick Sync")]
    IntelQsv,
    [Description("AMD AMF")]
    AmdAmf
}

public enum PerformanceMode
{
    [Description("自动")]
    Automatic,
    [Description("低配置 / 长时间稳定")]
    LowEndStable,
    [Description("平衡")]
    Balanced,
    [Description("速度优先")]
    SpeedPriority
}

public enum ProcessPriorityMode
{
    Normal,
    BelowNormal
}

public enum SoftwareThreadPolicy
{
    EncoderDefault,
    ReserveSystemCores
}

public enum EtaConfidence
{
    Unknown,
    Low,
    Medium,
    High
}

public enum EncoderVendor
{
    [Description("CPU")]
    Cpu,
    [Description("NVIDIA")]
    Nvidia,
    [Description("Intel")]
    Intel,
    [Description("AMD")]
    Amd
}

public enum SmartCompressionPreset
{
    [Description("高质量")]
    HighQuality,
    [Description("平衡")]
    Balanced,
    [Description("节省空间")]
    SpaceSaving,
    [Description("远程播放")]
    RemotePlayback,
    [Description("自定义")]
    Custom
}

public enum SmartRateControlMode
{
    [Description("平均码率 + 峰值约束")]
    AverageWithPeak,
    [Description("恒定质量")]
    ConstantQuality
}

public enum TargetSizeUnit
{
    [Description("MB")]
    Megabytes,
    [Description("GB")]
    Gigabytes
}

public enum VideoEncoder
{
    [Description("H.264 / libx264")]
    Libx264,
    [Description("H.265 / libx265")]
    Libx265,
    [Description("H.264 NVENC")]
    H264Nvenc,
    [Description("H.265 NVENC")]
    HevcNvenc,
    [Description("H.264 Intel QSV")]
    H264Qsv,
    [Description("H.265 Intel QSV")]
    HevcQsv,
    [Description("H.264 AMD AMF")]
    H264Amf,
    [Description("H.265 AMD AMF")]
    HevcAmf,
    [Description("AV1 / libsvtav1")]
    LibsvtAv1
}

public enum AudioMode
{
    [Description("保持 / 复制")]
    Copy,
    [Description("AAC 重新编码")]
    Aac
}

public enum OutputLocationMode
{
    [Description("原目录")]
    SameDirectory,
    [Description("指定目录")]
    SelectedDirectory,
    [Description("原目录下的自定义子目录")]
    ChildDirectory
}

public enum OutputContainerMode
{
    [Description("保持源容器")]
    PreserveSource,
    [Description("MP4")]
    Mp4,
    [Description("MKV")]
    Mkv
}

public enum CompletionAction
{
    [Description("不执行操作")]
    None,
    [Description("关闭软件")]
    CloseApplication,
    [Description("睡眠")]
    Sleep,
    [Description("休眠")]
    Hibernate,
    [Description("关闭电脑")]
    Shutdown
}

public enum OriginalFileAction
{
    [Description("原文件保持不动")]
    Keep,
    [Description("移动到指定目录")]
    MoveToSelectedDirectory,
    [Description("移动到原目录子目录")]
    MoveToSiblingChildDirectory
}

public enum ResolutionLimitPreset
{
    [Description("保持原分辨率")]
    Keep,
    [Description("最大 3840 × 2160")]
    UHD4K,
    [Description("最大 2560 × 1440")]
    QHD1440p,
    [Description("最大 1920 × 1080")]
    FullHd1080p,
    [Description("最大 1280 × 720")]
    Hd720p,
    [Description("自定义")]
    Custom
}

public enum FpsLimitPreset
{
    [Description("保持原 FPS")]
    Keep,
    [Description("最大 120 FPS")]
    Fps120,
    [Description("最大 60 FPS")]
    Fps60,
    [Description("最大 30 FPS")]
    Fps30,
    [Description("自定义")]
    Custom
}
