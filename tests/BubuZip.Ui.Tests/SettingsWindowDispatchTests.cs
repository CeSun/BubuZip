using BubuZip.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class SettingsWindowDispatchTests
{
    [Fact]
    public void SettingsOpenAfterTheParentUpdateAndIgnoreDuplicateRequestsWhileOpen()
    {
        using var fixture = new Fixture();
        fixture.Presenter.OnShow = settings =>
        {
            Assert.True(fixture.Window.TaskIsRunning);
            Assert.DoesNotContain(fixture.Host.Content.Descendants().OfType<TextBlock>(),
                text => string.Concat(text.Runs.Select(run => run.Text)) == "文件格式关联");
            fixture.Window.RequestShowSettings();
            settings.ThemeChanged(ThemeVariant.Dark);
        };

        fixture.Window.RequestShowSettings();
        fixture.Host.UpdateLayout();
        Assert.Empty(fixture.Presenter.Windows);
        fixture.Host.DrainPostedWork();
        fixture.Host.UpdateLayout();

        Assert.Single(fixture.Presenter.Windows);
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.False(fixture.Window.IsDisposed);
        Assert.NotNull(fixture.Window.Theme);
        fixture.Presenter.OnShow = null;
        fixture.Window.RequestShowSettings();
        fixture.Host.UpdateLayout();
        fixture.Host.DrainPostedWork();
        Assert.Equal(2, fixture.Presenter.Windows.Count);
        Assert.Equal(ThemeVariant.Dark, fixture.Presenter.Windows[1].SelectedTheme);
    }

    [Fact]
    public void AQueuedSettingsRequestDoesNotOpenAfterItsParentWasClosed()
    {
        using var fixture = new Fixture();
        fixture.Window.RequestShowSettings();
        fixture.Host.UpdateLayout();

        fixture.Window.Dispose();
        fixture.Host.SynchronizationContext!.Drain();

        Assert.Empty(fixture.Presenter.Windows);
    }

    private sealed class Presenter : IArchiveSettingsWindows
    {
        public List<ArchiveSettingsWindow> Windows { get; } = [];
        public Action<ArchiveSettingsWindow>? OnShow { get; set; }
        public void Show(ArchiveSettingsWindow window)
        {
            Windows.Add(window);
            try { OnShow?.Invoke(window); }
            finally { window.Dispose(); }
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

    private sealed class Fixture : IDisposable
    {
        public Presenter Presenter { get; } = new();
        public MainWindow Window { get; }
        public HeadlessRoot Host { get; }
        private readonly HeadlessRoot.UiScope _scope;
        public Fixture()
        {
            var engine = new SharpCompressArchiveEngine();
            Window = new MainWindow
            {
                Engine = engine, FileService = new ArchiveFileService(engine),
                Desktop = new FakeDesktopServices(), SettingsWindows = Presenter,
            };
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(1000, 640) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _scope = Host.InstallSynchronizationContext();
            Host.UpdateLayout();
        }
        public void Dispose()
        {
            _scope.Dispose();
            Host.Dispose();
            Window.Dispose();
        }
    }
}
