using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;

namespace EclipseArchive.Desktop;

internal static class PreviewRenderer
{
    public static void Export(string path, string output, bool dark, bool showSettings = false)
    {
        var options = new HarfBuzzTextOptions();
        using var shaper = new HarfBuzzTextShaper(options.FontPaths.First(File.Exists));
        using var text = new HarfBuzzTextPainter(shaper);
        var previous = TextMeasurer.Current;
        TextMeasurer.Current = text;
        try
        {
            var engine = new SharpCompressArchiveEngine();
            var files = new ArchiveFileService(engine);
            using var window = new MainWindow
            {
                Engine = engine, FileService = files, EditService = new(engine, files),
                Desktop = new PreviewServices(), InitialPath = path,
                ShellIntegration = OperatingSystem.IsMacOS() ? new MacShellIntegration()
                    : OperatingSystem.IsWindows() ? new WindowsShellIntegration() : null,
            };
            if (showSettings) window.RequestShowSettings();
            window.EnsureInitialized();
            var size = new Size(window.Width, window.Height);
            using var host = new HeadlessRoot(window) { AvailableSize = size };
            using var ui = host.InstallSynchronizationContext();
            var painter = new ElementPainter(dark ? FluentTheme.Dark : FluentTheme.Light, text);
            painter.Prepare(window.Content!);
            host.UpdateLayout();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (window.TaskIsRunning && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                host.DrainPostedWork();
                host.UpdateLayout();
                Thread.Sleep(1);
            }
            window.Content!.Measure(size);
            window.Content.Arrange(new Rect(Point.Zero, size));
            var list = painter.Build(window.Content, size, 1);
            using IRenderBackend backend = OperatingSystem.IsLinux() ? new VelloCpuBackend() : new VelloSparseBackend();
            var target = new RenderTarget((int)size.Width, (int)size.Height);
            backend.Render(list, target);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
            PngEncoder.Write(output, target);
            Console.WriteLine($"Saved {output}");
        }
        finally { TextMeasurer.Current = previous; }
    }

    private sealed class PreviewServices : IDesktopServices
    {
        public IReadOnlyList<string> ChooseInputs() => [];
        public string? ChooseArchive() => null;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null) => null;
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) { }
        public void Trash(IReadOnlyList<string> paths) { }
    }
}
