using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace wslcUI;

/// <summary>
/// WinUI 3 unpackaged entry point. The Windows App SDK generates an equivalent
/// Program when &lt;EnableDefaultProgramFile&gt; is left true; we provide our own
/// so the SynchronizationContext is wired for async MVVM flows.
///
/// Unpackaged apps MUST bootstrap the Windows App SDK runtime before any WinUI
/// API is touched (find + activate the installed WindowsAppRuntime 2.4 package).
/// The package's auto-initializer is disabled (WindowsAppSdkBootstrapInitialize=false
/// in the csproj) so we control it here and can surface the HRESULT on failure.
/// </summary>
public class Program
{
    [System.STAThread]
    private static void Main()
    {
        // Bootstrap Windows App SDK runtime 2.4 (majorMinor 0x00020004).
        // This matches the WindowsAppRuntime.2 packages installed on the machine
        // (e.g. 2.4.0.0). Write the HRESULT to a file next to
        // the exe so startup failures are diagnosable without a console.
        const uint majorMinor = 0x00020004;
        const string versionTag = ""; // stable release channel
        var minVersion = new PackageVersion(0x0002000400000000UL); // >= 2.4.0.0
        var baseDir = System.AppContext.BaseDirectory;

        // Self-contained builds (WindowsAppSDKSelfContained=true) ship the Windows App
        // SDK runtime next to the exe and register it via a regfree-COM manifest, so the
        // bootstrapper must NOT run: activating the *system-installed* WindowsAppRuntime
        // package on top of the local copy loads two runtime versions into one process,
        // which fail-fasts inside CoreMessagingXP.dll (0xC0000602, verified 2026-10-06).
        var selfContained = System.IO.File.Exists(
            System.IO.Path.Combine(baseDir, "Microsoft.WindowsAppRuntime.dll"));

        if (!selfContained && !Bootstrap.TryInitialize(majorMinor, versionTag, minVersion,
                Bootstrap.InitializeOptions.OnNoMatch_ShowUI, out int hr))
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(baseDir, "bootstrap_error.txt"),
                $"Bootstrap.TryInitialize failed. HRESULT=0x{hr:X8} ({(uint)hr}) MajorMinor=0x{majorMinor:X8} Tag='{versionTag}' MinVersion={minVersion}");
            throw new System.InvalidOperationException($"Windows App SDK 2.4 bootstrap 失败 (0x{hr:X8})。请安装 Windows App Runtime 2.4。");
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start((p) =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        finally
        {
            if (!selfContained)
            {
                Bootstrap.Shutdown();
            }
        }
    }
}
