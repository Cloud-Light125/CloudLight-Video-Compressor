using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudLight.VideoCompressor.Services;

/// <summary>
/// Tracks only FFmpeg/ffprobe processes created by this application. It deliberately never searches for
/// processes by name, so shutdown cannot affect another application's encoder.
/// </summary>
internal static class MediaProcessRegistry
{
    private static readonly ConcurrentDictionary<int, Process> ActiveProcesses = new();

    public static IDisposable Register(Process process)
    {
        ActiveProcesses[process.Id] = process;
        return new Registration(process.Id, process);
    }

    public static void TerminateAll()
    {
        foreach (var process in ActiveProcesses.Values)
        {
            TryTerminate(process);
        }
    }

    public static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process exited or was released in a normal cancellation race.
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly int _processId;
        private readonly Process _process;

        public Registration(int processId, Process process)
        {
            _processId = processId;
            _process = process;
        }

        public void Dispose()
        {
            if (ActiveProcesses.TryGetValue(_processId, out var current) && ReferenceEquals(current, _process))
            {
                ActiveProcesses.TryRemove(_processId, out _);
            }
        }
    }
}
