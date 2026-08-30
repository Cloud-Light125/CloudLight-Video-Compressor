using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class PlanningAndPathsTests
{
    [Fact]
    public void TargetSizeCalculator_ReservesAudioAndContainerMargin()
    {
        var media = Media(durationSeconds: 600, fileSizeBytes: 1_500L * 1024 * 1024);
        var result = new TargetSizeCalculator().Calculate("700 MB", media, AudioMode.Aac, 192);

        Assert.True(result.IsValid, result.Error);
        Assert.True(result.TargetVideoBitrateBps > 0);
        Assert.True(result.TargetVideoBitrateBps < result.TargetTotalBitrateBps);
        Assert.Equal(192_000, result.ReservedAudioBitrateBps);
    }

    [Fact]
    public void TargetSizeCalculator_RejectsImpossiblySmallTarget()
    {
        var media = Media(durationSeconds: 7_200);
        var result = new TargetSizeCalculator().Calculate("1 MB", media, AudioMode.Aac, 256);

        Assert.False(result.IsValid);
        Assert.Contains("低于安全下限", result.Error);
    }

    [Fact]
    public void TargetSizeCalculator_DoesNotReserveAudioBitsWhenSourceHasNoAudio()
    {
        var media = Media(durationSeconds: 600, audioTrackCount: 0, audioBitrateBps: null);

        var result = new TargetSizeCalculator().Calculate("700 MB", media, AudioMode.Copy, 192);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(0, result.ReservedAudioBitrateBps);
    }

    [Fact]
    public void OutputPath_UsesStemForSuffixAndAvoidsExistingName()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "abc.test.mp4");
        File.WriteAllText(sourcePath, "source");
        var settings = new AppSettings { OutputPrefix = "Compressed_", OutputSuffix = "_small" };
        var source = Media(fullPath: sourcePath);
        var service = new OutputPathService();
        var first = service.GetOutputPath(source, settings, directory.Path);
        File.WriteAllText(first, "existing");
        var second = service.GetOutputPath(source, settings, directory.Path);

        Assert.EndsWith("Compressed_abc.test_small.mp4", first, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Compressed_abc.test_small (1).mp4", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputPath_AllowsSourceNameOnlyWhenOriginalWillBeMoved()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        File.WriteAllText(sourcePath, "source");
        var source = Media(fullPath: sourcePath);
        var service = new OutputPathService();

        var keepPath = service.GetOutputPath(source, new AppSettings { OutputPrefix = "", OutputSuffix = "", OriginalFileAction = OriginalFileAction.Keep }, directory.Path);
        var replaceAfterMove = service.GetOutputPath(source, new AppSettings { OutputPrefix = "", OutputSuffix = "", OriginalFileAction = OriginalFileAction.MoveToSiblingChildDirectory }, directory.Path);

        Assert.EndsWith("movie (1).mp4", keepPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourcePath, replaceAfterMove, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OutputPrefix", "..\\escape")]
    [InlineData("OutputSuffix", "/escape")]
    public void OutputPath_RejectsPrefixAndSuffixThatCanEscapeOutputDirectory(string propertyName, string value)
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "movie.mp4");
        File.WriteAllText(sourcePath, "source");
        var settings = new AppSettings();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(settings, value);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new OutputPathService().GetOutputPath(Media(fullPath: sourcePath), settings, directory.Path));

        Assert.Contains("路径分隔符", exception.Message);
    }

    [Fact]
    public void OriginalDestination_AvoidsReservedFinalOutputPath()
    {
        using var sourceDirectory = new TemporaryDirectory();
        using var destinationDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(sourceDirectory.Path, "movie.mp4");
        File.WriteAllText(sourcePath, "source");
        var source = Media(fullPath: sourcePath);
        var settings = new AppSettings
        {
            OutputLocation = OutputLocationMode.SelectedDirectory,
            OutputDirectory = destinationDirectory.Path,
            OutputPrefix = string.Empty,
            OutputSuffix = string.Empty,
            OriginalFileAction = OriginalFileAction.MoveToSelectedDirectory,
            OriginalFilesDirectory = destinationDirectory.Path,
            OriginalPrefix = string.Empty,
            OriginalSuffix = string.Empty
        };
        var service = new OutputPathService();
        var finalOutput = service.GetOutputPath(source, settings, sourceDirectory.Path);
        var originalDestination = service.GetOriginalDestinationPath(source, settings, finalOutput);

        Assert.False(string.Equals(finalOutput, originalDestination, StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("movie (1).mp4", originalDestination, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutputPath_UsesNextNameForAnInFlightReservation()
    {
        using var sourceDirectory = new TemporaryDirectory();
        using var secondSourceDirectory = new TemporaryDirectory();
        using var outputDirectory = new TemporaryDirectory();
        var firstSourcePath = Path.Combine(sourceDirectory.Path, "movie.mp4");
        var secondSourcePath = Path.Combine(secondSourceDirectory.Path, "movie.mp4");
        File.WriteAllText(firstSourcePath, "first");
        File.WriteAllText(secondSourcePath, "second");
        var settings = new AppSettings
        {
            OutputLocation = OutputLocationMode.SelectedDirectory,
            OutputDirectory = outputDirectory.Path,
            PreserveDirectoryStructure = false,
            OutputSuffix = "_compressed"
        };
        var service = new OutputPathService();
        var first = service.GetOutputPath(Media(fullPath: firstSourcePath), settings, sourceDirectory.Path);
        var second = service.GetOutputPath(Media(fullPath: secondSourcePath), settings, secondSourceDirectory.Path, [first]);

        Assert.EndsWith("movie_compressed.mp4", first, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("movie_compressed (1).mp4", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsService_RoundTripsRulesAndCompressionSettingsAsJson()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var settings = new AppSettings
        {
            LastDirectory = @"D:\视频",
            CompressionMode = CompressionMode.TargetSize,
            TargetSize = "1.5 GB",
            OutputSuffix = "_1080P",
            CompressionConcurrency = 3
        };
        settings.Rules.Add(new CompressionRule
        {
            Field = RuleField.VideoCodec,
            Comparison = RuleComparison.NotEqual,
            Value = "hevc"
        });

        var service = new SettingsService(settingsPath);
        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal(settings.LastDirectory, loaded.LastDirectory);
        Assert.Equal(CompressionMode.TargetSize, loaded.CompressionMode);
        Assert.Equal("1.5 GB", loaded.TargetSize);
        Assert.Equal(1.5, loaded.TargetSizeValue);
        Assert.Equal(TargetSizeUnit.Gigabytes, loaded.TargetSizeUnit);
        Assert.Equal("_1080P", loaded.OutputSuffix);
        Assert.Equal(3, loaded.CompressionConcurrency);
        Assert.Single(loaded.Rules);
        Assert.Equal(RuleField.VideoCodec, loaded.Rules[0].Field);
    }

    [Fact]
    public void Planner_AddsDownscaleAndFpsOnlyWhenSourceExceedsLimit()
    {
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Bitrate,
            TargetVideoBitrateMbps = 6,
            ResolutionLimit = ResolutionLimitPreset.FullHd1080p,
            FpsLimit = FpsLimitPreset.Fps60,
            VideoEncoder = VideoEncoder.Libx264
        };
        var plan = new CompressionPlanner().CreatePlan(Media(width: 3840, height: 2160, frameRate: 120), settings);
        var args = plan.BuildArguments("input.mp4", "output.mp4", false).ToArray();
        var filter = args[Array.IndexOf(args, "-vf") + 1];

        Assert.Contains("min(iw,1920)", filter);
        Assert.Contains("fps=fps=60", filter);
        Assert.Contains("libx264", args);
    }

    [Fact]
    public void Planner_UsesOnlyTheSelectedCompressionModeParameter()
    {
        var media = Media();
        var planner = new CompressionPlanner();

        var crf = planner.CreatePlan(media, new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 29,
            TargetVideoBitrateMbps = 9,
            TargetSize = "1 GB",
            EncodingPreset = "slow"
        });
        var crfArguments = crf.BuildArguments("input.mp4", "output.mp4", false).ToArray();
        Assert.Contains("-crf", crfArguments);
        Assert.DoesNotContain("-b:v", crfArguments);
        Assert.Contains("slow", crfArguments);

        var bitrate = planner.CreatePlan(media, new AppSettings
        {
            CompressionMode = CompressionMode.Bitrate,
            Crf = 10,
            TargetVideoBitrateMbps = 5
        });
        var bitrateArguments = bitrate.BuildArguments("input.mp4", "output.mp4", false).ToArray();
        Assert.Contains("-b:v", bitrateArguments);
        Assert.DoesNotContain("-crf", bitrateArguments);

        var targetSettings = new AppSettings
        {
            CompressionMode = CompressionMode.TargetSize,
            Crf = 10,
            TargetVideoBitrateMbps = 5,
            TargetSize = "700 MB"
        };
        var target = new TargetSizeCalculator().Calculate(targetSettings.TargetSize, media, targetSettings.AudioMode, targetSettings.AudioBitrateKbps);
        var targetPlan = planner.CreatePlan(media, targetSettings, target);
        var targetArguments = targetPlan.BuildArguments("input.mp4", "output.mp4", false, "passlog").ToArray();
        Assert.Contains("-b:v", targetArguments);
        Assert.DoesNotContain("-crf", targetArguments);
    }

    [Fact]
    public void LegacyTargetSizeText_MigratesToSeparateValueAndUnit()
    {
        var megabytes = new AppSettings { TargetSize = "700 MB" };
        var gigabytes = new AppSettings { TargetSize = "1.5 GB" };

        Assert.Equal(700, megabytes.TargetSizeValue);
        Assert.Equal(TargetSizeUnit.Megabytes, megabytes.TargetSizeUnit);
        Assert.Equal(1.5, gigabytes.TargetSizeValue);
        Assert.Equal(TargetSizeUnit.Gigabytes, gigabytes.TargetSizeUnit);
    }

    [Fact]
    public void SafeFileService_TemporaryOutputNeverReusesFinalName()
    {
        using var directory = new TemporaryDirectory();
        var final = Path.Combine(directory.Path, "movie.mp4");
        var service = new SafeFileService(new FFprobeService());

        var temporary = service.CreateTemporaryOutputPath(final);

        Assert.False(string.Equals(final, temporary, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(".mp4", Path.GetExtension(temporary), StringComparer.OrdinalIgnoreCase);
    }

    private static VideoFileInfo Media(
        string? fullPath = null,
        long fileSizeBytes = 2_000_000_000,
        double durationSeconds = 600,
        int width = 1920,
        int height = 1080,
        double frameRate = 60,
        int audioTrackCount = 1,
        long? audioBitrateBps = 192_000) => new()
    {
        FileName = Path.GetFileName(fullPath ?? "movie.mp4"),
        FullPath = fullPath ?? Path.Combine(Path.GetTempPath(), "movie.mp4"),
        Extension = ".mp4",
        FileSizeBytes = fileSizeBytes,
        DurationSeconds = durationSeconds,
        VideoCodec = "h264",
        VideoBitrateBps = 12_000_000,
        TotalBitrateBps = 12_192_000,
        Width = width,
        Height = height,
        FrameRate = frameRate,
        AudioCodec = audioTrackCount == 0 ? null : "aac",
        AudioBitrateBps = audioBitrateBps,
        AudioTrackCount = audioTrackCount
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CloudLightVideoCompressorTests", Guid.NewGuid().ToString("N"));
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
