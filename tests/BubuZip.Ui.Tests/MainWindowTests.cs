using System.Diagnostics;
using System.Text;
using BubuZip.Core;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

// The headless UI belongs to the test thread. Pump its synchronization context while
// navigation runs so every UI continuation and assertion stays on that same thread.
#pragma warning disable xUnit1031

namespace BubuZip.Ui.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void SourcePickerShowsAllPathsAndRemovesOnlyTheChosenItem()
    {
        using var fixture = new WindowFixture();
        var paths = Enumerable.Range(0, 6).Select(index => fixture.WriteFile($"inputs/file{index}.txt", $"file {index}")).ToArray();
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        Assert.False(fixture.FindButton("开始").IsEffectiveEnabled);
        fixture.Desktop.Inputs = paths;
        fixture.Click("添加文件…");
        var picker = Assert.Single(fixture.Elements<ArchiveSourcePicker>());
        Assert.Equal(paths, picker.Sources);
        Assert.All(paths, path => Assert.Contains(path, fixture.Texts()));
        Assert.True(picker.Descendants().OfType<ScrollViewer>().Single().ScrollableHeight > 0);
        var row = Assert.Single(picker.Descendants().OfType<ArchiveSurface>(), element => element.Descendants().OfType<TextBlock>().Any(block => string.Concat(block.Runs.Select(run => run.Text)) == paths[4]));
        Assert.Single(row.Children.OfType<Button>()).Click();
        fixture.Host.UpdateLayout();
        Assert.Equal(paths.Where((_, index) => index != 4), picker.Sources);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.True(fixture.FindButton("开始").IsEffectiveEnabled);
    }

    [Fact]
    public void ManualDestinationSurvivesSelectionChangesAndCanReturnToAutomaticNaming()
    {
        using var fixture = new WindowFixture();
        var first = fixture.WriteFile("inputs/first.txt", "first");
        var second = fixture.WriteFile("inputs/second.txt", "second");
        fixture.OpenWindow(); fixture.Click("新建压缩");
        fixture.Desktop.Inputs = [first]; fixture.Click("添加文件…");
        var suggested = Path.Combine(fixture.DirectoryPath, "inputs", "first.zip");
        var box = Assert.Single(fixture.Elements<TextBox>(), box => box.Text == suggested);
        box.Text = Path.Combine(fixture.DirectoryPath, "custom.zip"); fixture.Host.UpdateLayout();
        box.Text = suggested; fixture.Host.UpdateLayout(); // Returning to the old suggestion remains an explicit manual choice.
        fixture.Desktop.Inputs = [second]; fixture.Click("添加文件…");
        Assert.Equal(suggested, box.Text);
        fixture.Click("清空");
        Assert.Equal(suggested, box.Text);
        Assert.True(fixture.FindButton("自动命名").IsEffectiveEnabled);
        fixture.Click("自动命名");
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "Archive.zip"), box.Text);
        Assert.False(fixture.FindButton("自动命名").IsEffectiveEnabled);
        fixture.Desktop.Inputs = [second]; fixture.Click("添加文件…");
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "inputs", "second.zip"), box.Text);
    }

    [Fact]
    public void SourcePickersRememberTheirDirectoryAndCancellationPreservesSelection()
    {
        using var fixture = new WindowFixture();
        var file = fixture.WriteFile("inputs/file.txt", "content");
        fixture.OpenWindow(); fixture.Click("新建压缩");
        fixture.Desktop.Inputs = [file]; fixture.Click("添加文件…");
        fixture.Desktop.Inputs = []; fixture.Click("添加文件…");
        fixture.Desktop.FolderChoice = null; fixture.Click("添加文件夹…");
        Assert.Equal(new[] { fixture.DirectoryPath, Path.GetDirectoryName(file) }, fixture.Desktop.FilePickerDirectories);
        Assert.Equal(Path.GetDirectoryName(file), Assert.Single(fixture.Desktop.FolderPickerDirectories));
        Assert.Equal(file, Assert.Single(Assert.Single(fixture.Elements<ArchiveSourcePicker>()).Sources));
    }

    [Fact]
    public void SourceConflictsAreReportedBeforeCompressionAndFolderChildrenAreMerged()
    {
        using var fixture = new WindowFixture();
        var first = fixture.WriteFile("one/same.txt", "one");
        var conflict = fixture.WriteFile("two/same.txt", "two");
        fixture.OpenWindow(); fixture.Click("新建压缩");
        fixture.Desktop.Inputs = [first]; fixture.Click("添加文件…");
        fixture.Desktop.Inputs = [conflict]; fixture.Click("添加文件…");
        var picker = Assert.Single(fixture.Elements<ArchiveSourcePicker>());
        Assert.Equal(first, Assert.Single(picker.Sources));
        Assert.Contains(fixture.Texts(), text => text.Contains("本次添加未生效"));
        fixture.Desktop.FolderChoice = Path.GetDirectoryName(first); fixture.Click("添加文件夹…");
        Assert.Equal(Path.GetDirectoryName(first), Assert.Single(picker.Sources));
        Assert.Contains(fixture.Texts(), text => text.Contains("已合并重复项目"));
        fixture.Desktop.Inputs = [first]; fixture.Click("添加文件…");
        Assert.Equal(Path.GetDirectoryName(first), Assert.Single(picker.Sources));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyPublishesSelectedFilesAndFoldersRelativeToCurrentDirectory(bool keyboard)
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("docs/中文 文件.txt", "text"), ("docs/folder/child.txt", "child"), ("docs/skip.txt", "skip"));
        var original = File.ReadAllBytes(archive);
        fixture.OpenWindow(archive);
        fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.SelectItem("中文 文件.txt");
        fixture.Files.Selection.Select(fixture.Window.VisibleItems.ToList().FindIndex(item => item.Name == "folder"), toggle: true);
        fixture.Host.UpdateLayout();
        if (keyboard)
        {
            fixture.Files.RequestFocus();
            fixture.Host.Input.KeyPress(Key.C, KeyModifiers.Control);
        }
        else fixture.Click("复制");
        fixture.WaitForIdle();
        Assert.Equal(2, fixture.Desktop.CopiedPaths.Count);
        Assert.Equal("text", File.ReadAllText(fixture.Desktop.CopiedPaths.Single(path => Path.GetFileName(path) == "中文 文件.txt")));
        Assert.Equal("child", File.ReadAllText(Path.Combine(fixture.Desktop.CopiedPaths.Single(Directory.Exists), "child.txt")));
        Assert.DoesNotContain(fixture.Desktop.CopiedPaths, path => Path.GetFileName(path) is "docs" or "skip.txt");
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.DoesNotContain("提取文件", fixture.Texts());
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.Equal(original, File.ReadAllBytes(archive));
        fixture.Window.Dispose();
        Assert.All(fixture.Desktop.CopiedPaths, path => Assert.True(File.Exists(path) || Directory.Exists(path)));
    }

    [Fact]
    public void CopyEncryptedFolderRetriesPasswordBeforePublishingAnything()
    {
        using var fixture = new WindowFixture();
        var archive = Path.Combine(fixture.DirectoryPath, "encrypted.zip");
        File.WriteAllBytes(archive, ArchiveBytes(ArchiveFormat.Zip, "folder/secret.txt", Encoding.UTF8.GetBytes("secret"), "password"));
        fixture.OpenWindow(archive);
        fixture.WaitForIdle();
        fixture.SelectItem("folder");
        fixture.Click("复制");
        fixture.WaitForIdle();
        Assert.Empty(fixture.Desktop.ClipboardAttempts);
        Assert.Contains("复制到剪贴板", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "wrong";
        fixture.Click("开始");
        fixture.WaitForIdle();
        Assert.Empty(fixture.Desktop.ClipboardAttempts);
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "password";
        fixture.Click("开始");
        fixture.WaitForIdle();
        Assert.Equal("secret", File.ReadAllText(Path.Combine(Assert.Single(fixture.Desktop.CopiedPaths), "secret.txt")));
    }

    [Fact]
    public void ClipboardFailureCleansUnpublishedFilesAndAllowsRetry()
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow(fixture.CreateZip(("file.txt", "content")));
        fixture.WaitForIdle();
        fixture.SelectItem("file.txt");
        fixture.Desktop.ClipboardFailure = new IOException("clipboard busy");
        fixture.Click("复制");
        fixture.WaitForIdle();
        Assert.Empty(fixture.Desktop.CopiedPaths);
        Assert.All(Assert.Single(fixture.Desktop.ClipboardAttempts), path => Assert.False(File.Exists(path)));
        Assert.Contains("clipboard busy", fixture.Texts());
        fixture.Desktop.ClipboardFailure = null;
        fixture.Click("开始");
        fixture.WaitForIdle();
        Assert.Equal("content", File.ReadAllText(Assert.Single(fixture.Desktop.CopiedPaths)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DraggingColumnEdgesResizesRowsWithoutSortingOrChangingSelection(int column)
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow(fixture.CreateZip(("alpha.txt", "alpha"), ("beta.txt", "beta")));
        fixture.WaitForIdle();
        fixture.SelectItem("beta.txt");
        var pane = Assert.Single(fixture.Elements<ArchivePane>());
        var selection = fixture.Files.SelectedItem;
        var grip = fixture.Elements<ArchiveColumnResizeGrip>().ElementAt(column);
        var header = fixture.Elements<ArchiveCommand>().Where(command => command.IsColumnHeader).ElementAt(column);
        var width = header.FinalRect.Width;
        var origin = grip.PointFromRoot(Point.Zero);
        var start = new Point(-origin.X + 4, -origin.Y + 12);
        fixture.Host.PointerMove(start);
        Assert.Equal(PointerCursor.ResizeHorizontal, fixture.Host.Input.Cursor);
        fixture.Host.PointerPress(start);
        Assert.Same(grip, fixture.Host.Input.Captured);
        fixture.Host.PointerMove(new(start.X + 70, start.Y + 40));
        Assert.Equal(PointerCursor.ResizeHorizontal, fixture.Host.Input.Cursor);
        fixture.Host.UpdateLayout();
        Assert.Equal(width + 70, header.FinalRect.Width);
        fixture.Host.PointerRelease(new(start.X + 70, start.Y + 40));
        Assert.Equal(PointerCursor.Arrow, fixture.Host.Input.Cursor);
        fixture.Host.UpdateLayout();
        Assert.Equal("name", pane.SortColumn);
        Assert.False(pane.SortDescending);
        Assert.Same(selection, fixture.Files.SelectedItem);
        Assert.All(fixture.Elements<ArchiveFileRow>(), row =>
        {
            Assert.Equal(pane.FileColumnDefinitions, row.ColumnDefinitions);
            Assert.Equal(header.FinalRect.Right, row.Children[column].FinalRect.Right + (column == 0 ? 0 : 3), 5);
        });

        // Sorting still works and newly ordered rows retain the chosen widths.
        var headerOrigin = header.PointFromRoot(Point.Zero);
        var center = new Point(-headerOrigin.X + 20, -headerOrigin.Y + 12);
        fixture.Host.PointerPress(center);
        fixture.Host.PointerRelease(center);
        fixture.Host.UpdateLayout();
        Assert.Equal(new[] { "name", "size", "modified", "packed", "type", "attributes" }[column], pane.SortColumn);
        Assert.All(fixture.Elements<ArchiveFileRow>(), row => Assert.Equal(pane.FileColumnDefinitions, row.ColumnDefinitions));
    }

    [Fact]
    public void ColumnResizingClampsWidthAndSurvivesScrollingAndCaptureCancellation()
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow(fixture.CreateZip(Enumerable.Range(0, 200).Select(index => ($"file{index:D3}.txt", (string?)"data")).ToArray()));
        fixture.WaitForIdle();
        fixture.Host.AvailableSize = new Size(600, 740);
        fixture.Host.UpdateLayout();
        var pane = Assert.Single(fixture.Elements<ArchivePane>());
        Assert.True(pane.Files.IsVirtualizing);
        pane.FileScroll.ScrollToHorizontalOffset(100);
        fixture.Host.UpdateLayout();
        var grip = fixture.Elements<ArchiveColumnResizeGrip>().First();
        var origin = grip.PointFromRoot(Point.Zero);
        var start = new Point(-origin.X + 4, -origin.Y + 12);
        fixture.Host.PointerPress(start);
        Assert.Same(grip, fixture.Host.Input.Captured);
        fixture.Host.PointerMove(new(start.X + 350, start.Y));
        fixture.Host.UpdateLayout();
        fixture.Host.Input.CancelPointerCapture();
        fixture.Host.PointerMove(new(start.X + 400, start.Y));
        fixture.Host.UpdateLayout();
        Assert.Equal(550, grip.ColumnWidth);
        pane.FileScroll.ScrollToVerticalOffset(3000);
        fixture.Host.UpdateLayout();
        Assert.All(fixture.Elements<ArchiveFileRow>(), row => Assert.StartsWith("550,", row.ColumnDefinitions));

        origin = grip.PointFromRoot(Point.Zero);
        start = new Point(-origin.X + 4, -origin.Y + 12);
        fixture.Host.PointerPress(start);
        Assert.Same(grip, fixture.Host.Input.Captured);
        fixture.Host.PointerMove(new(start.X - 1000, start.Y));
        fixture.Host.PointerRelease(new(start.X - 1000, start.Y));
        fixture.Host.UpdateLayout();
        Assert.Equal(48, grip.ColumnWidth);
        Assert.Equal(pane.FileScroll.HorizontalOffset, pane.HeaderScroll.HorizontalOffset);
        Assert.All(fixture.Elements<ArchiveFileRow>(), row => Assert.StartsWith("48,", row.ColumnDefinitions));
    }

    [Fact]
    public void DesktopCreateOpensAnIndependentWindowAndKeepsTheHomeUsable()
    {
        var windows = new CapturingArchiveWindows();
        using var fixture = new WindowFixture { ArchiveWindows = windows };
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        Assert.Equal([fixture.DirectoryPath], windows.CreatePaths);
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.True(fixture.FindButton("打开压缩").IsEffectiveEnabled);
        Assert.All(fixture.Elements<ArchiveDialogPopup>(), popup => Assert.False(popup.IsOpen));
        Assert.Null(fixture.Window.CurrentLocation);
    }

    [Fact]
    public void DesktopPickerOpensANewWindowAndPreservesTheCurrentDirectoryAndSelection()
    {
        var windows = new CapturingArchiveWindows();
        using var fixture = new WindowFixture { ArchiveWindows = windows };
        var first = fixture.CreateZip("first.zip", ("docs/readme.txt", "first"));
        var second = fixture.CreateZip("second.zip", ("second.txt", "second"));
        fixture.OpenWindow(first);
        fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.SelectItem("readme.txt");
        var selected = fixture.Files.SelectedItem;
        fixture.Desktop.ArchiveChoice = second;
        fixture.ClickFileCommand("打开压缩包…");
        fixture.WaitForIdle();
        Assert.Equal([second], windows.OpenPaths);
        Assert.Equal(first, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("docs/", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.Same(selected, fixture.Files.SelectedItem);
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Fact]
    public void DesktopPickerCancellationLeavesTheHomeWithoutOpeningAWindow()
    {
        var windows = new CapturingArchiveWindows();
        using var fixture = new WindowFixture { ArchiveWindows = windows };
        fixture.OpenWindow();
        fixture.Click("打开压缩");
        fixture.WaitForIdle();
        Assert.Empty(windows.OpenPaths);
        Assert.Null(fixture.Window.CurrentLocation);
    }

    [Fact]
    public void DesktopRunningOpenRequestsKeepTheOriginalArchive()
    {
        var windows = new CapturingArchiveWindows();
        using var fixture = new WindowFixture { ArchiveWindows = windows };
        var first = fixture.CreateZip("first.zip", ("first.txt", "first"));
        var second = fixture.CreateZip("second.zip", ("second.txt", "second"));
        fixture.OpenWindow(first);
        fixture.WaitForIdle();
        Task.Run(() => fixture.Window.RequestOpenArchive(second)).GetAwaiter().GetResult();
        fixture.WaitForIdle();
        Assert.Equal([second], windows.OpenPaths);
        Assert.Equal(first, fixture.Window.CurrentLocation!.ArchivePath);
    }

    [Fact]
    public void StandaloneCreationUsesTheWindowContentAndCreatesAReadableArchive()
    {
        using var fixture = new WindowFixture { StartCreating = true };
        var source = fixture.WriteFile("standalone.txt", "independent window payload");
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow();
        Assert.True(fixture.Window.OperationOnly);
        Assert.Equal(900, fixture.Window.Width);
        Assert.Equal(740, fixture.Window.Height);
        Assert.Contains("添加到压缩包", fixture.Window.Title);
        Assert.All(fixture.Elements<ArchiveDialogPopup>(), popup => Assert.False(popup.IsOpen));
        fixture.Click("添加文件…");
        fixture.WaitForIdle();
        fixture.Click("确定");
        fixture.WaitForIdle();
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Equal("independent window payload", fixture.ReadArchiveEntry(Path.Combine(fixture.DirectoryPath, "standalone.zip"), "standalone.txt"));
    }

    [Fact]
    public void ShowingCreationPasswordsKeepsBothEntriesAndResetsWhenReopened()
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        var boxes = fixture.Elements<ArchivePasswordBox>().ToArray();
        Assert.Equal(2, boxes.Length);
        foreach (var box in boxes) box.Text = "test-password";
        fixture.Host.UpdateLayout();
        fixture.Elements<CheckBox>().Single(box => box.Label == "显示密码").Toggle();
        fixture.Host.UpdateLayout();
        Assert.All(boxes, box => { Assert.True(box.ShowPassword); Assert.Equal("test-password", box.Text); });
        fixture.Click("取消");
        fixture.Click("新建压缩");
        Assert.All(fixture.Elements<ArchivePasswordBox>(), box => { Assert.False(box.ShowPassword); Assert.Empty(box.Text); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupMultipleArchivesKeepTheFirstAndOpenAdditionalWindows(bool preflight)
    {
        var windows = new CapturingArchiveWindows();
        using var fixture = new WindowFixture { ArchiveWindows = windows, Preflight = preflight };
        var first = fixture.CreateZip("first.zip", ("first.txt", "first"));
        var second = fixture.CreateZip("second.zip", ("second.txt", "second"));
        fixture.OpenWindow(first, second);
        fixture.WaitForIdle();
        Assert.Equal(first, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal([second], windows.OpenPaths);
    }

    [Fact]
    public void StartupOpenRequestOpensTheArchiveWithoutShowingAFilePicker()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip("打开 方式.zip", ("文档.txt", "content"));

        fixture.OpenWindow(archive);
        fixture.WaitForIdle();

        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("文档.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal(0, fixture.Desktop.ArchiveChoices);
    }

    [Fact]
    public void RunningOpenRequestSwitchesArchivesAndResetsTheArchiveDirectory()
    {
        using var fixture = new WindowFixture();
        var first = fixture.CreateZip("first.zip", ("docs/readme.txt", "first"));
        var second = fixture.CreateZip("second.zip", ("second.txt", "second"));
        fixture.OpenWindow(first);
        fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);

        Task.Run(() => fixture.Window.RequestOpenArchive(second)).GetAwaiter().GetResult();
        fixture.WaitForIdle();

        Assert.Equal(second, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.Equal("second.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal(0, fixture.Desktop.ArchiveChoices);
    }

    [Fact]
    public void OpenRequestWaitsUntilTheCurrentModalDialogIsDismissed()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("file.txt", "content"));
        fixture.OpenWindow();
        fixture.Click("新建压缩");

        fixture.Window.RequestOpenArchive(archive);
        fixture.Host.UpdateLayout();

        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Contains("添加到压缩包", fixture.Texts());
        fixture.Click("取消");
        fixture.WaitForIdle();

        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.DoesNotContain("添加到压缩包", fixture.Texts());
    }

    [Fact]
    public void OpenRequestWaitsForAnActivePickerAndThenOpensTheRequestedArchive()
    {
        using var fixture = new WindowFixture();
        var picked = fixture.CreateZip("picked.zip", ("picked.txt", "picked"));
        var requested = fixture.CreateZip("requested.zip", ("requested.txt", "requested"));
        fixture.Desktop.ArchiveChoice = picked;
        fixture.Desktop.DialogGate = new();
        fixture.OpenWindow();
        fixture.Click("打开压缩");

        fixture.Window.RequestOpenArchive(requested);
        fixture.Host.UpdateLayout();

        Assert.True(fixture.Window.TaskIsRunning);
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Equal(0, fixture.Desktop.ArchiveChoices);
        fixture.Desktop.DialogGate.SetResult(true);
        fixture.WaitForIdle();

        Assert.Equal(1, fixture.Desktop.ArchiveChoices);
        Assert.Equal(requested, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("requested.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Theory]
    [InlineData("missing.zip", false)]
    [InlineData("damaged.zip", true)]
    [InlineData("unsupported.txt", true)]
    public void InvalidOpenRequestShowsAnErrorAndContinuesAfterItIsDismissed(string name, bool createFile)
    {
        using var fixture = new WindowFixture();
        var invalid = createFile ? fixture.WriteFile(name, "Not an archive.") : Path.Combine(fixture.DirectoryPath, name);
        var valid = fixture.CreateZip(("recovered.txt", "content"));
        fixture.OpenWindow(invalid, valid);
        fixture.WaitForIdle();

        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.True(fixture.FindButton("关闭").IsEffectivelyVisible);
        fixture.Host.UpdateLayout();
        Assert.Null(fixture.Window.CurrentLocation);

        fixture.Click("关闭");
        fixture.WaitForIdle();

        Assert.Equal(valid, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("recovered.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void EncryptedOpenRequestPromptsForThePasswordAndCanBeOpened()
    {
        using var fixture = new WindowFixture();
        const string password = "打开方式-中文密码!";
        var archive = Path.Combine(fixture.DirectoryPath, "encrypted.7z");
        File.WriteAllBytes(archive, ArchiveBytes(ArchiveFormat.SevenZip, "机密.txt", Encoding.UTF8.GetBytes("secret"), password, true));
        fixture.OpenWindow(archive);
        fixture.WaitForIdle();

        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = password;
        fixture.Click("打开");
        fixture.WaitForIdle();

        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("机密.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Empty(fixture.Elements<PasswordBox>());
        Assert.Equal(0, fixture.Desktop.ArchiveChoices);
    }

    [Fact]
    public void ArchivePickerDoesNotBlockUiAndOpensTheSelectedArchive()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.ArchiveChoice = fixture.CreateZip(("file.txt", "content"));
        fixture.Desktop.DialogGate = new();
        fixture.OpenWindow();
        fixture.Click("打开压缩");
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.False(fixture.FindButton("打开压缩").IsEffectiveEnabled);
        fixture.Desktop.DialogGate.SetResult(true);
        fixture.WaitForIdle();
        Assert.Equal("file.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void SourceAndSavePickersDoNotBlockUiAndApplyTheirResults()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("source.txt", "content")];
        fixture.OpenWindow(); fixture.Click("新建压缩");
        fixture.Desktop.DialogGate = new();
        fixture.Click("添加文件…");
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.False(fixture.FindButton("开始").IsEffectiveEnabled);
        fixture.Desktop.DialogGate.SetResult(true); fixture.WaitForIdle();
        Assert.Contains("source.txt", fixture.Texts());
        fixture.Desktop.DialogGate = new();
        fixture.Desktop.SavePath = Path.Combine(fixture.DirectoryPath, "chosen.zip");
        fixture.Click("浏览…");
        Assert.True(fixture.Window.TaskIsRunning);
        fixture.Desktop.DialogGate.SetResult(true); fixture.WaitForIdle();
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.True(File.Exists(fixture.Desktop.SavePath));
    }

    [Fact]
    public void CancelledPickerReturnsToUsableWindow()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.DialogGate = new();
        fixture.OpenWindow(); fixture.Click("打开压缩");
        fixture.Desktop.DialogGate.SetResult(true); fixture.WaitForIdle();
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.True(fixture.FindButton("打开压缩").IsEffectiveEnabled);
    }

    [Fact]
    public void StartupShowsCreateOpenAndSettingsWithoutBrowsingLocalFiles()
    {
        using var fixture = new WindowFixture();
        fixture.WriteFile("本地文件.txt", "not in an archive");
        fixture.OpenWindow();
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Empty(fixture.Window.VisibleItems);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).IsEffectivelyVisible);
        Assert.Equal(new[] { "新建压缩", "打开压缩", "设置" }, fixture.Elements<Button>()
            .Where(button => button is not MenuItem && button.IsEffectivelyVisible)
            .Select(button => button.Label));
        fixture.Click("打开压缩");
        fixture.WaitForIdle();
        Assert.Equal(1, fixture.Desktop.ArchiveChoices);
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).IsEffectivelyVisible);
    }

    [Fact]
    public void OpenUsesTheFilePickerAndNavigationCannotLeaveTheArchiveRoot()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("docs/deeper/readme.txt", "content"), ("root.txt", "root"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow();
        fixture.Click("打开压缩");
        fixture.WaitForIdle();
        var pane = Assert.Single(fixture.Elements<ArchivePane>());
        Assert.True(pane.IsEffectivelyVisible);
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.True(fixture.Address.IsReadOnly);
        Assert.False(pane.UpButton.IsEffectiveEnabled);
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems, item => item.Name == "docs")));
        Assert.True(pane.UpButton.IsEffectiveEnabled);
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems, item => item.Name == "deeper")));
        Assert.Equal("docs/deeper/", fixture.Window.CurrentLocation!.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal("", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.False(pane.UpButton.IsEffectiveEnabled);
        fixture.Run(fixture.Window.NavigateUpAsync());
        fixture.Files.RaiseEvent(new KeyEventArgs(Element.KeyDownEvent, Key.Backspace));
        fixture.Host.UpdateLayout();
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.Throws<InvalidOperationException>(() => fixture.Run(fixture.Window.NavigateToAsync(fixture.DirectoryPath)));
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.DoesNotContain(fixture.Elements<Button>(), button => button.Label == "返回首页");
        Assert.All(fixture.Elements<ContextMenu>(), menu => Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Label == "返回首页"));
    }

    [Fact]
    public void NestedArchivesCanBeOpenedRecursivelyAndNavigateBackWithoutLeavingTheRoot()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.Zip, "content.txt", Encoding.UTF8.GetBytes("nested"));
        var middle = ArchiveBytes(ArchiveFormat.SevenZip, "third.zip", inner);
        var outer = ArchiveBytes(ArchiveFormat.Zip, "folder/second.7z", middle);
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, outer);
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        var secondPath = fixture.Window.CurrentLocation!.ArchivePath!;
        Assert.EndsWith("outer.zip!/folder/second.7z!/", fixture.Address.Text);
        Assert.True(Assert.Single(fixture.Elements<ArchivePane>()).UpButton.IsEffectiveEnabled);
        Assert.DoesNotContain(fixture.Elements<ArchiveCommand>(), command => command.Label == "添加" && command.IsEffectiveEnabled);
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        var thirdPath = fixture.Window.CurrentLocation!.ArchivePath!;
        Assert.EndsWith("outer.zip!/folder/second.7z!/third.zip!/", fixture.Address.Text);
        Assert.Equal("content.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(secondPath, fixture.Window.CurrentLocation!.ArchivePath);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("folder/", fixture.Window.CurrentLocation.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal("", fixture.Window.CurrentLocation!.ArchivePrefix);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).UpButton.IsEffectiveEnabled);
        fixture.Window.Dispose();
        Assert.False(File.Exists(secondPath));
        Assert.False(File.Exists(thirdPath));
    }

    [Fact]
    public void NestedArchivesHaveNoFixedDepthLimit()
    {
        using var fixture = new WindowFixture();
        var bytes = ArchiveBytes(ArchiveFormat.Zip, "final.txt", Encoding.UTF8.GetBytes("end"));
        for (var level = 0; level < 12; level++) bytes = ArchiveBytes(ArchiveFormat.Zip, $"level{level}.zip", bytes);
        var root = Path.Combine(fixture.DirectoryPath, "deep.zip");
        File.WriteAllBytes(root, bytes);
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        for (var level = 11; level >= 0; level--)
            fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Equal("final.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        for (var level = 0; level < 12; level++) fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).UpButton.IsEffectiveEnabled);
    }

    [Fact]
    public void EncryptedNestedEntryAndEncryptedInnerHeadersUseSeparatePasswords()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.SevenZip, "secret.txt", Encoding.UTF8.GetBytes("secret"), "inner-password", true);
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "inner.7z", inner, "outer-password"));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Contains("打开加密文件", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "outer-password";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "inner-password";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Equal("secret.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    [Fact]
    public void BackCanCrossNestedArchiveBoundaries()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.Zip, "file.txt", Encoding.UTF8.GetBytes("data"));
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "folder/inner.zip", inner));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Click("更多");
        var menu = Assert.Single(fixture.Elements<ContextMenu>(), candidate => candidate.Items.OfType<MenuItem>().Any(item => item.Label == "查看"));
        var view = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Label == "查看");
        var submenu = Assert.IsType<ContextMenu>(view.Submenu);
        Assert.Single(submenu.Items.OfType<MenuItem>(), item => item.Label == "后退").Click();
        fixture.WaitForIdle();
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("folder/", fixture.Window.CurrentLocation.ArchivePrefix);
    }

    [Fact]
    public void ExtractingNestedArchiveDefaultsBesideTheOriginalAndOpensOutput()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.Zip, "readme.txt", Encoding.UTF8.GetBytes("nested content"));
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "inner.zip", inner));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Click("提取"); fixture.Click("开始"); fixture.WaitForIdle();
        var output = Assert.Single(fixture.Desktop.OpenedPaths);
        Assert.Equal(Path.Combine(fixture.DirectoryPath, "inner"), output);
        Assert.Equal("nested content", File.ReadAllText(Path.Combine(output, "readme.txt")));
        Assert.EndsWith("inner.zip!/", fixture.Address.Text);
    }

    [Fact]
    public void EncryptedNestedArchivePromptsForItsOwnPasswordAndCanRetry()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.SevenZip, "readme.txt", Encoding.UTF8.GetBytes("secret"), "inner-password", true);
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "inner.7z", inner));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "wrong";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "inner-password";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Equal("readme.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        fixture.Run(fixture.Window.NavigateUpAsync());
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
    }

    [Fact]
    public void InvalidNestedArchiveKeepsTheParentOpen()
    {
        using var fixture = new WindowFixture();
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "broken.zip", Encoding.UTF8.GetBytes("invalid")));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        Assert.ThrowsAny<Exception>(() => fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems))));
        Assert.Equal(root, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    private static byte[] ArchiveBytes(ArchiveFormat format, string entry, byte[] content, string? password = null, bool encryptNames = false)
    {
        using var archive = new MemoryStream();
        using var input = new MemoryStream(content);
        new SharpCompressArchiveEngine().CreateAsync(archive, [new ArchiveWriteEntry(entry, input)], format, password, encryptNames).GetAwaiter().GetResult();
        return archive.ToArray();
    }

    [Fact]
    public void TarGzipCanBeCreatedAndOpenedFromTheInterface()
    {
        using var fixture = new WindowFixture();
        var file = fixture.WriteFile("测试.txt", "测试内容");
        fixture.Desktop.Inputs = [file];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = 3; fixture.Host.UpdateLayout();
        Assert.Empty(fixture.Elements<PasswordBox>());
        fixture.Click("开始"); fixture.WaitForIdle();
        var archive = Path.Combine(fixture.DirectoryPath, "测试.tar.gz");
        Assert.True(File.Exists(archive));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.Click("确认");
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        Assert.Equal("测试.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        fixture.Click("提取"); fixture.Click("开始"); fixture.WaitForIdle();
        Assert.True(fixture.Desktop.OpenedPaths.Count > 0, string.Join(" | ", fixture.Texts()));
        var output = Assert.Single(fixture.Desktop.OpenedPaths);
        Assert.Equal("测试内容", File.ReadAllText(Path.Combine(output, "测试.txt")));
    }

    [Fact]
    public void SingleGzipPreservesSourceExtensionAndRejectsMultipleSources()
    {
        using var fixture = new WindowFixture();
        var source = fixture.WriteFile("source.txt", "content");
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = 7; fixture.Host.UpdateLayout();
        Assert.Contains(fixture.Elements<TextBox>(), box => box.Text == Path.Combine(fixture.DirectoryPath, "source.txt.gz"));
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, "source.txt.gz")));
    }

    [Theory]
    [InlineData(2, "source.tar")]
    [InlineData(3, "source.tar.gz")]
    [InlineData(4, "source.tar.bz2")]
    [InlineData(5, "source.tar.lz")]
    [InlineData(6, "source.tar.zst")]
    [InlineData(7, "source.txt.gz")]
    [InlineData(8, "source.txt.bz2")]
    [InlineData(9, "source.txt.lz")]
    [InlineData(10, "source.txt.zst")]
    public void EachAdditionalFormatCanBeCreatedAndOpenedFromUi(int index, string filename)
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("source.txt", "content")];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = index; fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        var archive = Path.Combine(fixture.DirectoryPath, filename);
        Assert.True(File.Exists(archive), string.Join(" | ", fixture.Texts()));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.Click("确认");
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        Assert.Equal("source.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void SingleFileFormatClearlyRejectsMultipleSources()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("one.txt", "one"), fixture.WriteFile("two.txt", "two")];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = 8; fixture.Host.UpdateLayout();
        Assert.False(fixture.FindButton("开始").IsEffectiveEnabled);
        Assert.False(fixture.FindButton("添加文件夹…").IsEffectiveEnabled);
        Assert.Contains(fixture.Texts(), text => text.Contains("只能压缩单个文件"));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "Archive.bz2")));
    }

    [Fact]
    public void CompressedTarInsideZipOpensAsAnArchive()
    {
        using var fixture = new WindowFixture();
        var inner = ArchiveBytes(ArchiveFormat.TarGZip, "hello.txt", Encoding.UTF8.GetBytes("content"));
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "nested.tar.gz", inner));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.EndsWith("nested.tar.gz!/", fixture.Address.Text);
        Assert.Equal("hello.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void NestedGzipWithoutEmbeddedFilenameUsesOriginalArchiveName()
    {
        using var fixture = new WindowFixture();
        using var bytes = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes("content"));
        var root = Path.Combine(fixture.DirectoryPath, "outer.zip");
        File.WriteAllBytes(root, ArchiveBytes(ArchiveFormat.Zip, "document.txt.gz", bytes.ToArray()));
        fixture.Desktop.ArchiveChoice = root;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Equal("document.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void CreatingAnArchiveStartsAtHomeAndRemainsThereAfterSuccess()
    {
        using var fixture = new WindowFixture();
        var source = fixture.WriteFile("中文原文.txt", "往返内容 📦");
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        Assert.Contains("请选择要添加的文件或文件夹", fixture.Texts());
        fixture.Click("添加文件…");
        var archive = Path.Combine(fixture.DirectoryPath, "中文原文.zip");
        Assert.Contains(fixture.Elements<TextBox>(), box => box.Text == archive);
        fixture.Click("开始");
        fixture.WaitForIdle();
        Assert.True(File.Exists(archive));
        Assert.Equal("往返内容 📦", fixture.ReadArchiveEntry(archive, "中文原文.txt"));
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).IsEffectivelyVisible);
        Assert.Equal("压缩包创建完成。", fixture.Window.StatusMessage);
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.Contains("操作成功", fixture.Texts());
        Assert.Contains("压缩包创建完成。", fixture.Texts());
        Assert.Contains(fixture.Texts(), text => text.Contains(archive));
        var progress = Assert.Single(fixture.Elements<ProgressBar>());
        Assert.Equal(100, progress.Value);
        Assert.False(progress.IsIndeterminate);
        Assert.False(fixture.FindButton("打开压缩").IsEffectiveEnabled);
        fixture.ClickEnabled("打开文件夹");
        Assert.Equal(fixture.DirectoryPath, Assert.Single(fixture.Desktop.OpenedPaths));
        Assert.Contains("操作成功", fixture.Texts());
        fixture.ClickEnabled("确认");
        Assert.Empty(fixture.Elements<ProgressBar>());
        fixture.Desktop.ArchiveChoice = archive;
        fixture.ClickEnabled("打开压缩");
        fixture.WaitForIdle();
        Assert.Equal("中文原文.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void CreationSuccessOpensTheChosenArchiveParentFolder()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("source.txt", "content")];
        var outputFolder = fixture.CreateDirectory("chosen-output");
        var output = Path.Combine(outputFolder, "renamed.zip");
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        var suggested = Path.Combine(fixture.DirectoryPath, "source.zip");
        Assert.Single(fixture.Elements<TextBox>(), box => box.Text == suggested).Text = output;
        fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.True(File.Exists(output));
        Assert.Contains(fixture.Texts(), text => text.Contains(output));
        fixture.ClickEnabled("打开文件夹");
        Assert.Equal(outputFolder, Assert.Single(fixture.Desktop.OpenedPaths));
        fixture.ClickEnabled("确认");
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Theory]
    [InlineData(480, 320)]
    [InlineData(1000, 640)]
    public void SuccessDialogKeepsButtonsAccessibleAtDifferentWindowSizes(int width, int height)
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("source.txt", "content")];
        fixture.OpenWindow(); fixture.Host.AvailableSize = new Size(width, height); fixture.Host.UpdateLayout();
        fixture.Click("新建压缩"); fixture.Click("添加文件…"); fixture.Click("开始"); fixture.WaitForIdle();
        var frame = Assert.Single(fixture.Elements<ArchiveDialogBorder>());
        var open = fixture.FindButton("打开文件夹");
        var confirm = fixture.FindButton("确认");
        Assert.True(open.IsEffectiveEnabled && confirm.IsEffectiveEnabled);
        Assert.Same(open.Parent, confirm.Parent);
        Assert.True(frame.FinalRect.Width <= width - 32 && frame.FinalRect.Height <= height - 32);
        Assert.True(open.FinalRect.X >= 0 && confirm.FinalRect.Right <= open.Parent!.FinalRect.Width);
        Assert.True(open.FinalRect.Y >= 0 && confirm.FinalRect.Bottom <= open.Parent.FinalRect.Height);
        Assert.True(open.FinalRect.Right < confirm.FinalRect.X);
        fixture.ClickEnabled("确认");
    }

    [Fact]
    public void RepeatedSourceSelectionsCanCombineFilesAndFoldersOrBeCleared()
    {
        using var fixture = new WindowFixture();
        var file = fixture.WriteFile("first.txt", "first");
        fixture.WriteFile("folder/second.txt", "second");
        var folder = Path.Combine(fixture.DirectoryPath, "folder");
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        fixture.Desktop.Inputs = [file]; fixture.Click("添加文件…");
        fixture.Desktop.FolderChoice = folder; fixture.Click("添加文件夹…");
        Assert.Contains("已选择 2 个文件或文件夹", fixture.Texts());
        fixture.Click("清空");
        Assert.Contains("请选择要添加的文件或文件夹", fixture.Texts());
        fixture.Desktop.Inputs = [file]; fixture.Click("添加文件…");
        fixture.Desktop.FolderChoice = folder; fixture.Click("添加文件夹…");
        fixture.Click("开始"); fixture.WaitForIdle();
        var archive = Path.Combine(fixture.DirectoryPath, "Archive.zip");
        Assert.True(File.Exists(archive));
        Assert.Equal("first", fixture.ReadArchiveEntry(archive, "first.txt"));
        Assert.Equal("second", fixture.ReadArchiveEntry(archive, "folder/second.txt"));
        Assert.Null(fixture.Window.CurrentLocation);
    }

    [Fact]
    public void ExtractingOpensTheOutputFolderAndKeepsTheArchiveLocation()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("hello.txt", "content"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow();
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Click("提取");
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.Equal("content", File.ReadAllText(Path.Combine(fixture.DirectoryPath, "导航测试", "hello.txt")));
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
        var output = Path.Combine(fixture.DirectoryPath, "导航测试");
        Assert.Equal(output, Assert.Single(fixture.Desktop.OpenedPaths));
        Assert.Contains("操作成功", fixture.Texts());
        Assert.Contains("提取完成。", fixture.Texts());
        var progress = Assert.Single(fixture.Elements<ProgressBar>());
        Assert.Equal(100, progress.Value);
        Assert.False(progress.IsIndeterminate);
        fixture.ClickEnabled("打开文件夹");
        Assert.Equal(new[] { output, output }, fixture.Desktop.OpenedPaths);
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        fixture.ClickEnabled("确认");
        Assert.Empty(fixture.Elements<ProgressBar>());
    }

    [Fact]
    public void ExtractingSelectedItemsOpensTheUserSpecifiedOutputFolder()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("one.txt", "one"), ("two.txt", "two"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.SelectItem("one.txt");
        fixture.Click("提取");
        var defaultOutput = Path.Combine(fixture.DirectoryPath, "导航测试");
        var output = Path.Combine(fixture.DirectoryPath, "指定输出");
        Assert.Single(fixture.Elements<TextBox>(), box => box.Text == defaultOutput).Text = output;
        fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.Equal(output, Assert.Single(fixture.Desktop.OpenedPaths));
        fixture.ClickEnabled("打开文件夹");
        Assert.Equal(new[] { output, output }, fixture.Desktop.OpenedPaths);
        fixture.ClickEnabled("确认");
        Assert.Equal("one", File.ReadAllText(Path.Combine(output, "one.txt")));
        Assert.False(File.Exists(Path.Combine(output, "two.txt")));
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
    }

    [Fact]
    public void FailedExtractionDoesNotOpenAFileManager()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("file.txt", "content"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Click("提取");
        var defaultOutput = Path.Combine(fixture.DirectoryPath, "导航测试");
        Assert.Single(fixture.Elements<TextBox>(), box => box.Text == defaultOutput).Text = fixture.DirectoryPath;
        fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.DoesNotContain("操作成功", fixture.Texts());
        Assert.DoesNotContain(fixture.Elements<Button>(), button => button.Label == "打开文件夹" && button.IsEffectivelyVisible);
        Assert.Contains("提取文件", fixture.Texts());
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
    }

    [Fact]
    public void FailureToOpenTheOutputDoesNotReportExtractionAsFailed()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("file.txt", "content"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.Desktop.OpenFailure = new IOException("文件管理器不可用");
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Click("提取"); fixture.Click("开始"); fixture.WaitForIdle();
        var output = Path.Combine(fixture.DirectoryPath, "导航测试");
        Assert.Equal(output, Assert.Single(fixture.Desktop.OpenedPaths));
        Assert.Equal("content", File.ReadAllText(Path.Combine(output, "file.txt")));
        Assert.Equal("提取完成。", fixture.Window.StatusMessage);
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Contains("操作成功", fixture.Texts());
        Assert.Contains(fixture.Texts(), text => text.Contains("解压已完成，但无法打开输出文件夹：文件管理器不可用"));
        fixture.Desktop.OpenFailure = null;
        fixture.ClickEnabled("打开文件夹");
        Assert.Equal(new[] { output, output }, fixture.Desktop.OpenedPaths);
        Assert.DoesNotContain(fixture.Texts(), text => text.Contains("文件管理器不可用"));
        Assert.Contains("操作成功", fixture.Texts());
        fixture.ClickEnabled("确认");
    }

    [Fact]
    public void EncryptedArchiveCanBeCreatedAtHomeAndOpenedWithPassword()
    {
        using var fixture = new WindowFixture();
        var source = fixture.WriteFile("机密.txt", "秘密");
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = 1;
        fixture.Host.UpdateLayout();
        foreach (var box in fixture.Elements<PasswordBox>()) box.Text = "secret";
        Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "加密文件名").Toggle();
        fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        var archive = Path.Combine(fixture.DirectoryPath, "机密.7z");
        Assert.True(File.Exists(archive));
        Assert.Null(fixture.Window.CurrentLocation);
        fixture.Desktop.ArchiveChoice = archive;
        fixture.Click("确认");
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Contains("打开压缩包", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "secret";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("机密.txt", Assert.Single(fixture.Window.VisibleItems).Name);
    }

    [Fact]
    public void SelectedZipStoreLevelIsUsedForTheCreatedArchive()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("level.txt", new string('x', 10000))];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        fixture.Elements<ComboBox>().Skip(1).First().SelectedIndex = 0;
        fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(fixture.DirectoryPath, "level.zip"));
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(entry.Length, entry.CompressedLength);
    }

    [Fact]
    public void EncryptedZipRequiresConfirmedPasswordAndPromptsWhenOpeningAFile()
    {
        using var fixture = new WindowFixture();
        fixture.Desktop.Inputs = [fixture.WriteFile("secret.txt", "secret content")];
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("添加文件…");
        var passwords = fixture.Elements<PasswordBox>().ToArray();
        passwords[0].Text = "中文密码";
        fixture.Click("开始"); fixture.Host.UpdateLayout();
        Assert.Contains("两次输入的密码不一致。", fixture.Texts());
        passwords[1].Text = "中文密码";
        fixture.Click("开始"); fixture.WaitForIdle();
        var archive = Path.Combine(fixture.DirectoryPath, "secret.zip");
        Assert.True(File.Exists(archive));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.Click("确认");
        fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        Assert.Contains("打开加密文件", fixture.Texts());
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "wrong";
        fixture.Click("打开"); fixture.WaitForIdle();
        Assert.Contains("打开加密文件", fixture.Texts());
        Assert.Empty(fixture.Desktop.OpenedPaths);
        Assert.Single(fixture.Elements<PasswordBox>()).Text = "中文密码";
        fixture.Click("打开"); fixture.WaitForIdle();
        var opened = Assert.Single(fixture.Desktop.OpenedPaths);
        Assert.Equal("secret content", File.ReadAllText(opened));
    }

    [Fact]
    public void ExtractionDefaultsToPathsRelativeToCurrentArchiveFolder()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("docs/nested/file.txt", "content"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Click("提取"); fixture.Click("开始"); fixture.WaitForIdle();
        var output = Assert.Single(fixture.Desktop.OpenedPaths);
        Assert.Equal("content", File.ReadAllText(Path.Combine(output, "nested", "file.txt")));
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
    }

    [Fact]
    public void SelectingAnotherArchiveClearsThePreviousArchiveHistory()
    {
        using var fixture = new WindowFixture();
        var first = fixture.CreateZip(("docs/file.txt", "one"));
        var second = Path.Combine(fixture.DirectoryPath, "other.zip");
        File.Copy(first, second);
        fixture.Desktop.ArchiveChoice = first;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Run(fixture.Window.OpenItemAsync(Assert.Single(fixture.Window.VisibleItems)));
        fixture.Desktop.ArchiveChoice = second;
        fixture.ClickFileCommand("打开压缩包…"); fixture.WaitForIdle();
        Assert.Equal(second, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("", fixture.Window.CurrentLocation.ArchivePrefix);
        fixture.Click("更多");
        var menu = Assert.Single(fixture.Elements<ContextMenu>(), candidate => candidate.Items.OfType<MenuItem>().Any(item => item.Label == "查看"));
        var view = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Label == "查看");
        var submenu = Assert.IsType<ContextMenu>(view.Submenu);
        Assert.False(Assert.Single(submenu.Items.OfType<MenuItem>(), item => item.Label == "后退").IsEffectiveEnabled);
    }

    [Fact]
    public void InvalidArchiveSelectionDoesNotReplaceTheCurrentArchive()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("inside.txt", "content"));
        var invalid = fixture.WriteFile("invalid.zip", "not an archive");
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Desktop.ArchiveChoice = invalid;
        fixture.ClickFileCommand("打开压缩包…"); fixture.WaitForIdle();
        Assert.Equal(archive, fixture.Window.CurrentLocation!.ArchivePath);
        Assert.Equal("inside.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.True(Assert.Single(fixture.Elements<ArchivePane>()).IsEffectivelyVisible);
        Assert.Contains(fixture.Elements<Button>(), button => button.Label == "关闭" && button.IsEffectivelyVisible);
    }

    [Fact]
    public void HomeDialogMovesFocusInsideAndEscapeRestoresTheHomeButton()
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow();
        var create = fixture.FindButton("新建压缩");
        Assert.True(create.RequestFocus());
        fixture.Click("新建压缩");
        var frame = Assert.Single(fixture.Elements<ArchiveDialogBorder>());
        var popup = Assert.Single(fixture.Elements<ArchiveDialogPopup>());
        Assert.True(popup.IsModal && popup.IsOpen);
        Assert.Equal(PopupHostMode.Window, popup.HostMode);
        Assert.Contains(fixture.Host.Focus.Focused, frame.Descendants());
        Assert.False(create.RequestFocus());
        fixture.Host.Input.KeyPress(Key.Tab);
        Assert.Contains(fixture.Host.Focus.Focused, frame.Descendants());
        fixture.Host.Input.KeyPress(Key.Escape);
        fixture.Host.UpdateLayout();
        fixture.Host.Input.KeyRelease(Key.Escape);
        fixture.Host.UpdateLayout();
        Assert.False(popup.IsOpen);
        Assert.DoesNotContain("添加到压缩包", fixture.Texts());
        Assert.Same(create, fixture.Host.Focus.Focused);
    }

    [Fact]
    public void ClosingAFormatDropdownKeepsTheCreationPopupAndItsParametersOpen()
    {
        using var fixture = new WindowFixture();
        fixture.OpenWindow();
        fixture.Click("新建压缩");
        var popup = Assert.Single(fixture.Elements<ArchiveDialogPopup>());
        var format = fixture.Elements<ComboBox>().First();
        format.IsDropDownOpen = true;
        fixture.Host.UpdateLayout();
        format.SelectedIndex = 1;
        format.IsDropDownOpen = false;
        fixture.Host.UpdateLayout();

        Assert.True(popup.IsOpen);
        Assert.Equal(1, format.SelectedIndex);
        Assert.Contains("添加到压缩包", fixture.Texts());
        Assert.False(fixture.FindButton("开始").IsEffectiveEnabled);
        fixture.Click("取消");
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void ArchiveMenuOpensAndDismissesTheAddDialog()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("file.txt", "content"));
        fixture.Desktop.ArchiveChoice = archive;
        fixture.OpenWindow(); fixture.Click("打开压缩"); fixture.WaitForIdle();
        fixture.Click("更多");
        var menu = Assert.Single(fixture.Elements<ContextMenu>(), candidate => candidate.Items.OfType<MenuItem>().Any(item => item.Label == "文件"));
        var file = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Label == "文件");
        file.Click(); fixture.Host.UpdateLayout();
        var submenu = Assert.IsType<ContextMenu>(file.Submenu);
        Assert.Single(submenu.Items.OfType<MenuItem>(), item => item.Label == "添加到压缩包…").Click();
        fixture.Host.UpdateLayout();
        Assert.False(menu.IsOpen);
        Assert.False(submenu.IsOpen);
        Assert.Contains("添加到当前压缩包", fixture.Texts());
        fixture.Click("取消");
        Assert.DoesNotContain("添加到当前压缩包", fixture.Texts());
    }

    [Fact]
    public void AddToTheCurrentArchiveFolderPreservesExistingContent()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("docs/original.txt", "old"), ("root.txt", "root"));
        var addition = fixture.WriteFile("新增.txt", "new 📦");
        fixture.Desktop.Inputs = [addition];
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive, "docs/"));

        fixture.Click("添加");
        Assert.Contains("添加到当前压缩包", fixture.Texts());
        Assert.Empty(fixture.Elements<ComboBox>());
        fixture.Click("添加文件…");
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal("压缩包已更新。", fixture.Window.StatusMessage);
        Assert.Equal("docs/", fixture.Window.CurrentLocation!.ArchivePrefix);
        Assert.Contains(fixture.Window.VisibleItems, item => item.Path == "docs/新增.txt");
        Assert.Contains(fixture.Window.VisibleItems, item => item.Path == "docs/original.txt");
        Assert.Equal("new 📦", fixture.ReadArchiveEntry(archive, "docs/新增.txt"));
        Assert.Equal("old", fixture.ReadArchiveEntry(archive, "docs/original.txt"));
        Assert.Equal("root", fixture.ReadArchiveEntry(archive, "root.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void F2RenamesTheSelectedArchiveEntryAndRefreshesTheList()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("原名.txt", "preserved content"), ("keep.txt", "keep"));
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.SelectItem("原名.txt");

        fixture.Files.RaiseEvent(new KeyEventArgs(Element.KeyDownEvent, Key.F2));
        fixture.Host.UpdateLayout();
        Assert.Contains("重命名", fixture.Texts());
        var name = Assert.Single(fixture.Elements<TextBox>(), box => box.GetType() == typeof(TextBox) && box.Text == "原名.txt");
        name.Text = "新名.txt";
        fixture.Host.UpdateLayout();
        fixture.Click("保存");
        fixture.WaitForIdle();

        Assert.Equal("重命名完成。", fixture.Window.StatusMessage);
        Assert.Contains(fixture.Window.VisibleItems, item => item.Name == "新名.txt");
        Assert.DoesNotContain(fixture.Window.VisibleItems, item => item.Name == "原名.txt");
        Assert.Equal("preserved content", fixture.ReadArchiveEntry(archive, "新名.txt"));
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void ArchiveDeleteRequiresItsOwnConfirmationAndNeverUsesTheSystemTrash()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("remove.txt", "remove"), ("keep.txt", "keep"));
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.SelectItem("remove.txt");
        var before = File.ReadAllBytes(archive);

        fixture.Click("删除");

        Assert.Contains("从压缩包删除", fixture.Texts());
        Assert.Contains("remove.txt", fixture.Texts());
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
        fixture.ClickEnabled("删除");
        fixture.WaitForIdle();

        Assert.Equal("所选归档项目已删除。", fixture.Window.StatusMessage);
        Assert.Equal("keep.txt", Assert.Single(fixture.Window.VisibleItems).Name);
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    [Fact]
    public void AddingAConflictingFileCanBeRetriedWithExplicitReplacement()
    {
        using var fixture = new WindowFixture();
        var archive = fixture.CreateZip(("same.txt", "before"), ("keep.txt", "keep"));
        var source = fixture.WriteFile("same.txt", "after");
        var before = File.ReadAllBytes(archive);
        fixture.Desktop.Inputs = [source];
        fixture.OpenWindow();
        fixture.Run(fixture.Window.NavigateToAsync(archive));
        fixture.Click("添加");
        fixture.Click("添加文件…");

        var replace = Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "替换压缩包内的同名文件");
        Assert.False(replace.Checked);
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Contains("添加到当前压缩包", fixture.Texts());
        Assert.Contains(fixture.Texts(), text => text.Contains("已存在同名文件", StringComparison.Ordinal));
        Assert.Equal("before", fixture.ReadArchiveEntry(archive, "same.txt"));
        replace = Assert.Single(fixture.Elements<CheckBox>(), box => box.Label == "替换压缩包内的同名文件");
        replace.Toggle();
        fixture.Host.UpdateLayout();
        fixture.Click("保存修改");
        fixture.WaitForIdle();

        Assert.Equal("压缩包已更新。", fixture.Window.StatusMessage);
        Assert.Equal("after", fixture.ReadArchiveEntry(archive, "same.txt"));
        Assert.Equal("keep", fixture.ReadArchiveEntry(archive, "keep.txt"));
        Assert.Empty(fixture.Elements<CheckBox>());
        Assert.Equal(0, fixture.Desktop.TrashCalls);
    }

    private sealed class WindowFixture : IDisposable
    {
        private HeadlessRoot.UiScope? _ui;
        public string DirectoryPath { get; } = CreateTemporaryDirectory();
        public IArchiveEngine Engine { get; } = new SharpCompressArchiveEngine();
        public FakeDesktopServices Desktop { get; } = new();
        public IArchiveWindows? ArchiveWindows { get; init; }
        public bool StartCreating { get; init; }
        public bool Preflight { get; init; }
        public MainWindow Window { get; private set; } = null!;
        public HeadlessRoot Host { get; private set; } = null!;
        public ListBox Files { get; private set; } = null!;
        public TextBox Address { get; private set; } = null!;

        public void OpenWindow(params string[] requestPaths)
        {
            Window = new MainWindow
            {
                Engine = Engine,
                FileService = new ArchiveFileService(Engine),
                Desktop = Desktop,
                InitialPath = DirectoryPath,
                ArchiveWindows = ArchiveWindows,
                StartCreating = StartCreating,
                OperationOnly = StartCreating,
            };
            foreach (var path in requestPaths) Window.RequestOpenArchive(path);
            if (Preflight) Window.PrepareInitialRequests();
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(1140, 740) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _ui = Host.InstallSynchronizationContext();
            Host.UpdateLayout();
            Files = Elements<ArchivePane>().Last().Files;
            Address = Elements<ArchivePane>().Last().Address;
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();

        public IEnumerable<string> Texts() => Elements<TextBlock>()
            .Select(block => string.Concat(block.Runs.Select(run => run.Text)));

        public Button FindButton(string label) => Assert.Single(Elements<Button>(),
            button => button is not MenuItem && button.Label == label);

        public void Click(string label)
        {
            FindButton(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void ClickFileCommand(string label)
        {
            var menu = Assert.Single(Elements<ContextMenu>(), candidate => candidate.Items.OfType<MenuItem>().Any(item => item.Label == "文件"));
            var file = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Label == "文件");
            var commands = Assert.IsType<ContextMenu>(file.Submenu);
            Assert.Single(commands.Items.OfType<MenuItem>(), item => item.Label == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void ClickEnabled(string label)
        {
            var button = Assert.Single(Elements<Button>(), candidate => candidate is not MenuItem &&
                candidate.Label == label && candidate.IsEffectiveEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void SelectItem(string name)
        {
            Files.SelectedItem = Assert.Single(Window.VisibleItems, item => item.Name == name);
            Host.UpdateLayout();
        }

        public void Run(Task operation)
        {
            PumpUntil(() => operation.IsCompleted, "navigation");
            operation.GetAwaiter().GetResult();
            Host.DrainPostedWork();
            Host.UpdateLayout();
        }

        public void WaitForIdle() => PumpUntil(
            () => !Window.TaskIsRunning && Host.SynchronizationContext?.PendingCount == 0,
            "archive operation");

        private void PumpUntil(Func<bool> completed, string operation)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Host.DrainPostedWork();
                Host.UpdateLayout();
                if (completed()) return;
                if (clock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"Headless {operation} did not complete within ten seconds. " +
                        $"Status: {Window.StatusMessage}. Visible text: {string.Join(" | ", Texts())}");
                Thread.Sleep(1);
            }
        }

        public string CreateDirectory(string relativePath)
        {
            var path = LocalPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string WriteFile(string relativePath, string content)
        {
            var path = LocalPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

        public string CreateZip(params (string Path, string? Content)[] entries) => CreateZip("导航测试.zip", entries);

        public string CreateZip(string name, params (string Path, string? Content)[] entries)
        {
            var path = Path.Combine(DirectoryPath, name);
            var inputs = entries.Select(entry => new ArchiveWriteEntry(entry.Path,
                entry.Content is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(entry.Content), writable: false)))
                .ToArray();
            try
            {
                using var output = File.Create(path);
                Engine.CreateAsync(output, inputs).GetAwaiter().GetResult();
            }
            finally
            {
                foreach (var entry in inputs) entry.Content?.Dispose();
            }
            return path;
        }

        public string ReadArchiveEntry(string archivePath, string entryPath)
        {
            using var source = File.OpenRead(archivePath);
            using var output = new MemoryStream();
            Engine.ExtractEntryAsync(source, entryPath, output).GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public void Dispose()
        {
            _ui?.Dispose();
            Host?.Dispose();
            Window?.Dispose();
            foreach (var paths in Desktop.ClipboardAttempts)
            {
                var workspace = Directory.GetParent(Path.GetDirectoryName(paths[0])!)!;
                Assert.StartsWith("BubuZip-clipboard-", workspace.Name);
                if (workspace.Exists) workspace.Delete(recursive: true);
            }
            Directory.Delete(DirectoryPath, recursive: true);
        }

        private string LocalPath(string relativePath) =>
            Path.Combine(DirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        private static string CreateTemporaryDirectory()
        {
            // macOS aliases /var and /tmp through symlinks. Resolve every ancestor so
            // real workflow tests satisfy ArchiveFileService's filesystem policy.
            var temporary = Path.GetFullPath(Path.GetTempPath());
            var current = Path.GetPathRoot(temporary)!;
            foreach (var part in temporary[current.Length..].Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            var directory = Path.Combine(current, "BubuZip.MainWindow.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private sealed class CapturingArchiveWindows : IArchiveWindows
    {
        public List<string> CreatePaths { get; } = [];
        public List<string> OpenPaths { get; } = [];
        public void Create(string initialPath) => CreatePaths.Add(initialPath);
        public void Open(string archivePath) => OpenPaths.Add(archivePath);
    }

    private sealed class FakeDesktopServices : IDesktopServices, IAsyncDesktopDialogService, IFileClipboardService
    {
        public TaskCompletionSource<bool>? DialogGate { get; set; }
        public async Task<T> ShowDialogAsync<T>(Func<T> showDialog)
        {
            if (DialogGate is { } gate) await gate.Task;
            return showDialog();
        }
        public IReadOnlyList<string> Inputs { get; set; } = [];
        public string? FolderChoice { get; set; }
        public List<string?> FilePickerDirectories { get; } = [];
        public List<string?> FolderPickerDirectories { get; } = [];
        public string? ArchiveChoice { get; set; }
        public int ArchiveChoices { get; private set; }
        public string? SavePath { get; set; }
        public List<string> SaveSuggestions { get; } = [];
        public int TrashCalls { get; private set; }
        public List<string> OpenedPaths { get; } = [];
        public Exception? OpenFailure { get; set; }
        public Exception? ClipboardFailure { get; set; }
        public IReadOnlyList<string> CopiedPaths { get; private set; } = [];
        public List<IReadOnlyList<string>> ClipboardAttempts { get; } = [];
        public void CopyFiles(IReadOnlyList<string> paths)
        {
            ClipboardAttempts.Add(paths.ToArray());
            if (ClipboardFailure is not null) throw ClipboardFailure;
            CopiedPaths = paths.ToArray();
        }

        public IReadOnlyList<string> ChooseFiles(string? initialDirectory = null) { FilePickerDirectories.Add(initialDirectory); return Inputs; }
        public string? ChooseArchive() { ArchiveChoices++; return ArchiveChoice; }
        public string? ChooseFolder(string? initialDirectory = null) { FolderPickerDirectories.Add(initialDirectory); return FolderChoice; }
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null)
        {
            SaveSuggestions.Add(suggestedName);
            return SavePath;
        }
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) { OpenedPaths.Add(path); if (OpenFailure is not null) throw OpenFailure; }
        public void Trash(IReadOnlyList<string> paths) => TrashCalls++;
    }
}
