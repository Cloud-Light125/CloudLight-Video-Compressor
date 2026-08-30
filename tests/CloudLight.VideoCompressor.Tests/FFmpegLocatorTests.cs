using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class FFmpegLocatorTests
{
    [Fact]
    public void Locate_UsesBundledToolsBeforePathFallback()
    {
        using var applicationDirectory = new TemporaryDirectory();
        using var pathDirectory = new TemporaryDirectory();
        var bundledDirectory = Path.Combine(applicationDirectory.Path, "ffmpeg");
        CreateToolPair(bundledDirectory);
        CreateToolPair(pathDirectory.Path);

        var locator = new FFmpegLocator(applicationDirectory.Path, () => pathDirectory.Path);

        var tools = locator.Locate(null);

        Assert.NotNull(tools);
        Assert.Equal(Path.Combine(bundledDirectory, "ffmpeg.exe"), tools.FFmpegPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(bundledDirectory, "ffprobe.exe"), tools.FFprobePath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_UsesExplicitConfiguredDirectoryAsAdvancedUserOverride()
    {
        using var applicationDirectory = new TemporaryDirectory();
        using var configuredDirectory = new TemporaryDirectory();
        var bundledDirectory = Path.Combine(applicationDirectory.Path, "ffmpeg");
        CreateToolPair(bundledDirectory);
        CreateToolPair(configuredDirectory.Path);

        var locator = new FFmpegLocator(applicationDirectory.Path, () => string.Empty);

        var tools = locator.Locate(configuredDirectory.Path);

        Assert.NotNull(tools);
        Assert.Equal(Path.Combine(configuredDirectory.Path, "ffmpeg.exe"), tools.FFmpegPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(configuredDirectory.Path, "ffprobe.exe"), tools.FFprobePath, StringComparer.OrdinalIgnoreCase);
    }

    private static void CreateToolPair(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ffmpeg.exe"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "ffprobe.exe"), string.Empty);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorLocatorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
