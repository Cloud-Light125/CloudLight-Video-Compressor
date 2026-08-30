using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;

namespace CloudLight.VideoCompressor.Tests;

public sealed class CompressionTaskTests
{
    [Fact]
    public async Task Session_ContainsOnlySelectedMatchingVideos()
    {
        var candidates = Enumerable.Range(0, 47)
            .Select(index =>
            {
                var item = new VideoTaskItem(Media($"video-{index:D2}.mp4", videoBitrate: index == 0 ? 14_000_000 : 6_000_000));
                item.ApplyConditionResult(new RuleEngine().Evaluate(item.Media, [
                    new CompressionRule
                    {
                        Field = RuleField.TotalBitrate,
                        Comparison = RuleComparison.GreaterThan,
                        Value = "10 Mbps"
                    }
                ]));
                return item;
            })
            .ToList();

        var unselected = new VideoTaskItem(Media("unselected.mp4", videoBitrate: 20_000_000));
        unselected.IsSelected = false;
        unselected.ApplyConditionResult(new RuleEngine().Evaluate(unselected.Media, []));
        candidates.Add(unselected);

        var session = await CreatePlanner().CreateSessionAsync(
            candidates,
            new AppSettings { CompressionMode = CompressionMode.Bitrate, TargetVideoBitrateMbps = 5 },
            Path.GetTempPath(),
            FakeTools(),
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        var entry = Assert.Single(session.Entries);
        Assert.Equal("video-00.mp4", entry.FileName);
    }

    [Fact]
    public async Task ManualMode_AlwaysCreatesPlanForMatchingVideo()
    {
        var item = MatchingItem(Media("manual.mp4", 20_000_000));
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Crf,
            Crf = 28
        };

        var session = await CreatePlanner().CreateSessionAsync(
            [item],
            settings,
            Path.GetTempPath(),
            FakeTools(),
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        var entry = Assert.Single(session.Entries);
        Assert.Null(entry.SmartDecision);
        Assert.Equal(CompressionMode.Crf, entry.Plan.Mode);
    }

    [Fact]
    public async Task SmartMode_ExcludesVideoThatPlannerSaysShouldBeSkipped()
    {
        var item = MatchingItem(Media("already-reasonable.mp4", fileSizeBytes: 225L * 1024 * 1024, videoBitrate: 3_000_000));
        var session = await CreatePlanner().CreateSessionAsync(
            [item],
            new AppSettings { CompressionMode = CompressionMode.SmartAutomatic },
            Path.GetTempPath(),
            FakeTools(),
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        Assert.Empty(session.Entries);
        Assert.Contains(session.PlanningNotes, note => note.Contains("智能跳过", StringComparison.Ordinal));
        Assert.Equal(VideoTaskStatus.Skipped, item.Status);
        Assert.Contains("智能跳过", item.StatusDetail);
        Assert.NotNull(item.SmartDecision);
    }

    [Fact]
    public async Task HardwareAutomatic_ResolvesQsvInPreview()
    {
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Bitrate,
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.HardwareAutomatic,
            TargetVideoBitrateMbps = 5
        };
        var session = await CreatePlanner().CreateSessionAsync(
            [MatchingItem(Media("qsv.mp4", videoBitrate: 30_000_000))],
            settings,
            Path.GetTempPath(),
            FakeTools(),
            Capabilities(VideoEncoder.HevcQsv),
            CancellationToken.None);

        var entry = Assert.Single(session.Entries);
        Assert.Equal(VideoEncoder.HevcQsv, entry.Plan.Encoder);
        Assert.Equal("Intel Quick Sync · H.265", entry.PlannedEncoderDisplay);
        Assert.Equal("hevc_qsv", entry.PlannedEncoderIdDisplay);
        Assert.Equal("GPU 硬件编码", entry.PlannedEncoderKindDisplay);
    }

    [Fact]
    public async Task Comparison_LabelsReducedAndUnchangedValuesExplicitly()
    {
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.Bitrate,
            TargetVideoBitrateMbps = 8,
            TargetVideoCodec = VideoCodecKind.H265,
            ResolutionLimit = ResolutionLimitPreset.FullHd1080p,
            FpsLimit = FpsLimitPreset.Fps30,
            AudioMode = AudioMode.Aac,
            AudioBitrateKbps = 128,
            EncoderSelection = EncoderSelectionMode.CpuSoftware
        };
        var session = await CreatePlanner().CreateSessionAsync(
            [MatchingItem(Media("compare.mp4", fileSizeBytes: 40_000_000, videoBitrate: 25_000_000, width: 3840, height: 2160, frameRate: 60))],
            settings,
            Path.GetTempPath(),
            FakeTools(),
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        var entry = Assert.Single(session.Entries);
        var rows = entry.Comparison.Parameters.ToDictionary(row => row.Parameter);
        Assert.Equal(CompressionParameterChangeType.Converted, rows["视频编码"].ChangeType);
        Assert.Equal(CompressionParameterChangeType.Reduced, rows["视频码率"].ChangeType);
        Assert.Equal(CompressionParameterChangeType.Reduced, rows["FPS"].ChangeType);
        Assert.Equal("保持", rows["音轨数量"].ChangeDisplay);
        Assert.Equal(CompressionParameterChangeType.Unchanged, rows["容器"].ChangeType);
        Assert.Contains("将降低", rows["音频码率"].ChangeDisplay);
    }

    [Fact]
    public async Task CrfPreview_DoesNotInventExactOutputSize()
    {
        var session = await CreatePlanner().CreateSessionAsync(
            [MatchingItem(Media("quality.mp4", fileSizeBytes: 30_000_000))],
            new AppSettings { CompressionMode = CompressionMode.Crf, Crf = 27 },
            Path.GetTempPath(),
            FakeTools(),
            EncoderCapabilitySet.SoftwareDefaults,
            CancellationToken.None);

        var entry = Assert.Single(session.Entries);
        Assert.Null(entry.Plan.EstimatedOutputSizeBytes);
        Assert.Equal("无法精确预估", entry.PlannedOutputSizeDisplay);
    }

    [Fact]
    public void Fallback_UpdatesActualEncoderButKeepsPlannedEncoder()
    {
        var plan = new CompressionPlan(
            false,
            VideoEncoder.HevcNvenc,
            CompressionMode.Bitrate,
            26,
            "medium",
            5_000_000,
            null,
            null,
            AudioMode.Copy,
            192,
            ".mp4",
            []);
        var entry = new CompressionTaskEntry(
            Media("fallback.mp4", fileSizeBytes: 30_000_000),
            plan,
            new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许"),
            new CompressionPlanComparison([]));

        entry.ApplyResult(new CompressionJobResult(
            VideoTaskStatus.Completed,
            "压缩完成",
            OutputPath: "fallback-compressed.mp4",
            OutputInfo: Media("fallback-compressed.mp4", fileSizeBytes: 10_000_000, videoBitrate: 6_000_000),
            Encoder: VideoEncoder.HevcQsv,
            FallbackReason: "NVIDIA NVENC 初始化失败，自动尝试 Intel QSV"));

        Assert.Equal(VideoEncoder.HevcNvenc, entry.Plan.Encoder);
        Assert.Equal(VideoEncoder.HevcQsv, entry.ActualEncoder);
        Assert.Equal("NVIDIA NVENC · H.265", entry.PlannedEncoderDisplay);
        Assert.Equal("实际编码方式：Intel Quick Sync · H.265", entry.ActualEncoderSummaryDisplay);
        Assert.Contains("Intel QSV", entry.FallbackReason);
        Assert.Equal(CompressionExecutionState.Completed, entry.ExecutionState);
    }

    [Fact]
    public void ResultRejected_UsesDedicatedUserFacingStateAndMessage()
    {
        var entry = new CompressionTaskEntry(
            Media("larger-result.mp4"),
            new CompressionPlan(false, VideoEncoder.Libx265, CompressionMode.Crf, 28, "medium", null, null, null, AudioMode.Copy, 192, ".mp4", [], TargetCodec: VideoCodecKind.H265),
            new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许"),
            new CompressionPlanComparison([]));

        entry.ApplyResult(new CompressionJobResult(
            VideoTaskStatus.Skipped,
            "已放弃结果：压缩后的文件未小于源文件。源文件已保留。",
            SourceInfo: entry.Source,
            FailureKind: CompressionFailureKind.ResultRejected,
            PlannedEncoder: VideoEncoder.Libx265));

        Assert.Equal("已放弃结果", entry.ExecutionStateDisplay);
        Assert.True(entry.HasResultRejection);
        Assert.Contains("未小于源文件", entry.FailureReasonDisplay);
        Assert.Contains("源文件已保留", entry.FailureReasonDisplay);
    }

    [Fact]
    public void ValidationFailure_UsesValidationLabelAndKeepsDiagnosticsAdvanced()
    {
        var entry = new CompressionTaskEntry(
            Media("invalid-result.mp4"),
            new CompressionPlan(false, VideoEncoder.Libx265, CompressionMode.Crf, 28, "medium", null, null, null, AudioMode.Copy, 192, ".mp4", [], TargetCodec: VideoCodecKind.H265),
            new ConditionEvaluationResult(ConditionResultState.AllAllowed, true, "全部允许", [], "全部允许"),
            new CompressionPlanComparison([]));

        entry.ApplyResult(new CompressionJobResult(
            VideoTaskStatus.Failed,
            "输出文件未检测到有效视频流。ffprobe stderr tail",
            SourceInfo: entry.Source,
            FailureKind: CompressionFailureKind.ValidationFailed,
            PlannedEncoder: VideoEncoder.Libx265));

        Assert.Equal("验证失败", entry.ExecutionStateDisplay);
        Assert.True(entry.IsValidationFailed);
        Assert.Contains("验证失败", entry.FailureReasonDisplay);
        Assert.Contains("有效视频流", entry.FailureReasonDisplay);
        Assert.Contains("stderr tail", entry.DiagnosticErrorDisplay);
    }

    [Fact]
    public void TargetSizeH265_UsesH265AndDoesNotForceLibx264()
    {
        var media = Media("target-h265.mp4", fileSizeBytes: 2_000_000_000, videoBitrate: 12_000_000);
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.TargetSize,
            TargetSize = "700 MB",
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.CpuSoftware
        };
        var targetSize = new TargetSizeCalculator().Calculate(settings.TargetSize, media, settings.AudioMode, settings.AudioBitrateKbps);
        var plan = new CompressionPlanner().CreatePlan(media, settings, targetSize);
        var arguments = plan.BuildArguments("input.mp4", "output.mp4", false, "passlog");

        Assert.Equal(VideoEncoder.Libx265, plan.Encoder);
        Assert.True(plan.IsTwoPass);
        Assert.Contains("libx265", arguments);
        Assert.DoesNotContain("libx264", arguments);
    }

    [Fact]
    public void TargetSizeH265Qsv_UsesHardwareBitrateModeWithoutTwoPass()
    {
        var media = Media("target-qsv.mp4", fileSizeBytes: 2_000_000_000, videoBitrate: 12_000_000);
        var settings = new AppSettings
        {
            CompressionMode = CompressionMode.TargetSize,
            TargetSize = "700 MB",
            TargetVideoCodec = VideoCodecKind.H265,
            EncoderSelection = EncoderSelectionMode.HardwareAutomatic
        };
        var targetSize = new TargetSizeCalculator().Calculate(settings.TargetSize, media, settings.AudioMode, settings.AudioBitrateKbps);
        var plan = new CompressionPlanner().CreatePlan(media, settings, targetSize, Capabilities(VideoEncoder.HevcQsv));
        var arguments = plan.BuildArguments("input.mp4", "output.mp4", false);

        Assert.Equal(VideoEncoder.HevcQsv, plan.Encoder);
        Assert.False(plan.IsTwoPass);
        Assert.Contains("hevc_qsv", arguments);
        Assert.Contains("目标大小模式", string.Join(Environment.NewLine, plan.Warnings));
        Assert.DoesNotContain("-pass", arguments);
    }

    private static CompressionTaskPlanner CreatePlanner()
    {
        var probe = new FFprobeService();
        return new CompressionTaskPlanner(
            new RuleEngine(),
            probe,
            new CompressionPlanner(),
            new TargetSizeCalculator(),
            new OutputPathService());
    }

    private static VideoTaskItem MatchingItem(VideoFileInfo media)
    {
        var item = new VideoTaskItem(media);
        item.ApplyConditionResult(new RuleEngine().Evaluate(media, []));
        return item;
    }

    private static EncoderCapabilitySet Capabilities(params VideoEncoder[] usableHardware) =>
        new(EncoderCatalog.Definitions.Select(definition =>
        {
            var usable = !definition.IsHardware || usableHardware.Contains(definition.Encoder);
            return new EncoderCapability(
                definition.Id,
                definition.DisplayName,
                definition.Encoder,
                definition.Codec,
                definition.Vendor,
                definition.IsHardware,
                true,
                usable,
                usable ? null : "测试中不可用");
        }));

    private static FFmpegTools FakeTools() => new("ffmpeg.exe", "ffprobe.exe");

    private static VideoFileInfo Media(
        string fileName,
        long fileSizeBytes = 2_000_000_000,
        long videoBitrate = 12_000_000,
        int width = 1920,
        int height = 1080,
        double frameRate = 30,
        double durationSeconds = 600) => new()
    {
        FileName = fileName,
        FullPath = Path.Combine(Path.GetTempPath(), fileName),
        Extension = ".mp4",
        FileSizeBytes = fileSizeBytes,
        DurationSeconds = durationSeconds,
        VideoCodec = "h264",
        VideoBitrateBps = videoBitrate,
        TotalBitrateBps = videoBitrate + 192_000,
        Width = width,
        Height = height,
        FrameRate = frameRate,
        AudioCodec = "aac",
        AudioBitrateBps = 192_000,
        AudioTrackCount = 1
    };
}
