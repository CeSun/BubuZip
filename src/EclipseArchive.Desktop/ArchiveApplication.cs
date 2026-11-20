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
            ShellRequestDecoder = OperatingSystem.IsMacOS() ? new MacFinderRequestFile().ReadIfRequest : null,
            OperationOnly = options.ShellRequest?.Action is { } action && action != ArchiveShellAction.Open,
            InitialPath = options.InitialPath,
            MaxTicks = options.FrameLimit,
            CloseOnEscape = false,
        };
        if (options.ShellRequest is not null) window.RequestShellAction(options.ShellRequest);
        else foreach (var path in options.ArchivePaths) window.RequestOpenArchive(path);
        if (options.OpenSettings) window.RequestShowSettings();
        if (!OperatingSystem.IsMacOS()) window.PrepareInitialRequests();
        MainWindow = window;
        window.OperationWindows = new DesktopArchiveOperationWindows(this, window.Desktop);
    }
}
