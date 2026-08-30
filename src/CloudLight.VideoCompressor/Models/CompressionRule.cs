using System.Globalization;
using CloudLight.VideoCompressor.Infrastructure;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Models;

public sealed class CompressionRule : ObservableObject
{
    private bool _isEnabled = true;
    private RuleField _field = RuleField.FileSize;
    private RuleComparison _comparison = RuleComparison.GreaterThan;
    private string _value = "1 GB";
    private RuleJoin _joinWithPrevious = RuleJoin.And;

    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public RuleField Field
    {
        get => _field;
        set
        {
            if (!SetProperty(ref _field, value))
            {
                return;
            }

            var normalized = NormalizeValue(_value, _field);
            if (!string.Equals(_value, normalized, StringComparison.Ordinal))
            {
                _value = normalized;
                OnPropertyChanged(nameof(Value));
            }
            OnPropertyChanged(nameof(Unit));
        }
    }
    public RuleComparison Comparison { get => _comparison; set => SetProperty(ref _comparison, value); }
    public string Value { get => _value; set => SetProperty(ref _value, NormalizeValue(value, Field)); }
    public RuleJoin JoinWithPrevious { get => _joinWithPrevious; set => SetProperty(ref _joinWithPrevious, value); }

    public string Unit => Field switch
    {
        RuleField.FileSize => "GB",
        RuleField.VideoBitrate or RuleField.TotalBitrate => "Mbps",
        RuleField.Width or RuleField.Height => "px",
        RuleField.FrameRate => "FPS",
        RuleField.Duration => "分钟",
        _ => "—"
    };

    public string GetComparisonValue() => Field switch
    {
        RuleField.FileSize => $"{Value} GB",
        RuleField.VideoBitrate or RuleField.TotalBitrate => $"{Value} Mbps",
        RuleField.Duration => $"{Value}m",
        _ => Value
    };

    private static string NormalizeValue(string? value, RuleField field)
    {
        var text = value?.Trim() ?? string.Empty;
        switch (field)
        {
            case RuleField.FileSize when ValueParser.TryParseFileSize(text, out var bytes, out _):
                return FormatNumber(bytes / (1024d * 1024 * 1024));
            case RuleField.VideoBitrate or RuleField.TotalBitrate when ValueParser.TryParseBitrate(text, out var bitsPerSecond, out _):
                return FormatNumber(bitsPerSecond / 1_000_000d);
            case RuleField.Duration when ValueParser.TryParseDuration(text, out var seconds, out _):
                return FormatNumber(seconds / 60d);
            case RuleField.Width or RuleField.Height or RuleField.FrameRate when ValueParser.TryParseNumber(text, out var number):
                return FormatNumber(number);
            default:
                return text;
        }
    }

    private static string FormatNumber(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
}
