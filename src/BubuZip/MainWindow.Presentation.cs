using System.Diagnostics;
using BubuZip.Core;
using EclipseUI;
using EclipseUI.Elements;
using Path = System.IO.Path;

namespace BubuZip;

public partial class MainWindow
{
    private void InitializePane(ArchivePane pane)
    {
        pane.EnsureInitialized();
        pane.Files.ItemTemplate = value => CreateRow(value, pane);
        pane.Files.Selection.Changed += () => { pane.RefreshView(); Invalidate(); };
        pane.PaneRoot.AddHandler(Element.GotFocusEvent, _ => ActivatePane(pane));
        pane.PaneRoot.AddHandler(Element.PointerPressedEvent, _ => ActivatePane(pane));
        pane.Files.AddHandler(Element.KeyDownEvent, args =>
        {
            ActivatePane(pane);
            if (args is KeyEventArgs { Key: Key.Enter }) { args.Handled = true; OpenSelected(); }
            else if (args is KeyEventArgs { Key: Key.Backspace }) { args.Handled = true; GoUp(); }
        });
    }

    private void ActivatePane(ArchivePane pane)
    {
        if (ReferenceEquals(ActivePane, pane) || _busy || _panel != DialogKind.None) return;
        _activePane = pane;
        _lastClick = "";
        Title = pane.Snapshot?.DisplayPath ?? "BubuZip";
        Invalidate();
    }

    private void PaneUp(ArchivePane pane) { ActivatePane(pane); GoUp(); }
    private void PaneSort(ArchivePane pane, string column) { ActivatePane(pane); SortBy(column); }

    private void ReturnHome()
    {
        if (_busy || _panel != DialogKind.None) return;
        _showBrowser = false;
        _activePane = RightPane;
        _history.Clear();
        ClearNestedArchives();
        _rootArchivePath = null;
        RightPane.HasParentArchive = false;
        _archivePassword = null;
        _snapshot = null;
        RightPane.Files.Selection.Clear();
        VisibleItems = [];
        RightPane.Files.ItemsSource = VisibleItems;
        RightPane.Address.Text = "";
        RightPane.RefreshView();
        StatusMessage = "就绪";
        _error = "";
        Title = "BubuZip";
        Invalidate();
    }
    private void ShowMore() { MainMenu.Open(); }

    private async void RunBenchmark()
    {
        if (_busy) return;
        string result = "";
        var completed = await RunOperationAsync(async token =>
        {
            // A bounded, reproducible in-memory ZIP benchmark. No user files are written.
            var data = new byte[4 * 1024 * 1024];
            var random = new Random(7);
            var block = new byte[4096]; random.NextBytes(block);
            for (var offset = 0; offset < data.Length; offset += block.Length) block.CopyTo(data, offset);
            using var source = new MemoryStream(data, writable: false);
            using var archive = new MemoryStream();
            var timer = Stopwatch.StartNew();
            await Engine.CreateAsync(archive, [new("benchmark.bin", source)], cancellationToken: token);
            var compressSeconds = Math.Max(timer.Elapsed.TotalSeconds, .000001);
            archive.Position = 0;
            using var extracted = new MemoryStream();
            timer.Restart();
            await Engine.ExtractEntryAsync(archive, "benchmark.bin", extracted, cancellationToken: token);
            var extractSeconds = Math.Max(timer.Elapsed.TotalSeconds, .000001);
            if (!data.AsSpan().SequenceEqual(extracted.ToArray())) throw new InvalidDataException("性能测试校验失败。");
            result = $"ZIP · 4 MiB 测试数据\n\n压缩速度：{4 / compressSeconds:N1} MiB/s\n解压速度：{4 / extractSeconds:N1} MiB/s\n压缩后：{BrowserItem.FormatSize(archive.Length)}\n\n解压数据校验通过。";
        }, "性能测试完成");
        if (completed && !_disposed) ShowMessageDialog("性能测试", result);
    }
}
