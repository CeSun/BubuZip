using EclipseArchive.Core;
using EclipseUI;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseArchive.Desktop;

internal sealed record DesktopLaunchOptions(int? FrameLimit, string InitialPath);

public sealed class ArchiveApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var options = Services.GetRequiredService<DesktopLaunchOptions>();
        MainWindow = new MainWindow
        {
            ApplyTheme = SetTheme,
            Engine = Services.GetRequiredService<IArchiveEngine>(),
            FileService = Services.GetRequiredService<ArchiveFileService>(),
            EditService = Services.GetRequiredService<ArchiveEditService>(),
            Desktop = Services.GetRequiredService<IDesktopServices>(),
            InitialPath = options.InitialPath,
            MaxTicks = options.FrameLimit,
            CloseOnEscape = false,
        };
    }
}
