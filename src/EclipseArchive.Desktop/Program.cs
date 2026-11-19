using EclipseArchive.Core;
using EclipseArchive;
using EclipseArchive.Desktop;
using EclipseUI;
using EclipseUI.Platform.MacOS;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Microsoft.Extensions.DependencyInjection;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: EclipseArchive.Desktop [--frames positive-number] [--path folder] [--shot image.png]");
    return 0;
}

int? frameLimit = null;
var initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string? shotPath = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--frames" && index + 1 < args.Length && int.TryParse(args[++index], out var frames) && frames > 0)
        frameLimit = frames;
    else if (args[index] == "--path" && index + 1 < args.Length)
        initialPath = Path.GetFullPath(args[++index]);
    else if (args[index] == "--shot" && index + 1 < args.Length)
        shotPath = Path.GetFullPath(args[++index]);
    else
    {
        Console.Error.WriteLine("Usage: EclipseArchive.Desktop [--frames positive-number] [--path folder] [--shot image.png]");
        return 2;
    }
}

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("EclipseArchive currently supports macOS only.");
    return 1;
}

// Cocoa requires synchronous startup on the process main thread.
var builder = AppBuilder.Configure<ArchiveApplication>()
    .UseMacOS()
    .UseVello(new VelloSurfaceOptions { UseSoftwareRasterizer = true })
    .UseHarfBuzzText()
    .UseFluentTheme(ThemeVariant.System)
    .ConfigureServices(services =>
    {
        services.AddSingleton(new DesktopLaunchOptions(frameLimit, initialPath));
        services.AddSingleton<IArchiveEngine, SharpCompressArchiveEngine>();
        services.AddSingleton<ArchiveFileService>();
        services.AddSingleton<ArchiveEditService>();
        services.AddSingleton<IDesktopServices, MacDesktopServices>();
    });

if (shotPath is null) return builder.Start(args);

var app = builder.Build();
try
{
    var window = app.MainWindow!;
    window.EnsureInitialized();
    var root = window.Content!;
    var theme = new ThemeBinding(window.Theme ?? app.DefaultTheme!).Current;
    var size = new Size(window.Width, window.Height);
    using var host = new HeadlessRoot(window) { AvailableSize = size };
    root.SetThemeDefaults(theme as IElementThemeDefaults);
    host.UpdateLayout();
    root.Measure(size); root.Arrange(new Rect(Point.Zero, size));
    var textPainter = app.Services.GetRequiredService<ITextPainter>();
    var list = new ElementPainter(theme, textPainter).Build(root, size, 1);
    using var backend = new VelloSparseBackend();
    var target = new RenderTarget((int)size.Width, (int)size.Height);
    backend.Render(list, target);
    Directory.CreateDirectory(Path.GetDirectoryName(shotPath)!);
    PngEncoder.Write(shotPath, target);
    Console.WriteLine($"Saved {shotPath} ({root.Descendants().Count()} elements, {list.Commands.Count} drawing commands)");
    return 0;
}
finally
{
    app.MainWindow?.Dispose();
    (app.Services as IDisposable)?.Dispose();
}
