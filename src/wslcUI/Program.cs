using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace wslcUI;

/// <summary>
/// WinUI 3 unpackaged entry point. The Windows App SDK generates an equivalent
/// Program when &lt;EnableDefaultProgramFile&gt; is left true; we provide our own
/// so the SynchronizationContext is wired for async MVVM flows.
///
/// Unpackaged apps MUST bootstrap the Windows App SDK runtime before any WinUI
/// API is touched (find + activate the installed WindowsAppRuntime 1.6 package).
/// The package's auto-initializer is disabled (WindowsAppSdkBootstrapInitialize=false
/// in the csproj) so we control it here and can surface the HRESULT on failure.
/// </summary>
public class Program
{
    [System.STAThread]
    private static void Main()
    {
        // Bootstrap Windows App SDK runtime 1.6 (majorMinor 0x00010006).
        // This matches the WindowsAppRuntime.1.6 packages installed on the machine
        // (e.g. 6000.401.2352 / 6000.519.329). Write the HRESULT to a file next to
        // the exe so startup failures are diagnosable without a console.
        const uint majorMinor = 0x00010006;
        const string versionTag = ""; // stable release channel
        var minVersion = new PackageVersion(0x0001000600000000UL); // >= 1.6.0.0
        var baseDir = System.AppContext.BaseDirectory;

        if (!Bootstrap.TryInitialize(majorMinor, versionTag, minVersion,
                Bootstrap.InitializeOptions.OnNoMatch_ShowUI, out int hr))
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(baseDir, "bootstrap_error.txt"),
                $"Bootstrap.TryInitialize failed. HRESULT=0x{hr:X8} ({(uint)hr}) MajorMinor=0x{majorMinor:X8} Tag='{versionTag}' MinVersion={minVersion}");
            throw new System.InvalidOperationException($"Windows App SDK 1.6 bootstrap 失败 (0x{hr:X8})。请安装 Windows App Runtime 1.6。");
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
            Bootstrap.Shutdown();
        }
    }
}
