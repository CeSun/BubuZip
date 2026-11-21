using EclipseUI;

namespace BubuZip;

internal static class ArchiveWindowClose
{
    public static void Request(Window window)
    {
        if (window.IsDisposed) return;
        // The native message pump may still have pointer/focus events queued for
        // this window. Tear down the UI only after that batch has unwound.
        if (SynchronizationContext.Current is UiSynchronizationContext context)
            context.Post(static state => ((Window)state!).Dispose(), window);
        else window.Dispose();
    }
}
