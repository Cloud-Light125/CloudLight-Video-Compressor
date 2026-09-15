using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CloudLight.VideoCompressor.Models;
using CloudLight.VideoCompressor.Services;
using CloudLight.VideoCompressor.ViewModels;

namespace CloudLight.VideoCompressor.Tests;

public sealed partial class CompressionTaskXamlBindingTests
{
    private static readonly (string Element, string Property)[] RiskyBindingTargets =
    [
        ("TextBox", "Text"),
        ("Slider", "Value"),
        ("ProgressBar", "Value"),
        ("ToggleButton", "IsChecked"),
        ("CheckBox", "IsChecked"),
        ("ComboBox", "SelectedValue"),
        ("ComboBox", "SelectedItem"),
        ("DatePicker", "SelectedDate"),
        ("ListBox", "SelectedItem"),
        ("DataGrid", "SelectedItem")
    ];

    [Fact]
    public void CommandPreviewDisplay_IsReadOnlyAndBoundOneWay()
    {
        var property = typeof(CompressionTaskEntry).GetProperty(nameof(CompressionTaskEntry.CommandPreviewDisplay));

        Assert.NotNull(property);
        Assert.Null(property!.GetSetMethod(nonPublic: true));

        var textBox = LoadTaskWindowXaml()
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBox" &&
                BindingPath(element.Attribute("Text")?.Value) == nameof(CompressionTaskEntry.CommandPreviewDisplay));

        Assert.Equal("True", textBox.Attribute("IsReadOnly")?.Value);
        Assert.Equal("OneWay", BindingMode(textBox.Attribute("Text")!.Value));
    }

    [Fact]
    public void ReadOnlyProperties_OnRiskyTaskPageTargets_AreExplicitlyOneWay()
    {
        var sourceTypes = new[] { typeof(CompressionTaskEntry), typeof(CompressionTaskViewModel) };
        var violations = new List<string>();

        foreach (var element in LoadTaskWindowXaml().Descendants())
        {
            foreach (var (_, targetProperty) in RiskyBindingTargets.Where(target => target.Element == element.Name.LocalName))
            {
                var binding = element.Attribute(targetProperty)?.Value;
                var path = BindingPath(binding);
                if (path is null)
                {
                    continue;
                }

                var sourceProperty = sourceTypes
                    .Select(type => type.GetProperty(path, BindingFlags.Instance | BindingFlags.Public))
                    .FirstOrDefault(property => property is not null);
                if (sourceProperty is null || sourceProperty.SetMethod?.IsPublic == true)
                {
                    continue;
                }

                if (!string.Equals(BindingMode(binding!), "OneWay", StringComparison.Ordinal))
                {
                    violations.Add($"{element.Name.LocalName}.{targetProperty} -> {sourceProperty.DeclaringType!.Name}.{path}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "只读属性绑定到可能回写的任务页属性时必须显式使用 Mode=OneWay：" +
            string.Join("；", violations));
    }

    [Fact]
    public void CompressionTaskWindow_CreatesWithCommandPreviewBinding()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            global::CloudLight.VideoCompressor.CompressionTaskWindow? window = null;
            try
            {
                var source = new VideoFileInfo
                {
                    FileName = "binding-smoke.mp4",
                    FullPath = Path.Combine(Path.GetTempPath(), "binding-smoke.mp4"),
                    Extension = ".mp4",
                    FileSizeBytes = 10_000_000,
                    DurationSeconds = 60,
                    VideoCodec = "h264",
                    VideoBitrateBps = 4_000_000,
                    TotalBitrateBps = 4_192_000,
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 30,
                    AudioCodec = "aac",
                    AudioBitrateBps = 192_000,
                    AudioTrackCount = 1
                };
                var plan = new CompressionPlan(
                    false,
                    VideoEncoder.Libx265,
                    CompressionMode.Crf,
                    28,
                    "medium",
                    null,
                    null,
                    null,
                    AudioMode.Copy,
                    192,
                    ".mp4",
                    [],
                    TargetCodec: VideoCodecKind.H265,
                    SourcePath: source.FullPath,
                    TargetPath: Path.Combine(Path.GetTempPath(), "binding-smoke-compressed.mp4"));
                var entry = new CompressionTaskEntry(
                    source,
                    plan,
                    ConditionEvaluationResult.Pending,
                    new CompressionPlanComparison([]));
                var session = new CompressionTaskSession([entry], new AppSettings(), Path.GetTempPath());
                var probe = new FFprobeService();
                var workflow = new CompressionWorkflowService(
                    new RuleEngine(),
                    probe,
                    new FFmpegService(),
                    new CompressionPlanner(),
                    new TargetSizeCalculator(),
                    new OutputPathService(),
                    new SafeFileService(probe));

                window = new global::CloudLight.VideoCompressor.CompressionTaskWindow(
                    session,
                    workflow,
                    new FFmpegTools("ffmpeg.exe", "ffprobe.exe"));
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.Show();
                window.UpdateLayout();

                Assert.Same(entry, window.ViewModel.SelectedEntry);
                Assert.False(window.ViewModel.HasStarted);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                window?.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "压缩任务窗口 smoke test 超时。");
        failure?.Throw();
    }

    private static XElement LoadTaskWindowXaml() =>
        XElement.Load(Path.Combine(FindRepositoryRoot(), "src", "CloudLight.VideoCompressor", "CompressionTaskWindow.xaml"));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudLight.VideoCompressor.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到 CloudLight.VideoCompressor.sln，无法审计任务页 XAML。");
    }

    private static string? BindingPath(string? binding)
    {
        if (binding is null)
        {
            return null;
        }

        var match = BindingPathPattern().Match(binding);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? BindingMode(string binding)
    {
        var match = BindingModePattern().Match(binding);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\{Binding\s+(?:Path\s*=\s*)?([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex BindingPathPattern();

    [GeneratedRegex(@"(?:^|,)\s*Mode\s*=\s*(OneWay|TwoWay|OneTime|OneWayToSource)\s*(?:,|\})", RegexOptions.CultureInvariant)]
    private static partial Regex BindingModePattern();
}
