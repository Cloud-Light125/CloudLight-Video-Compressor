using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public sealed class RuleEngine
{
    private static readonly HashSet<RuleField> ProbeFields =
    [
        RuleField.VideoBitrate, RuleField.TotalBitrate, RuleField.Width, RuleField.Height,
        RuleField.FrameRate, RuleField.VideoCodec, RuleField.Duration
    ];

    public bool RequiresProbe(IEnumerable<CompressionRule> rules) =>
        rules.Any(rule => rule.IsEnabled && ProbeFields.Contains(rule.Field));

    public ConditionEvaluationResult Evaluate(VideoFileInfo media, IEnumerable<CompressionRule> rules)
    {
        var activeRules = rules.Where(rule => rule.IsEnabled).ToList();
        if (activeRules.Count == 0)
        {
            return new ConditionEvaluationResult(
                ConditionResultState.AllAllowed,
                true,
                "全部允许：未设置条件。",
                [],
                "未设置启用的压缩条件。所有选中的视频均允许进入处理队列。 ");
        }

        bool? aggregate = null;
        var details = new List<string>(activeRules.Count);
        var ruleResults = new List<ConditionRuleResult>(activeRules.Count);
        var hasUnavailableValue = false;
        foreach (var rule in activeRules)
        {
            var check = EvaluateRule(media, rule);
            hasUnavailableValue |= !check.IsAvailable;
            var join = aggregate is null ? string.Empty : $"{JoinText(rule.JoinWithPrevious)} ";
            details.Add(join + check.Message);
            ruleResults.Add(check.Result);
            aggregate = aggregate is null
                ? check.IsMatch
                : rule.JoinWithPrevious == RuleJoin.And
                    ? aggregate.Value && check.IsMatch
                    : aggregate.Value || check.IsMatch;
        }

        var matched = aggregate ?? true;
        // An unavailable operand must never be allowed through an OR branch. A
        // conservative failed result keeps a partial ffprobe result from
        // accidentally admitting a file into the destructive workflow.
        var state = hasUnavailableValue
            ? ConditionResultState.Failed
            : matched
                ? ConditionResultState.Matches
                : ConditionResultState.DoesNotMatch;
        var isMatch = state is ConditionResultState.Matches or ConditionResultState.AllAllowed;
        var resultText = state switch
        {
            ConditionResultState.Matches => "符合",
            ConditionResultState.Failed => "判断失败",
            _ => "不符合"
        };
        var summary = state switch
        {
            ConditionResultState.Matches => "已满足压缩条件。",
            ConditionResultState.Failed => "判断失败：无法完整读取条件所需的媒体信息。",
            _ => "已跳过：不符合压缩条件。"
        };
        var tooltip = string.Join(Environment.NewLine, details) + Environment.NewLine + $"最终结果：{resultText}";
        return new ConditionEvaluationResult(state, isMatch, summary, details, tooltip, ruleResults);
    }

    private static RuleCheck EvaluateRule(VideoFileInfo media, CompressionRule rule)
    {
        return rule.Field switch
        {
            RuleField.FileSize => CompareNumeric(media.FileSizeBytes, rule, "文件大小", "字节", ParseFileSize),
            RuleField.VideoBitrate => CompareOptionalNumeric(media.VideoBitrateBps, rule, "视频码率", "bps", ParseBitrate),
            RuleField.TotalBitrate => CompareOptionalNumeric(media.TotalBitrateBps, rule, "总码率", "bps", ParseBitrate),
            RuleField.Width => CompareOptionalNumeric(media.Width, rule, "宽度", "像素", ParseNumber),
            RuleField.Height => CompareOptionalNumeric(media.Height, rule, "高度", "像素", ParseNumber),
            RuleField.FrameRate => CompareOptionalNumeric(media.FrameRate, rule, "FPS", string.Empty, ParseNumber),
            RuleField.Duration => CompareOptionalNumeric(media.DurationSeconds, rule, "时长", "秒", ParseDuration),
            RuleField.VideoCodec => CompareText(media.VideoCodec, rule, "视频编码"),
            RuleField.FileName => CompareText(media.FileName, rule, "文件名"),
            RuleField.Extension => CompareText(media.Extension, rule, "扩展名"),
            _ => new RuleCheck(
                false,
                false,
                "未知条件字段。",
                new ConditionRuleResult(rule.Field, "不可用", rule.Comparison, rule.GetComparisonValue(), string.Empty, false, false, "未知条件字段。"))
        };
    }

    private static RuleCheck CompareNumeric(double actual, CompressionRule rule, string field, string unit, ValueParserDelegate parser)
    {
        var comparisonValue = rule.GetComparisonValue();
        if (!parser(comparisonValue, out var expected, out var error))
        {
            return new RuleCheck(
                false,
                false,
                $"{field}条件无效：{error}",
                new ConditionRuleResult(rule.Field, FormatValue(actual, unit), rule.Comparison, comparisonValue, unit, false, false, $"{field}条件无效：{error}"));
        }

        var matched = Compare(actual, expected, rule.Comparison);
        var message = NumericMessage(field, actual, expected, unit, rule.Comparison, matched);
        return new RuleCheck(
            matched,
            true,
            message,
            new ConditionRuleResult(rule.Field, FormatValue(actual, unit), rule.Comparison, FormatValue(expected, unit), unit, matched, true, message));
    }

    private static RuleCheck CompareOptionalNumeric<T>(T? actual, CompressionRule rule, string field, string unit, ValueParserDelegate parser)
        where T : struct, IConvertible
    {
        if (actual is null)
        {
            var message = $"{field}不可用，无法判断条件 {OperatorText(rule.Comparison)} {rule.GetComparisonValue()}。 ";
            return new RuleCheck(
                false,
                false,
                message,
                new ConditionRuleResult(rule.Field, "不可用", rule.Comparison, rule.GetComparisonValue(), unit, false, false, message));
        }

        return CompareNumeric(Convert.ToDouble(actual.Value), rule, field, unit, parser);
    }

    private static RuleCheck CompareText(string? actual, CompressionRule rule, string field)
    {
        if (actual is null)
        {
            var message = $"{field}不可用，无法判断条件。 ";
            return new RuleCheck(
                false,
                false,
                message,
                new ConditionRuleResult(rule.Field, "不可用", rule.Comparison, rule.Value, string.Empty, false, false, message));
        }

        if (rule.Comparison is not RuleComparison.Equal and not RuleComparison.NotEqual)
        {
            var message = $"{field}仅支持 = 或 != 比较。 ";
            return new RuleCheck(
                false,
                false,
                message,
                new ConditionRuleResult(rule.Field, actual, rule.Comparison, rule.Value, string.Empty, false, false, message));
        }

        var equal = string.Equals(actual.Trim(), rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);
        var matched = rule.Comparison == RuleComparison.Equal ? equal : !equal;
        var messageText = $"{(matched ? "✓" : "✗")} {field}“{actual}” {OperatorText(rule.Comparison)} “{rule.Value}”";
        return new RuleCheck(
            matched,
            true,
            messageText,
            new ConditionRuleResult(rule.Field, actual, rule.Comparison, rule.Value, string.Empty, matched, true, messageText));
    }

    private static bool ParseFileSize(string value, out double parsed, out string error)
    {
        var ok = ValueParser.TryParseFileSize(value, out var bytes, out error);
        parsed = bytes;
        return ok;
    }

    private static bool ParseBitrate(string value, out double parsed, out string error)
    {
        var ok = ValueParser.TryParseBitrate(value, out var bps, out error);
        parsed = bps;
        return ok;
    }

    private static bool ParseNumber(string value, out double parsed, out string error)
    {
        var ok = ValueParser.TryParseNumber(value, out parsed);
        error = ok ? string.Empty : "请输入数字。";
        return ok;
    }

    private static bool ParseDuration(string value, out double parsed, out string error) =>
        ValueParser.TryParseDuration(value, out parsed, out error);

    private static bool Compare(double actual, double expected, RuleComparison comparison) => comparison switch
    {
        RuleComparison.GreaterThan => actual > expected,
        RuleComparison.GreaterOrEqual => actual >= expected,
        RuleComparison.LessThan => actual < expected,
        RuleComparison.LessOrEqual => actual <= expected,
        RuleComparison.Equal => Math.Abs(actual - expected) < 0.000001,
        RuleComparison.NotEqual => Math.Abs(actual - expected) >= 0.000001,
        _ => false
    };

    private static string NumericMessage(string field, double actual, double expected, string unit, RuleComparison comparison, bool matched)
    {
        var actualDisplay = FormatValue(actual, unit);
        var expectedDisplay = FormatValue(expected, unit);
        return $"{(matched ? "✓" : "✗")} {field} {actualDisplay} {OperatorText(comparison)} {expectedDisplay}";
    }

    private static string FormatValue(double value, string unit) => unit == "字节" ? DisplayFormat.FileSize((long)value) :
        unit == "bps" ? $"{value / 1_000_000d:0.##} Mbps" :
        $"{value:0.###}{(string.IsNullOrEmpty(unit) ? string.Empty : $" {unit}")}";

    private static string OperatorText(RuleComparison comparison) => comparison switch
    {
        RuleComparison.GreaterThan => ">",
        RuleComparison.GreaterOrEqual => ">=",
        RuleComparison.LessThan => "<",
        RuleComparison.LessOrEqual => "<=",
        RuleComparison.Equal => "=",
        RuleComparison.NotEqual => "!=",
        _ => "?"
    };

    private static string JoinText(RuleJoin join) => join == RuleJoin.And ? "AND" : "OR";

    private delegate bool ValueParserDelegate(string value, out double parsed, out string error);

    private sealed record RuleCheck(bool IsMatch, bool IsAvailable, string Message, ConditionRuleResult Result);
}
