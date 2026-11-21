using EclipseUI;

namespace EclipseArchive.Desktop;

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
        };
        application.ShowDialog(window);
        // Cancellation during shutdown is collected before reading the final result.
        return window.Result;
    }
}
