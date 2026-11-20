using EclipseArchive.Core;
using EclipseUI;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseArchive.Desktop;

internal sealed record DesktopLaunchOptions(int? FrameLimit, string InitialPath, IReadOnlyList<string> ArchivePaths,
    ArchiveShellRequest? ShellRequest = null, bool OpenSettings = false);

public sealed class ArchiveApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<DesktopLaunchOptions>();
        var window = new MainWindow
        {
            ApplyTheme = SetTheme,
            Engine = Services.GetRequiredService<IArchiveEngine>(),
            FileService = Services.GetRequiredService<ArchiveFileService>(),
            EditService = Services.GetRequiredService<ArchiveEditService>(),
            Desktop = Services.GetRequiredService<IDesktopServices>(),
            ShellIntegration = Services.GetService<IShellIntegrationService>(),
            InitialPath = options.InitialPath,
            MaxTicks = options.FrameLimit,
            CloseOnEscape = false,
        };
        if (options.ShellRequest is not null) window.RequestShellAction(options.ShellRequest);
        else foreach (var path in options.ArchivePaths) window.RequestOpenArchive(path);
        if (options.OpenSettings) window.RequestShowSettings();
        MainWindow = window;
    }
}
