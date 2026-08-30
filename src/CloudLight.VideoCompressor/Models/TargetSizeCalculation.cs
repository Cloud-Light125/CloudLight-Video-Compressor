namespace CloudLight.VideoCompressor.Models;

public sealed record TargetSizeCalculation(
    bool IsValid,
    string? Error,
    long TargetSizeBytes,
    long TargetTotalBitrateBps,
    long ReservedAudioBitrateBps,
    long TargetVideoBitrateBps,
    bool IsLargerThanSource)
{
    public static TargetSizeCalculation Invalid(string error) => new(false, error, 0, 0, 0, 0, false);
}
