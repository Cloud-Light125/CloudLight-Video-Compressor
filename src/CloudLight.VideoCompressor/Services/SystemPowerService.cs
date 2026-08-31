using System.Runtime.InteropServices;
using CloudLight.VideoCompressor.Models;

namespace CloudLight.VideoCompressor.Services;

public interface ISystemPowerService
{
    void Sleep();
    void Hibernate();
    void Shutdown();
}

public sealed class WindowsSystemPowerService : ISystemPowerService
{
    private const uint EwxShutdown = 0x00000001;
    private const uint EwxForceIfHung = 0x00000010;
    private const uint ShtdnReasonFlagPlanned = 0x80000000;
    private const uint ShtdnReasonMajorApplication = 0x00040000;
    private const uint ShtdnReasonMinorMaintenance = 0x00000001;

    public void Sleep()
    {
        if (!SetSuspendState(false, false, false))
        {
            throw new InvalidOperationException("Windows 拒绝进入睡眠状态。");
        }
    }

    public void Hibernate()
    {
        if (!SetSuspendState(true, false, false))
        {
            throw new InvalidOperationException("Windows 拒绝进入休眠状态。");
        }
    }

    public void Shutdown()
    {
        var reason = ShtdnReasonFlagPlanned | ShtdnReasonMajorApplication | ShtdnReasonMinorMaintenance;
        if (!InitiateSystemShutdownEx(null, null, 0, false, false, reason) &&
            !ExitWindowsEx(EwxShutdown | EwxForceIfHung, reason))
        {
            throw new InvalidOperationException("Windows 拒绝关闭电脑；请检查当前用户的关机权限。");
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownEx(
        string? machineName,
        string? message,
        uint timeout,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown,
        uint reason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}

public sealed record CompletionActionDecision(
    bool ShouldExecute,
    CompletionAction Action,
    string Message);

public static class CompletionActionPolicy
{
    public static CompletionActionDecision Evaluate(
        AppSettings settings,
        IEnumerable<CompressionExecutionState> states)
    {
        var action = settings.CompletionAction;
        if (action == CompletionAction.None)
        {
            return new CompletionActionDecision(false, action, "未设置任务完成后的系统操作。");
        }

        var stateList = states.ToArray();
        if (stateList.Length == 0 || stateList.Any(state => state is
                CompressionExecutionState.WaitingToStart or
                CompressionExecutionState.Queued or
                CompressionExecutionState.Compressing or
                CompressionExecutionState.Verifying or
                CompressionExecutionState.Committing or
                CompressionExecutionState.Interrupted))
        {
            return new CompletionActionDecision(false, action, "仍有任务未进入终态。");
        }

        var hasFailure = stateList.Any(state => state is
            CompressionExecutionState.Failed or
            CompressionExecutionState.Cancelled or
            CompressionExecutionState.SourceChanged);
        if (hasFailure && settings.DoNotPowerOffOnFailure)
        {
            return new CompletionActionDecision(false, action, "队列已结束，但存在失败、取消或源文件变化；按设置不执行系统操作。");
        }

        return new CompletionActionDecision(true, action, "所有任务已进入终态。");
    }
}
