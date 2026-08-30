using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudLight.VideoCompressor.Services;

public static partial class ValueParser
{
    private const NumberStyles DecimalStyle = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    public static bool TryParseFileSize(string? text, out long bytes, out string error)
    {
        bytes = 0;
        error = string.Empty;
        if (!TrySplit(text, out var value, out var unit))
        {
            error = "请输入例如 700 MB、1 GB 的文件大小。";
            return false;
        }

        var multiplier = unit.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024d,
            "GB" or "GIB" => 1024d * 1024d * 1024d,
            "TB" or "TIB" => 1024d * 1024d * 1024d * 1024d,
            _ => 0d
        };

        if (multiplier == 0 || value <= 0 || value * multiplier > long.MaxValue)
        {
            error = "文件大小单位仅支持 B、KB、MB、GB、TB，且数值必须大于 0。";
            return false;
        }

        bytes = checked((long)Math.Round(value * multiplier));
        return true;
    }

    public static bool TryParseBitrate(string? text, out long bitsPerSecond, out string error)
    {
        bitsPerSecond = 0;
        error = string.Empty;
        if (!TrySplit(text, out var value, out var unit))
        {
            error = "请输入例如 6000 Kbps、10 Mbps 的码率。";
            return false;
        }

        unit = unit.Replace("/S", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("BIT", "", StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
        var multiplier = unit switch
        {
            "BPS" or "" => 1d,
            "KBPS" or "K" => 1_000d,
            "MBPS" or "M" => 1_000_000d,
            "GBPS" or "G" => 1_000_000_000d,
            _ => 0d
        };

        if (multiplier == 0 || value < 0 || value * multiplier > long.MaxValue)
        {
            error = "码率单位仅支持 bps、Kbps、Mbps、Gbps。";
            return false;
        }

        bitsPerSecond = checked((long)Math.Round(value * multiplier));
        return true;
    }

    public static bool TryParseDuration(string? text, out double seconds, out string error)
    {
        seconds = 0;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请输入时长，例如 90s、2m 或 00:01:30。";
            return false;
        }

        var normalized = text.Trim();
        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var span) && span.TotalSeconds >= 0)
        {
            seconds = span.TotalSeconds;
            return true;
        }

        var match = DurationRegex().Match(normalized);
        if (!match.Success || !TryParseNumber(match.Groups["number"].Value, out var value))
        {
            error = "时长格式无效。";
            return false;
        }

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "" or "s" or "sec" or "secs" => 1d,
            "m" or "min" or "mins" => 60d,
            "h" or "hr" or "hrs" => 3600d,
            _ => 0d
        };
        if (multiplier == 0 || value < 0)
        {
            error = "时长单位仅支持 s、m、h。";
            return false;
        }

        seconds = value * multiplier;
        return true;
    }

    public static bool TryParseNumber(string? text, out double number)
    {
        return double.TryParse(text?.Trim(), DecimalStyle, CultureInfo.InvariantCulture, out number) ||
               double.TryParse(text?.Trim(), DecimalStyle, CultureInfo.CurrentCulture, out number);
    }

    private static bool TrySplit(string? text, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;
        var match = NumberWithUnitRegex().Match(text ?? string.Empty);
        return match.Success && TryParseNumber(match.Groups["number"].Value, out value) &&
               (unit = match.Groups["unit"].Value).Length >= 0;
    }

    [GeneratedRegex(@"^\s*(?<number>[+-]?\d+(?:[\.,]\d+)?)\s*(?<unit>[a-zA-Z/]+)\s*$")]
    private static partial Regex NumberWithUnitRegex();

    [GeneratedRegex(@"^\s*(?<number>[+-]?\d+(?:[\.,]\d+)?)\s*(?<unit>[a-zA-Z]*)\s*$")]
    private static partial Regex DurationRegex();
}
