using BubuZip.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;

namespace BubuZip.Desktop;

internal static class PreviewRenderer
{
    public static void Export(string path, string output, bool dark, bool showSettings = false, bool createArchive = false)
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
            var integration = OperatingSystem.IsMacOS() ? (IShellIntegrationService)new MacShellIntegration()
                : OperatingSystem.IsWindows() ? new WindowsShellIntegration() : null;
            using Window window = showSettings ? new ArchiveSettingsWindow
            {
                ShellIntegration = integration,
                SelectedTheme = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            } : new MainWindow
            {
                Engine = engine, FileService = files, EditService = new(engine, files),
                Desktop = new PreviewServices(), InitialPath = path,
                ShellIntegration = integration,
                OperationOnly = createArchive, StartCreating = createArchive,
            };
            window.EnsureInitialized();
            using var host = new HeadlessRoot(window) { AvailableSize = new Size(window.Width, window.Height) };
            using var ui = host.InstallSynchronizationContext();
            var painter = new ElementPainter(dark ? FluentTheme.Dark : FluentTheme.Light, text);
            painter.Prepare(window.Content!);
            host.UpdateLayout();
            var size = new Size(window.Width, window.Height);
            host.AvailableSize = size;
            host.UpdateLayout();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (IsBusy(window) && wait.Elapsed < TimeSpan.FromSeconds(10))
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

    private static bool IsBusy(Window window) => window switch
    {
        MainWindow main => main.TaskIsRunning,
        ArchiveSettingsWindow settings => settings.TaskIsRunning,
        _ => false,
    };

    private sealed class PreviewServices : IDesktopServices
    {
        public IReadOnlyList<string> ChooseFiles(string? initialDirectory = null) => [];
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
