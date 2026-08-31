using System.Runtime.InteropServices;

namespace CloudLight.VideoCompressor.Services;

public sealed record DiskSpaceCheckResult(
    bool IsEnough,
    long AvailableBytes,
    long RequiredBytes,
    string Message);

/// <summary>
/// A deliberately coarse preflight check. CRF output size is unknowable, so
/// this prevents obviously unsafe starts while avoiding false rejection of
/// ordinary small disks.
/// </summary>
public static class DiskSpaceGuard
{
    private const long MinimumFreeBytes = 256L * 1024 * 1024;
    private const long MaximumSourceReserveBytes = 2L * 1024 * 1024 * 1024;

    public static DiskSpaceCheckResult Check(string outputPath, long sourceSizeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(root))
            {
                return Sufficient();
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.AvailableFreeSpace < 0)
            {
                return Sufficient();
            }

            var sourceReserve = Math.Min(
                MaximumSourceReserveBytes,
                Math.Max(0, sourceSizeBytes) / 2);
            var required = Math.Max(MinimumFreeBytes, sourceReserve);
            var enough = drive.AvailableFreeSpace >= required;
            return new DiskSpaceCheckResult(
                enough,
                drive.AvailableFreeSpace,
                required,
                enough
                    ? "磁盘空间预检查通过。"
                    : $"输出磁盘可用空间不足：当前仅 {FormatBytes(drive.AvailableFreeSpace)}，至少需要约 {FormatBytes(required)}；已在编码前停止，源文件未修改。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A network/virtual volume may not expose DriveInfo reliably. It
            // is safer to continue and let FFmpeg report a real write error.
            DiagnosticLog.Write("disk", $"无法读取输出卷剩余空间，跳过预检查：{exception.Message}");
            return Sufficient();
        }
    }

    private static DiskSpaceCheckResult Sufficient() =>
        new(true, -1, 0, "磁盘空间预检查未提供数据，继续执行。");

    private static string FormatBytes(long bytes) =>
        Models.DisplayFormat.FileSize(Math.Max(0, bytes));
}

/// <summary>
/// Keeps Windows awake only while an active compression session holds a
/// lease. ES_DISPLAY_REQUIRED is intentionally omitted so the monitor can
/// still turn off; no power plan is changed.
/// </summary>
public static class CompressionSleepPrevention
{
    private const uint ExecutionContinuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private static readonly object Sync = new();
    private static int _leaseCount;

    public static IDisposable Acquire(bool enabled)
    {
        if (!enabled)
        {
            return NoopLease.Instance;
        }

        lock (Sync)
        {
            if (_leaseCount == 0)
            {
                SetExecutionState(ExecutionContinuous | SystemRequired, "启用");
            }

            _leaseCount++;
        }

        return new Lease();
    }

    private static void Release()
    {
        lock (Sync)
        {
            if (_leaseCount <= 0)
            {
                return;
            }

            _leaseCount--;
            if (_leaseCount == 0)
            {
                SetExecutionState(ExecutionContinuous, "恢复");
            }
        }
    }

    private static void SetExecutionState(uint flags, string action)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || SetThreadExecutionState(flags) == 0)
            {
                DiagnosticLog.Write("power", $"{action}压缩期间防睡眠状态失败，继续执行。");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write("power", $"{action}压缩期间防睡眠状态不受当前平台支持：{exception.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);

    private sealed class Lease : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Release();
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static NoopLease Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
