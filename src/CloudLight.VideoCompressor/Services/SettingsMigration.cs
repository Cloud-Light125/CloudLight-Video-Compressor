using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Normalizes settings at the serializer boundary. The application has no
/// database migration dependency; missing JSON fields continue to use the
/// model defaults and legacy 1.1.0 names remain supported.
/// </summary>
public static class SettingsMigration
{
    public static AppSettings Normalize(AppSettings? settings, bool hasExplicitCompressionProfile = false)
    {
        settings ??= new AppSettings();

        // Older files only contain SmartPreset. Newer files contain both
        // fields, so the explicit profile wins when it is present.
        if (hasExplicitCompressionProfile)
        {
            settings.CompressionProfile = settings.CompressionProfile;
        }
        else
        {
            settings.SmartPreset = settings.SmartPreset;
        }

        settings.Rules ??= [];
        settings.ProbeConcurrency = settings.ProbeConcurrency;
        settings.CompressionConcurrency = settings.CompressionConcurrency;
        settings.Crf = settings.Crf;
        settings.TargetVideoBitrateMbps = settings.TargetVideoBitrateMbps;
        settings.AudioBitrateKbps = settings.AudioBitrateKbps;
        settings.RemotePlaybackBandwidthMbps = settings.RemotePlaybackBandwidthMbps;
        settings.RemotePlaybackSafetyRatio = settings.RemotePlaybackSafetyRatio;
        settings.SmartMaximumVideoBitrateMbps = settings.SmartMaximumVideoBitrateMbps;
        settings.SmartMinimumExpectedSavingRatio = settings.SmartMinimumExpectedSavingRatio;
        settings.SmartQualityFactor = settings.SmartQualityFactor;
        settings.VmafTarget = settings.VmafTarget;
        settings.QualityCalibrationSampleSeconds = settings.QualityCalibrationSampleSeconds;
        settings.QualityCalibrationCandidateCount = settings.QualityCalibrationCandidateCount;
        return settings;
    }
}
