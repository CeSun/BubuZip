using EclipseArchive.Core;
using EclipseArchive;
using EclipseArchive.Desktop;
using EclipseUI;
using EclipseUI.Platform.MacOS;
using EclipseUI.Platform.Windows;
using EclipseUI.Platform.Linux;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Microsoft.Extensions.DependencyInjection;

DesktopCommandLineOptions options;
try { options = DesktopCommandLine.Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine(DesktopCommandLine.Usage);
    return 2;
}
if (options.ShowHelp)
{
    Console.WriteLine(DesktopCommandLine.Usage);
    return 0;
}

if (options.ShotPath is not null)
{
    PreviewRenderer.Export(options.InitialPath, options.ShotPath, options.DarkPreview, options.OpenSettings, options.CreateArchive);
    return 0;
}

if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("BubuZip supports Windows, Linux and macOS.");
    return 1;
}

var appearance = new ArchiveAppearance(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BubuZip", "appearance.txt"));
var builder = AppBuilder.Configure<ArchiveApplication>()
    .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = !OperatingSystem.IsLinux() })
    .UseHarfBuzzText()
    .UseTheme(appearance)
    .ConfigureServices(services =>
    {
        services.AddSingleton(appearance);
        services.AddSingleton(new DesktopLaunchOptions(options.FrameLimit, options.InitialPath, options.ArchivePaths, options.ShellRequest, options.OpenSettings, options.CreateArchive));
        services.AddSingleton<IArchiveEngine, SharpCompressArchiveEngine>();
        services.AddSingleton<ArchiveFileService>();
        services.AddSingleton<ArchiveEditService>();
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IDesktopServices, MacDesktopServices>();
            services.AddSingleton<IShellIntegrationService, MacShellIntegration>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IDesktopServices, WindowsDesktopServices>();
            services.AddSingleton<IShellIntegrationService, WindowsShellIntegration>();
        }
        else services.AddSingleton<IDesktopServices, LinuxDesktopServices>();
    });

if (OperatingSystem.IsMacOS()) builder.UseMacOS();
else if (OperatingSystem.IsWindows()) builder.UseWindows();
else builder.UseLinux();

var app = builder.Build();
var window = (MainWindow)app.MainWindow!;
using var openDocuments = OperatingSystem.IsMacOS()
    ? new MacOpenDocuments(window.RequestOpenArchive)
    : null;
var exitCode = OperatingSystem.IsMacOS() && openDocuments is not null
    ? new MacArchiveLifetime(openDocuments).Run(app, args)
    : app.Services.GetRequiredService<IApplicationLifetime>().Run(app, args);
if (options.FrameLimit is not null)
{
    Console.WriteLine($"archive: {window.CurrentLocation?.ArchivePath ?? "(none)"}");
    Console.WriteLine($"archive entries: {window.VisibleItems.Count}");
    Console.WriteLine($"status: {window.StatusMessage}");
}
return exitCode;
