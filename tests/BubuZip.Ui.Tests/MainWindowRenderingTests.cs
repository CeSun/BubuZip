using BubuZip.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

namespace BubuZip.Ui.Tests;

public sealed class MainWindowRenderingTests
{
    [Theory]
    [InlineData(false, 1000, 640)]
    [InlineData(true, 1000, 640)]
    [InlineData(false, 480, 320)]
    [InlineData(true, 480, 320)]
    public void CreationUsesAModalPopupWithOpaqueContentAndAccessibleActions(bool dark, int width, int height)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BubuZip.Modal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = new SharpCompressArchiveEngine();
        using var window = new MainWindow
        {
            Engine = engine, FileService = new ArchiveFileService(engine),
            Desktop = new FakeDesktopServices(), InitialPath = directory,
        };
        try
        {
            window.EnsureInitialized();
            var root = window.Content!;
            var size = new Size(width, height);
            using var host = new HeadlessRoot(window) { AvailableSize = size };
            var painter = new ElementPainter(dark ? FluentTheme.Dark : FluentTheme.Light, new ShapeTextPainter());
            using IRenderBackend backend = OperatingSystem.IsLinux()
                ? new VelloCpuBackend(new VelloBackendOptions { ValidateDisplayList = true })
                : new VelloSparseBackend(validateDisplayList: true);
            RenderTarget Render()
            {
                painter.Prepare(root); host.UpdateLayout();
                root.Measure(size); root.Arrange(new Rect(Point.Zero, size));
                var target = new RenderTarget(width, height);
                backend.Render(painter.Build(root, size, 1), target);
                return target;
            }
            var before = Render();
            root.Descendants().OfType<Button>().Single(button => button.Label == "新建压缩").Click();
            var after = Render();
            var frame = Assert.Single(root.Descendants().OfType<ArchiveDialogBorder>());
            var popup = Assert.Single(root.Descendants().OfType<ArchiveDialogPopup>());
            Assert.True(popup.IsOpen);
            Assert.True(popup.IsModal);
            Assert.Equal(PopupHostMode.Window, popup.HostMode);
            Assert.Same(frame, popup.Content);
            var origin = frame.PointFromRoot(Point.Zero);
            var bounds = new Rect(-origin.X, -origin.Y, frame.FinalRect.Width, frame.FinalRect.Height);
            Assert.InRange(Math.Abs(bounds.X + bounds.Width / 2 - width / 2d), 0, .01);
            Assert.InRange(Math.Abs(bounds.Y + bounds.Height / 2 - height / 2d), 0, .01);
            Assert.True(bounds.Width <= width);
            Assert.True(bounds.Height <= height);
            if (!bounds.Contains(new Point(8, height - 8)))
                Assert.Equal(before.GetPixel(8, height - 8), after.GetPixel(8, height - 8));
            Assert.Equal(before.GetPixel(8, height - 8),
                after.GetPixel((int)bounds.X + 12, (int)bounds.Y + 12));
            var start = Assert.Single(frame.Descendants().OfType<Button>(), b => b.Label == "开始");
            var cancel = Assert.Single(frame.Descendants().OfType<Button>(), b => b.Label == "取消");
            Assert.Equal(start.FinalRect.Y, cancel.FinalRect.Y);
            Assert.True(start.FinalRect.Right < cancel.FinalRect.X);
            Assert.True(start.IsEffectiveEnabled && cancel.IsEffectiveEnabled);
            Assert.Null(host.Input.HitTest(new Point(bounds.Left - 1, bounds.Top - 1)));
            var cancelOrigin = cancel.PointFromRoot(Point.Zero);
            Assert.Contains(host.Input.HitTest(new Point(-cancelOrigin.X + cancel.FinalRect.Width / 2,
                -cancelOrigin.Y + cancel.FinalRect.Height / 2))!, cancel.Descendants().Prepend(cancel));
            frame.Descendants().OfType<ComboBox>().First().SelectedIndex = 1;
            Render();
            Assert.True(frame.FinalRect.Height <= height);
            if (height == 320)
                Assert.True(frame.Descendants().OfType<ScrollViewer>().First().ScrollableHeight > 0);
            Assert.True(start.IsEffectiveEnabled && cancel.IsEffectiveEnabled);
            cancel.Click(); Render();
            Assert.False(popup.IsOpen);
            Assert.False(frame.IsEffectivelyVisible);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void SoftwareRasterizerPreservesTheWholeWindowAcrossFrames(bool dark, int scale)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BubuZip.Rendering-" + Guid.NewGuid().ToString("N"));
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
            using IRenderBackend backend = OperatingSystem.IsLinux()
                ? new VelloCpuBackend(new VelloBackendOptions { ValidateDisplayList = true })
                : new VelloSparseBackend(validateDisplayList: true);

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
                return target;
            }

            var first = RenderFrame();
            Assert.Equal(dark ? new Color(28, 34, 51) : new Color(240, 244, 249), first.GetPixel(width - 4, height - 4));
            Assert.Equal(dark ? new Color(28, 34, 51) : new Color(240, 244, 249), first.GetPixel(width * 3 / 4, height / 2));
            for (var frame = 0; frame < 3; frame++)
                Assert.Equal(first.ContentHash(), RenderFrame().ContentHash());

            root.Descendants().OfType<Button>().Single(button => button.Label == "新建压缩").Click();
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
