using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

namespace EclipseArchive.Ui.Tests;

public sealed class MainWindowRenderingTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void SoftwareRasterizerPreservesTheWholeWindowAcrossFrames(bool dark, int scale)
    {
        var directory = Path.Combine(Path.GetTempPath(), "EclipseArchive.Rendering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "Documents", "Images", "Empty" })
            Directory.CreateDirectory(Path.Combine(directory, name));
        foreach (var name in new[] { "README.md", "List.csv", "Example.zip" })
            File.WriteAllText(Path.Combine(directory, name), "Rendering regression fixture.");

        var engine = new SharpCompressArchiveEngine();
        var window = new MainWindow
        {
            Engine = engine,
            FileService = new ArchiveFileService(engine),
            Desktop = new FakeDesktopServices(),
            InitialPath = directory,
        };
        HeadlessRoot? host = null;
        try
        {
            window.EnsureInitialized();
            var root = window.Content!;
            var logicalSize = new Size(1140, 740);
            var pixelSize = new Size(logicalSize.Width * scale, logicalSize.Height * scale);
            var width = (int)pixelSize.Width;
            var height = (int)pixelSize.Height;
            var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
            host = new HeadlessRoot(window) { AvailableSize = logicalSize };
            var painter = new ElementPainter(theme, new ShapeTextPainter());
            using var backend = new VelloSparseBackend(validateDisplayList: true);

            RenderTarget RenderFrame()
            {
                painter.Prepare(root);
                host.UpdateLayout();
                root.Measure(logicalSize);
                root.Arrange(new Rect(Point.Zero, logicalSize));
                var list = painter.BuildFrame(root, logicalSize, scale).List;
                var target = new RenderTarget(width, height);
                backend.Render(list, target);
                for (var pixel = 3; pixel < target.Pixels.Length; pixel += 4)
                    Assert.True(target.Pixels[pixel] >= 250,
                        $"Missing window pixel (alpha < 250) at {(pixel / 4) % width},{(pixel / 4) / width} in the {width}x{height} {(dark ? "dark" : "light")} frame.");
                Assert.Equal(theme.Surface, target.GetPixel(width - 4, height - 4));
                Assert.Equal(theme.Surface, target.GetPixel(width / 2, height / 2));
                return target;
            }

            var first = RenderFrame();
            for (var frame = 0; frame < 3; frame++)
                Assert.Equal(first.ContentHash(), RenderFrame().ContentHash());

            var files = Assert.Single(root.Descendants().OfType<ListBox>());
            files.SelectedItem = Assert.Single(window.VisibleItems, item => item.Name == "README.md");
            var changed = RenderFrame();
            Assert.NotEqual(first.ContentHash(), changed.ContentHash());
            for (var frame = 0; frame < 3; frame++)
                Assert.Equal(changed.ContentHash(), RenderFrame().ContentHash());
        }
        finally
        {
            window.Dispose();
            host?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    // Keep the real window layout and clips without depending on system fonts.
    private sealed class ShapeTextPainter : ITextPainter
    {
        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * .3, text.Length * fontSize * .5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
        {
            for (var index = 0; index < text.Length; index++)
                if (!char.IsWhiteSpace(text[index]))
                    list.Add(new FillRect(new Rect(topLeft.X + index * fontSize * .5,
                        topLeft.Y + fontSize * .2, fontSize * .35, fontSize * .6), color));
        }
    }

    private sealed class FakeDesktopServices : IDesktopServices
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
