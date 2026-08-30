using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class RuleEngineTests
{
    private readonly RuleEngine _engine = new();

    [Fact]
    public void FileSizeRule_SkipsFileBelowThreshold()
    {
        var result = _engine.Evaluate(Media(fileSizeBytes: 800L * 1024 * 1024),
        [
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "1 GB" }
        ]);

        Assert.False(result.IsMatch);
        Assert.Contains("已跳过", result.Summary);
        Assert.Contains("文件大小", result.Details.Single());
    }

    [Fact]
    public void VideoBitrateRule_UsesProbedBitrate()
    {
        var result = _engine.Evaluate(Media(videoBitrate: 12_000_000),
        [
            new CompressionRule { Field = RuleField.VideoBitrate, Comparison = RuleComparison.GreaterThan, Value = "10 Mbps" }
        ]);

        Assert.True(result.IsMatch);
        Assert.True(_engine.RequiresProbe([new CompressionRule { Field = RuleField.VideoBitrate }]));
    }

    [Fact]
    public void AndAndOr_AreEvaluatedInRuleOrder()
    {
        var source = Media(fileSizeBytes: 2L * 1024 * 1024 * 1024, videoBitrate: 8_000_000);
        var andRules = new[]
        {
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "1 GB" },
            new CompressionRule { JoinWithPrevious = RuleJoin.And, Field = RuleField.VideoBitrate, Comparison = RuleComparison.GreaterThan, Value = "10 Mbps" }
        };
        var orRules = new[]
        {
            andRules[0],
            new CompressionRule { JoinWithPrevious = RuleJoin.Or, Field = RuleField.VideoBitrate, Comparison = RuleComparison.GreaterThan, Value = "10 Mbps" }
        };

        Assert.False(_engine.Evaluate(source, andRules).IsMatch);
        Assert.True(_engine.Evaluate(source, orRules).IsMatch);
    }

    [Fact]
    public void DirectModeRuleProbeRequirement_IsFalseForFileSystemOnlyRules()
    {
        var rules = new[]
        {
            new CompressionRule { Field = RuleField.FileSize, Comparison = RuleComparison.GreaterThan, Value = "1 GB" },
            new CompressionRule { JoinWithPrevious = RuleJoin.And, Field = RuleField.Extension, Comparison = RuleComparison.Equal, Value = ".mp4" }
        };

        Assert.False(_engine.RequiresProbe(rules));
    }

    [Fact]
    public void NumericRuleValues_UseDedicatedUnitsAndMigrateLegacyText()
    {
        var bitrate = new CompressionRule { Field = RuleField.VideoBitrate, Value = "1024 Kbps" };
        var size = new CompressionRule { Field = RuleField.FileSize, Value = "1024 MB" };
        var duration = new CompressionRule { Field = RuleField.Duration, Value = "90s" };

        Assert.Equal("Mbps", bitrate.Unit);
        Assert.Equal("1.024", bitrate.Value);
        Assert.Equal("1.024 Mbps", bitrate.GetComparisonValue());
        Assert.Equal("GB", size.Unit);
        Assert.Equal("1", size.Value);
        Assert.Equal("分钟", duration.Unit);
        Assert.Equal("1.5", duration.Value);
        Assert.Equal("1.5m", duration.GetComparisonValue());

        var result = _engine.Evaluate(Media(videoBitrate: 1_500_000), [bitrate]);
        Assert.True(result.IsMatch);
    }

    private static VideoFileInfo Media(long fileSizeBytes = 2_000_000_000, long? videoBitrate = 12_000_000) => new()
    {
        FileName = "movie.mp4",
        FullPath = Path.Combine(Path.GetTempPath(), "movie.mp4"),
        Extension = ".mp4",
        FileSizeBytes = fileSizeBytes,
        DurationSeconds = 600,
        VideoBitrateBps = videoBitrate,
        TotalBitrateBps = videoBitrate is null ? null : videoBitrate + 192_000,
        VideoCodec = "h264",
        Width = 1920,
        Height = 1080,
        FrameRate = 60,
        AudioCodec = "aac",
        AudioBitrateBps = 192_000,
        AudioTrackCount = 1
    };
}
