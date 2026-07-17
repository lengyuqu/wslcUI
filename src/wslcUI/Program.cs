using Microsoft.UI.Xaml;

namespace wslcUI;

/// <summary>
/// WinUI 3 unpackaged entry point. The Windows App SDK generates an equivalent
/// Program when &lt;EnableDefaultProgramFile&gt; is left true; we provide our own
/// so the SynchronizationContext is wired for async MVVM flows.
/// </summary>
public class Program
{
    [System.STAThread]
    private static void Main()
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
}
