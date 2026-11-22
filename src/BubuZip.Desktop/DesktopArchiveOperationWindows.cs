using EclipseUI;
using System.Runtime.InteropServices;

namespace BubuZip.Desktop;

/// <summary>Runs one archive operation in a separate native window on the owning UI thread.</summary>
internal sealed class DesktopArchiveOperationWindows(Application application, IDesktopServices desktop) : IArchiveOperationWindows
{
    public ArchiveOperationOutcome Show(ArchiveOperationSpec operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var window = new ArchiveOperationWindow
        {
            Spec = operation,
            Desktop = desktop,
            CloseOnEscape = false,
            MaxTicks = application.MainWindow?.MaxTicks,
            BackgroundRequested = OperatingSystem.IsWindows() ? MinimizeActiveWindow : null,
        };
        application.ShowDialog(window);
        // Cancellation during shutdown is collected before reading the final result.
        return window.Result;
    }

    internal static void MinimizeActiveWindow()
    {
        var handle = GetActiveWindow();
        if (handle == 0) return;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == Environment.ProcessId) ShowWindow(handle, 6);
    }

    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
