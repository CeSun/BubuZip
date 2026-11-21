using System.Runtime.Versioning;
using EclipseUI;
using EclipseUI.Platform.MacOS;

namespace EclipseArchive.Desktop;

/// <summary>Receives the initial Finder document before handing the prepared window to EclipseUI.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacArchiveLifetime(MacOpenDocuments documents) : IApplicationLifetime
{
    public int Run(Application app, string[] args)
    {
        var window = app.MainWindow as MainWindow
            ?? throw new InvalidOperationException("EclipseArchive.MainWindow was not set.");
        MacOSWindow? native = null;
        Exception? failure = null;
        try
        {
            // Keep the Cocoa window hidden until the launch document selects browser
            // or operation-only presentation. EclipseUI owns its host and window loop.
            native = new MacOSWindow("BubuZip", 960, 640);
            while (!documents.HasDocuments && (!documents.IsLaunchCompleted || !documents.IsDefaultLaunch))
            {
                if (!native.PumpMessages()) return 0;
                if (!documents.HasDocuments && (!documents.IsLaunchCompleted || !documents.IsDefaultLaunch)) Thread.Sleep(1);
            }
            window.PrepareInitialRequests();
            return native.RunWindow(app, window);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            List<Exception>? cleanupFailures = null;
            void CleanUp(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            }
            // The framework releases UI/host/surface; the borrowed Cocoa window
            // stays owned by this bootstrap, including early/failed Finder launches.
            CleanUp(window);
            CleanUp(native);
            if (cleanupFailures is not null)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Finder launch cleanup failed.", cleanupFailures);
            }
        }
    }
}
