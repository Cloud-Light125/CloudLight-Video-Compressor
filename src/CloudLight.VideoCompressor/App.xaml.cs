using System.Configuration;
using System.Data;
using System.Windows;

namespace CloudLight.VideoCompressor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    // Inno Setup uses this stable mutex name to ask the user to close the application before an update or uninstall.
    // It is intentionally not used to enforce single-instance behavior.
    private static readonly System.Threading.Mutex InstallerDetectionMutex = new(false, "CloudLightVideoCompressor-17BD4C51-B1E9-41C6-8818-11A70CE1ACC9");

    protected override void OnExit(ExitEventArgs e)
    {
        // The normal window path has already cancelled and bounded its waits. This safety net only targets
        // FFmpeg/ffprobe instances that this application registered itself.
        Services.MediaProcessRegistry.TerminateAll();
        InstallerDetectionMutex.Dispose();
        base.OnExit(e);
    }
}
