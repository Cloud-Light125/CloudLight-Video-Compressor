using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Models;

public sealed class AppSettings : ObservableObject
{
    private string _lastDirectory = string.Empty;
    private string _ffmpegDirectory = string.Empty;
    private bool _recursiveScan = true;
    private int _probeConcurrency = 2;
    private int _compressionConcurrency = 1;
    private CompressionMode _compressionMode = CompressionMode.Crf;
    private VideoEncoder _videoEncoder = VideoEncoder.Libx265;
    // These nullable fields preserve the legacy VideoEncoder-only settings format.
    // A null value means that the old explicit encoder should continue to be used.
    private EncoderSelectionMode? _encoderSelection;
    private VideoCodecKind? _targetVideoCodec;
    private string _encodingPreset = "medium";
    private double _crf = 26;
    private double _targetVideoBitrateMbps = 6;
    private double _targetSizeValue = 700;
    private TargetSizeUnit _targetSizeUnit = TargetSizeUnit.Megabytes;
    private ResolutionLimitPreset _resolutionLimit = ResolutionLimitPreset.Keep;
    private int _customMaxWidth = 1920;
    private int _customMaxHeight = 1080;
    private FpsLimitPreset _fpsLimit = FpsLimitPreset.Keep;
    private double _customMaxFps = 60;
    private AudioMode _audioMode = AudioMode.Copy;
    private int _audioBitrateKbps = 192;
    private OutputLocationMode _outputLocation = OutputLocationMode.SameDirectory;
    private string _outputDirectory = string.Empty;
    private string _outputSubdirectory = "Compressed";
    private bool _preserveDirectoryStructure = true;
    private OriginalFileAction _originalFileAction = OriginalFileAction.Keep;
    private string _originalFilesDirectory = string.Empty;
    private string _originalFilesSubdirectory = "高质量";
    private string _outputPrefix = string.Empty;
    private string _outputSuffix = "_compressed";
    private string _originalPrefix = string.Empty;
    private string _originalSuffix = string.Empty;
    private bool _discardIfLarger = true;
    private SmartCompressionPreset _smartPreset = SmartCompressionPreset.Balanced;
    private CompressionProfile _compressionProfile = CompressionProfile.Balanced;
    private double _remotePlaybackBandwidthMbps = 12;
    private double _remotePlaybackSafetyRatio = 0.70;
    private double _smartMaximumVideoBitrateMbps;
    private double _smartMinimumExpectedSavingRatio = 0.08;
    private double _smartQualityFactor = 1.0;
    private bool _enableAdvancedQualityCalibration;
    private double _vmafTarget = 95;
    private int _qualityCalibrationSampleSeconds = 8;
    private int _qualityCalibrationCandidateCount = 3;

    public string LastDirectory { get => _lastDirectory; set => SetProperty(ref _lastDirectory, value); }
    public string FFmpegDirectory { get => _ffmpegDirectory; set => SetProperty(ref _ffmpegDirectory, value); }
    public bool RecursiveScan { get => _recursiveScan; set => SetProperty(ref _recursiveScan, value); }
    public int ProbeConcurrency { get => _probeConcurrency; set => SetProperty(ref _probeConcurrency, Math.Clamp(value, 1, 8)); }
    public int CompressionConcurrency { get => _compressionConcurrency; set => SetProperty(ref _compressionConcurrency, Math.Clamp(value, 1, 4)); }
    public CompressionMode CompressionMode { get => _compressionMode; set => SetProperty(ref _compressionMode, value); }
    public VideoEncoder VideoEncoder
    {
        get => _videoEncoder;
        set
        {
            if (SetProperty(ref _videoEncoder, value) && TargetVideoCodec is null)
            {
                OnPropertyChanged(nameof(SelectedVideoCodec));
            }
        }
    }
    public EncoderSelectionMode? EncoderSelection
    {
        get => _encoderSelection;
        set
        {
            if (SetProperty(ref _encoderSelection, value))
            {
                OnPropertyChanged(nameof(SelectedEncoderSelection));
            }
        }
    }

    [JsonIgnore]
    public EncoderSelectionMode SelectedEncoderSelection
    {
        get => EncoderSelection ?? LegacyEncoderSelection(VideoEncoder);
        set => EncoderSelection = value;
    }

    public VideoCodecKind? TargetVideoCodec
    {
        get => _targetVideoCodec;
        set
        {
            if (SetProperty(ref _targetVideoCodec, value))
            {
                OnPropertyChanged(nameof(SelectedVideoCodec));
            }
        }
    }

    [JsonIgnore]
    public VideoCodecKind SelectedVideoCodec
    {
        get => TargetVideoCodec ?? LegacyCodec(VideoEncoder);
        set => TargetVideoCodec = value;
    }
    public string EncodingPreset { get => _encodingPreset; set => SetProperty(ref _encodingPreset, string.IsNullOrWhiteSpace(value) ? "medium" : value.Trim()); }
    public double Crf { get => _crf; set => SetProperty(ref _crf, Math.Clamp(value, 0, 51)); }
    public double TargetVideoBitrateMbps { get => _targetVideoBitrateMbps; set => SetProperty(ref _targetVideoBitrateMbps, Math.Max(0.01, value)); }
    // Keep the legacy JSON field so existing settings files continue to deserialize. The UI edits the value and unit separately.
    public string TargetSize
    {
        get => $"{TargetSizeValue.ToString("0.###############", CultureInfo.InvariantCulture)} {(TargetSizeUnit == TargetSizeUnit.Gigabytes ? "GB" : "MB")}";
        set => SetTargetSizeFromLegacyText(value);
    }

    [JsonIgnore]
    public double TargetSizeValue
    {
        get => _targetSizeValue;
        set
        {
            if (SetProperty(ref _targetSizeValue, Math.Max(0.01, value)))
            {
                OnPropertyChanged(nameof(TargetSize));
            }
        }
    }

    [JsonIgnore]
    public TargetSizeUnit TargetSizeUnit
    {
        get => _targetSizeUnit;
        set
        {
            if (SetProperty(ref _targetSizeUnit, value))
            {
                OnPropertyChanged(nameof(TargetSize));
            }
        }
    }
    public ResolutionLimitPreset ResolutionLimit { get => _resolutionLimit; set => SetProperty(ref _resolutionLimit, value); }
    public int CustomMaxWidth { get => _customMaxWidth; set => SetProperty(ref _customMaxWidth, Math.Max(2, value)); }
    public int CustomMaxHeight { get => _customMaxHeight; set => SetProperty(ref _customMaxHeight, Math.Max(2, value)); }
    public FpsLimitPreset FpsLimit { get => _fpsLimit; set => SetProperty(ref _fpsLimit, value); }
    public double CustomMaxFps { get => _customMaxFps; set => SetProperty(ref _customMaxFps, Math.Max(1, value)); }
    public AudioMode AudioMode { get => _audioMode; set => SetProperty(ref _audioMode, value); }
    public int AudioBitrateKbps { get => _audioBitrateKbps; set => SetProperty(ref _audioBitrateKbps, Math.Max(8, value)); }
    public OutputLocationMode OutputLocation { get => _outputLocation; set => SetProperty(ref _outputLocation, value); }
    public string OutputDirectory { get => _outputDirectory; set => SetProperty(ref _outputDirectory, value); }
    public string OutputSubdirectory { get => _outputSubdirectory; set => SetProperty(ref _outputSubdirectory, value); }
    public bool PreserveDirectoryStructure { get => _preserveDirectoryStructure; set => SetProperty(ref _preserveDirectoryStructure, value); }
    public OriginalFileAction OriginalFileAction { get => _originalFileAction; set => SetProperty(ref _originalFileAction, value); }
    public string OriginalFilesDirectory { get => _originalFilesDirectory; set => SetProperty(ref _originalFilesDirectory, value); }
    public string OriginalFilesSubdirectory { get => _originalFilesSubdirectory; set => SetProperty(ref _originalFilesSubdirectory, value); }
    public string OutputPrefix { get => _outputPrefix; set => SetProperty(ref _outputPrefix, value); }
    public string OutputSuffix { get => _outputSuffix; set => SetProperty(ref _outputSuffix, value); }
    public string OriginalPrefix { get => _originalPrefix; set => SetProperty(ref _originalPrefix, value); }
    public string OriginalSuffix { get => _originalSuffix; set => SetProperty(ref _originalSuffix, value); }
    public bool DiscardIfLarger { get => _discardIfLarger; set => SetProperty(ref _discardIfLarger, value); }
    public SmartCompressionPreset SmartPreset
    {
        get => _smartPreset;
        set
        {
            if (SetProperty(ref _smartPreset, value))
            {
                var profile = CompressionProfileCatalog.FromLegacy(value);
                if (_compressionProfile != profile)
                {
                    _compressionProfile = profile;
                    OnPropertyChanged(nameof(CompressionProfile));
                }
            }
        }
    }

    /// <summary>
    /// New profile name. SmartPreset remains serialized and synchronized for
    /// compatibility with settings.json files from 1.1.0 and earlier.
    /// </summary>
    public CompressionProfile CompressionProfile
    {
        get => _compressionProfile;
        set
        {
            if (SetProperty(ref _compressionProfile, value))
            {
                var legacy = CompressionProfileCatalog.ToLegacy(value);
                if (_smartPreset != legacy)
                {
                    _smartPreset = legacy;
                    OnPropertyChanged(nameof(SmartPreset));
                }
            }
        }
    }
    public double RemotePlaybackBandwidthMbps
    {
        get => _remotePlaybackBandwidthMbps;
        set => SetProperty(ref _remotePlaybackBandwidthMbps, Math.Clamp(value, 0.1, 1_000));
    }

    public double RemotePlaybackSafetyRatio
    {
        get => _remotePlaybackSafetyRatio;
        set => SetProperty(ref _remotePlaybackSafetyRatio, Math.Clamp(value, 0.5, 0.9));
    }

    /// <summary>Zero means no additional user cap.</summary>
    public double SmartMaximumVideoBitrateMbps
    {
        get => _smartMaximumVideoBitrateMbps;
        set => SetProperty(ref _smartMaximumVideoBitrateMbps, Math.Clamp(value, 0, 1_000));
    }

    public double SmartMinimumExpectedSavingRatio
    {
        get => _smartMinimumExpectedSavingRatio;
        set => SetProperty(ref _smartMinimumExpectedSavingRatio, Math.Clamp(value, 0, 0.95));
    }

    public double SmartQualityFactor
    {
        get => _smartQualityFactor;
        set => SetProperty(ref _smartQualityFactor, Math.Clamp(value, 0.5, 1.5));
    }

    /// <summary>
    /// Optional and intentionally disabled by default. When enabled, planning
    /// may run bounded representative-sample quality calibration if libvmaf is
    /// available in the selected FFmpeg build.
    /// </summary>
    public bool EnableAdvancedQualityCalibration
    {
        get => _enableAdvancedQualityCalibration;
        set => SetProperty(ref _enableAdvancedQualityCalibration, value);
    }

    public double VmafTarget
    {
        get => _vmafTarget;
        set => SetProperty(ref _vmafTarget, Math.Clamp(value, 70, 99.9));
    }

    public int QualityCalibrationSampleSeconds
    {
        get => _qualityCalibrationSampleSeconds;
        set => SetProperty(ref _qualityCalibrationSampleSeconds, Math.Clamp(value, 5, 15));
    }

    public int QualityCalibrationCandidateCount
    {
        get => _qualityCalibrationCandidateCount;
        set => SetProperty(ref _qualityCalibrationCandidateCount, Math.Clamp(value, 3, 5));
    }

    public ObservableCollection<CompressionRule> Rules { get; set; } = [];

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings);
        return System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
    }

    private void SetTargetSizeFromLegacyText(string? value)
    {
        if (!ValueParser.TryParseFileSize(value, out var bytes, out _))
        {
            return;
        }

        var unit = bytes >= 1024L * 1024 * 1024 ? TargetSizeUnit.Gigabytes : TargetSizeUnit.Megabytes;
        var divisor = unit == TargetSizeUnit.Gigabytes
            ? 1024d * 1024 * 1024
            : 1024d * 1024;
        var numericValue = Math.Max(0.01, bytes / divisor);
        var valueChanged = !EqualityComparer<double>.Default.Equals(_targetSizeValue, numericValue);
        var unitChanged = _targetSizeUnit != unit;
        _targetSizeValue = numericValue;
        _targetSizeUnit = unit;
        if (valueChanged)
        {
            OnPropertyChanged(nameof(TargetSizeValue));
        }
        if (unitChanged)
        {
            OnPropertyChanged(nameof(TargetSizeUnit));
        }
        if (valueChanged || unitChanged)
        {
            OnPropertyChanged(nameof(TargetSize));
        }
    }

    private static VideoCodecKind LegacyCodec(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.Libx264 or VideoEncoder.H264Nvenc or VideoEncoder.H264Qsv or VideoEncoder.H264Amf => VideoCodecKind.H264,
        VideoEncoder.LibsvtAv1 => VideoCodecKind.Av1,
        _ => VideoCodecKind.H265
    };

    private static EncoderSelectionMode LegacyEncoderSelection(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.H264Nvenc or VideoEncoder.HevcNvenc => EncoderSelectionMode.NvidiaNvenc,
        VideoEncoder.H264Qsv or VideoEncoder.HevcQsv => EncoderSelectionMode.IntelQsv,
        VideoEncoder.H264Amf or VideoEncoder.HevcAmf => EncoderSelectionMode.AmdAmf,
        _ => EncoderSelectionMode.CpuSoftware
    };
}
