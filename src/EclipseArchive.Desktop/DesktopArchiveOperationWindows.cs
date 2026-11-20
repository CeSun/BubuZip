using System.Runtime.Versioning;
using EclipseUI;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;
using LinuxHost = EclipseUI.Platform.Linux.WindowHost;
using LinuxHostOptions = EclipseUI.Platform.Linux.WindowHostOptions;
using MacHost = EclipseUI.Platform.MacOS.WindowHost;
using MacHostOptions = EclipseUI.Platform.MacOS.WindowHostOptions;
using WindowsHost = EclipseUI.Platform.Windows.WindowHost;
using WindowsHostOptions = EclipseUI.Platform.Windows.WindowHostOptions;

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
        var resources = new WindowResources();
        var previousHost = application.Host;
        var previousClipboard = Clipboard.Current;
        var parentContent = application.MainWindow?.Content;
        var parentEnabled = parentContent?.IsEnabled ?? true;
        Exception? failure = null;
        try
        {
            if (parentContent is not null) parentContent.IsEnabled = false;
            window.EnsureInitialized();
            if (window.Content is null) throw new InvalidOperationException("操作窗口缺少内容。");
            var surfaceFactory = application.Services.GetRequiredService<ISurfaceFactory>();
            var textPainter = application.Services.GetService<ITextPainter>();
            if (OperatingSystem.IsMacOS()) RunMacOS(window, resources, surfaceFactory, textPainter);
            else if (OperatingSystem.IsWindows()) RunWindows(window, resources, surfaceFactory, textPainter);
            else if (OperatingSystem.IsLinux()) RunLinux(window, resources, surfaceFactory, textPainter);
            else throw new PlatformNotSupportedException("当前系统不支持独立操作窗口。");
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

            // The native event batch must finish before the UI is disposed. Cancel/join
            // here, after the host loop returns and while its surface still exists.
            CleanUp(window);
            CleanUp(resources.Host);
            CleanUp(resources.Surface);
            CleanUp(resources.Native);
            application.Host = previousHost;
            Clipboard.Current = previousClipboard;
            if (parentContent is { IsDisposed: false }) parentContent.IsEnabled = parentEnabled;
            if (cleanupFailures is not null)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("独立操作窗口未能完整关闭。", cleanupFailures);
            }
        }
        // A tick-limited run is cancelled during disposal; read its final result afterwards.
        return window.Result;
    }

    private IHostTheme Theme(ArchiveOperationWindow window)
        => window.Theme ?? application.DefaultTheme ?? ElementTheme.Light;

    private static int Width(ArchiveOperationWindow window) => window.Width > 0 ? (int)window.Width : 560;
    private static int Height(ArchiveOperationWindow window) => window.Height > 0 ? (int)window.Height : 280;

    [SupportedOSPlatform("macos")]
    private void RunMacOS(ArchiveOperationWindow window, WindowResources resources,
        ISurfaceFactory surfaceFactory, ITextPainter? textPainter)
    {
        var native = new EclipseUI.Platform.MacOS.MacOSWindow(window.Title, Width(window), Height(window));
        resources.Native = native;
        native.Show();
        var surface = surfaceFactory.Create(native.SurfaceHandle, Math.Max(native.ClientWidth, 1), Math.Max(native.ClientHeight, 1));
        resources.Surface = surface;
        var host = new MacHost(native, surface, window.Content!, new MacHostOptions
        {
            Theme = Theme(window),
            TextPainter = textPainter,
            MaxFramesPerSecond = window.MaxFramesPerSecond,
            AlwaysRender = window.AlwaysRender,
            AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
            CloseOnEscape = false,
            IdleSleepMilliseconds = window.IdleSleepMilliseconds,
        }, window);
        resources.Host = host;
        application.Host = host;
        host.FrameTicked += (_, _) => { if (!native.IsClosed) native.SetTitle(window.Title); };
        host.Run(window.MaxTicks);
    }

    [SupportedOSPlatform("windows")]
    private void RunWindows(ArchiveOperationWindow window, WindowResources resources,
        ISurfaceFactory surfaceFactory, ITextPainter? textPainter)
    {
        // Closing a secondary window must not post WM_QUIT to its parent's message loop.
        var native = new EclipseUI.Platform.Windows.Win32Window(window.Title, Width(window), Height(window), quitOnDestroy: false);
        resources.Native = native;
        var surface = surfaceFactory.Create(native.Handle, Math.Max(native.ClientWidth, 1), Math.Max(native.ClientHeight, 1));
        resources.Surface = surface;
        var host = new WindowsHost(native, surface, window.Content!, new WindowsHostOptions
        {
            Theme = Theme(window),
            TextPainter = textPainter,
            MaxFramesPerSecond = window.MaxFramesPerSecond,
            AlwaysRender = window.AlwaysRender,
            AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
            CloseOnEscape = false,
            IdleSleepMilliseconds = window.IdleSleepMilliseconds,
        }, window);
        resources.Host = host;
        application.Host = host;
        host.FrameTicked += (_, _) => { if (!native.IsClosed) native.SetTitle(window.Title); };
        host.Run(window.MaxTicks);
    }

    [SupportedOSPlatform("linux")]
    private void RunLinux(ArchiveOperationWindow window, WindowResources resources,
        ISurfaceFactory surfaceFactory, ITextPainter? textPainter)
    {
        var native = new EclipseUI.Platform.Linux.LinuxWindow(window.Title, Width(window), Height(window));
        resources.Native = native;
        // Wayland must configure the selected toplevel before creating its GPU surface.
        native.Show();
        var surface = surfaceFactory.Create(native.SurfaceHandle, Math.Max(native.ClientWidth, 1), Math.Max(native.ClientHeight, 1));
        resources.Surface = surface;
        var host = new LinuxHost(native, surface, window.Content!, new LinuxHostOptions
        {
            Theme = Theme(window),
            TextPainter = textPainter,
            MaxFramesPerSecond = window.MaxFramesPerSecond,
            AlwaysRender = window.AlwaysRender,
            AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
            CloseOnEscape = false,
            IdleSleepMilliseconds = window.IdleSleepMilliseconds,
        }, window);
        resources.Host = host;
        application.Host = host;
        host.FrameTicked += (_, _) => { if (!native.IsClosed) native.SetTitle(window.Title); };
        host.Run(window.MaxTicks);
    }

    private sealed class WindowResources
    {
        public IDisposable? Native { get; set; }
        public ISurfacePresenter? Surface { get; set; }
        public IDisposable? Host { get; set; }
    }
}
