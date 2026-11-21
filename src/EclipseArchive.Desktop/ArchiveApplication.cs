using EclipseArchive.Core;
using EclipseUI;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseArchive.Desktop;

internal sealed record DesktopLaunchOptions(int? FrameLimit, string InitialPath, IReadOnlyList<string> ArchivePaths,
    ArchiveShellRequest? ShellRequest = null, bool OpenSettings = false, bool CreateArchive = false);

public sealed class ArchiveApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<DesktopLaunchOptions>();
        var appearance = Services.GetRequiredService<ArchiveAppearance>();
        var window = new MainWindow
        {
            Appearance = appearance,
            Theme = appearance,
            ApplyTheme = SetTheme,
            Engine = Services.GetRequiredService<IArchiveEngine>(),
            FileService = Services.GetRequiredService<ArchiveFileService>(),
            EditService = Services.GetRequiredService<ArchiveEditService>(),
            Desktop = Services.GetRequiredService<IDesktopServices>(),
            ShellIntegration = Services.GetService<IShellIntegrationService>(),
            ShellRequestDecoder = OperatingSystem.IsMacOS() ? new MacFinderRequestFile().ReadIfRequest : null,
            OperationOnly = options.CreateArchive || options.ShellRequest?.Action is { } action && action != ArchiveShellAction.Open,
            StartCreating = options.CreateArchive,
            ArchiveWindows = new DesktopArchiveWindows(),
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
        window.SettingsWindows = new DesktopArchiveSettingsWindows(this);
    }
}
