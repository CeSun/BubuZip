using EclipseArchive.Core;
using EclipseArchive;
using EclipseArchive.Desktop;
using EclipseUI;
using EclipseUI.Platform.MacOS;
using EclipseUI.Platform.Windows;
using EclipseUI.Platform.Linux;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: EclipseArchive.Desktop [--frames positive-number] [--path folder] [--shot image.png] [--dark]");
    return 0;
}

int? frameLimit = null;
var initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string? shotPath = null;
var darkPreview = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--frames" && index + 1 < args.Length && int.TryParse(args[++index], out var frames) && frames > 0)
        frameLimit = frames;
    else if (args[index] == "--path" && index + 1 < args.Length)
        initialPath = Path.GetFullPath(args[++index]);
    else if (args[index] == "--shot" && index + 1 < args.Length)
        shotPath = Path.GetFullPath(args[++index]);
    else if (args[index] == "--dark") darkPreview = true;
    else
    {
        Console.Error.WriteLine("Usage: EclipseArchive.Desktop [--frames positive-number] [--path folder] [--shot image.png] [--dark]");
        return 2;
    }
}

if (shotPath is not null)
{
    PreviewRenderer.Export(initialPath, shotPath, darkPreview);
    return 0;
}

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("EclipseArchive supports Windows, Linux and macOS.");
    return 1;
}

var builder = AppBuilder.Configure<ArchiveApplication>()
    .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = !OperatingSystem.IsLinux() })
    .UseHarfBuzzText()
    .UseFluentTheme(ThemeVariant.System)
    .ConfigureServices(services =>
    {
        services.AddSingleton(new DesktopLaunchOptions(frameLimit, initialPath));
        services.AddSingleton<IArchiveEngine, SharpCompressArchiveEngine>();
        services.AddSingleton<ArchiveFileService>();
        services.AddSingleton<ArchiveEditService>();
        if (OperatingSystem.IsMacOS()) services.AddSingleton<IDesktopServices, MacDesktopServices>();
        else if (OperatingSystem.IsWindows()) services.AddSingleton<IDesktopServices, WindowsDesktopServices>();
        else services.AddSingleton<IDesktopServices, LinuxDesktopServices>();
    });

if (OperatingSystem.IsMacOS()) builder.UseMacOS();
else if (OperatingSystem.IsWindows()) builder.UseWindows();
else builder.UseLinux();

return builder.Start(args);
