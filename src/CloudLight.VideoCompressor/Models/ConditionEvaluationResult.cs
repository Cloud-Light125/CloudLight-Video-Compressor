namespace CloudLight.VideoCompressor.Models;

/// <summary>
/// The cached result of evaluating the current compression rules against a video.
/// It is deliberately separate from <see cref="VideoTaskStatus"/>: a video can be
/// eligible while queued, or have a completed task while its displayed rule result
/// is recalculated after the user edits a rule.
/// </summary>
public sealed record ConditionEvaluationResult(
    ConditionResultState State,
    bool IsMatch,
    string Summary,
    IReadOnlyList<string> Details,
    string Tooltip)
{
    public bool IsDetermined => State != ConditionResultState.Pending;

    public static ConditionEvaluationResult Pending { get; } = new(
        ConditionResultState.Pending,
        false,
        "待判断",
        [],
        "尚未根据当前条件判断。扫描或开始处理后会使用已有媒体信息计算。 ");
}
