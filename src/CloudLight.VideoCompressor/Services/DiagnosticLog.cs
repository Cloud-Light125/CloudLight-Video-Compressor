using System.Diagnostics;

namespace CloudLight.VideoCompressor.Services;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudLight Video Compressor",
        "diagnostic.log");

    public static void Write(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{category}] {message}";
        Trace.WriteLine(line);
        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must never change application behavior.
        }
    }
}
