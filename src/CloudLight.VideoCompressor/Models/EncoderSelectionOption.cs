using CloudLight.VideoCompressor.Infrastructure;

namespace CloudLight.VideoCompressor.Models;

/// <summary>
/// User-facing encoder choice. The internal selection enum remains in the
/// settings model, while this option carries availability information for UI.
/// </summary>
public sealed record EncoderSelectionOption(
    EncoderSelectionMode Mode,
    bool IsAvailable,
    string? UnavailableReason = null)
{
    public string DisplayName => Mode.GetDescription();
    public string AvailabilityDisplay => IsAvailable
        ? DisplayName
        : $"{DisplayName}（不可用）";
    public string ToolTip => IsAvailable
        ? DisplayName
        : $"{DisplayName}当前不可用：{UnavailableReason ?? "未通过能力检测"}";
}
