using System.Runtime.Versioning;
using EclipseUI;
using EclipseUI.Platform.MacOS;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseArchive.Desktop;

/// <summary>Routes initial Finder documents before any application window becomes visible.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacArchiveLifetime(MacOpenDocuments documents) : IApplicationLifetime
{
    public int Run(Application app, string[] args)
    {
        var window = app.MainWindow as MainWindow
            ?? throw new InvalidOperationException("EclipseArchive.MainWindow was not set.");
        MacOSWindow? native = null;
        ISurfacePresenter? presenter = null;
        WindowHost? host = null;
        Exception? failure = null;
        try
        {
            // SDL's native window is born hidden. Creating it initializes Cocoa,
            // allowing the first Apple event to arrive without building/showing
            // the browser UI while the launch purpose is still unknown.
            native = new MacOSWindow("BubuZip", 960, 640);
            while (!documents.HasDocuments && (!documents.IsLaunchCompleted || !documents.IsDefaultLaunch))
            {
                if (!native.PumpMessages()) return 0;
                if (!documents.HasDocuments && (!documents.IsLaunchCompleted || !documents.IsDefaultLaunch)) Thread.Sleep(1);
            }

            // Decode and select the presentation synchronously. Operations remain
            // queued until WindowHost.Run installs its normal UI synchronization context.
            window.PrepareInitialRequests();
            window.EnsureInitialized();
            var content = window.Content ?? throw new InvalidOperationException("Window.Content was not set.");
            var title = window.Title;
            var width = window.Width > 0 ? window.Width : 960;
            var height = window.Height > 0 ? window.Height : 640;
            native.SetTitle(title);
            native.SetClientSize(width, height);

            var surfaceFactory = app.Services.GetRequiredService<ISurfaceFactory>();
            presenter = surfaceFactory.Create(native.SurfaceHandle,
                Math.Max(native.ClientWidth, 1), Math.Max(native.ClientHeight, 1));
            host = new WindowHost(native, presenter, content, new WindowHostOptions
            {
                Theme = window.Theme ?? app.DefaultTheme ?? ElementTheme.Light,
                TextPainter = app.Services.GetService<ITextPainter>(),
                MaxFramesPerSecond = window.MaxFramesPerSecond,
                AlwaysRender = window.AlwaysRender,
                AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
                CloseOnEscape = window.CloseOnEscape,
                IdleSleepMilliseconds = window.IdleSleepMilliseconds,
            }, window);
            app.Host = host;
            host.FrameTicked += (_, _) =>
            {
                // The framework's live updater setter is internal. Keep the same
                // window properties in sync through the public platform methods.
                if (window.Title != title)
                {
                    title = window.Title;
                    native.SetTitle(title);
                }
                var nextWidth = window.Width > 0 ? window.Width : 960;
                var nextHeight = window.Height > 0 ? window.Height : 640;
                if (nextWidth != width || nextHeight != height)
                {
                    width = nextWidth;
                    height = nextHeight;
                    native.SetClientSize(width, height);
                }
            };
            if (window.MaxTicks is not null)
                Console.WriteLine($"presentation: {(window.OperationOnly ? "operation" : "browser")}; initial window: {width}x{height}");
            host.Run(window.MaxTicks);
            return 0;
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
                catch (Exception error) { (cleanupFailures ??= new()).Add(error); }
            }

            // UI hooks may still use the native window while their resources are released.
            CleanUp(window);
            app.Host = null;
            CleanUp(host);
            CleanUp(presenter);
            CleanUp(native);
            if (cleanupFailures is not null)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Window lifetime cleanup failed.", cleanupFailures);
            }
        }
    }
}
