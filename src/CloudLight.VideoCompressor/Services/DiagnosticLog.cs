using System.Diagnostics;

namespace CloudLight.VideoCompressor.Services;

internal static class DiagnosticLog
{
    public static void Write(string category, string message) =>
        Trace.WriteLine($"[{category}] {message}");
}
