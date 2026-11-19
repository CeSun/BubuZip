using System.Diagnostics;
using System.Text;
using EclipseArchive.Core;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

// The headless UI belongs to the test thread. Pump its synchronization context while
// navigation runs so every UI continuation and assertion stays on that same thread.
#pragma warning disable xUnit1031

namespace EclipseArchive.Ui.Tests;

public sealed class MainWindowTests
{
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
        fixture.Click("选择文件…");
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
    public void StartupShowsOnlyCreateAndOpenWithoutBrowsingLocalFiles()
    {
        using var fixture = new WindowFixture();
        fixture.WriteFile("本地文件.txt", "not in an archive");
        fixture.OpenWindow();
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.Empty(fixture.Window.VisibleItems);
        Assert.False(Assert.Single(fixture.Elements<ArchivePane>()).IsEffectivelyVisible);
        Assert.Equal(new[] { "新建压缩", "打开压缩" }, fixture.Elements<Button>()
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
        fixture.Click("返回首页");
        Assert.Null(fixture.Window.CurrentLocation);
        Assert.False(pane.IsEffectivelyVisible);
        fixture.Click("新建压缩");
        Assert.Contains("添加到压缩包", fixture.Texts());
        Assert.DoesNotContain("添加到当前压缩包", fixture.Texts());
        fixture.Click("取消");
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
        fixture.Click("返回首页");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
        fixture.Elements<ComboBox>().First().SelectedIndex = 8; fixture.Host.UpdateLayout();
        fixture.Click("开始"); fixture.WaitForIdle();
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
        fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.Click("新建压缩"); fixture.Click("选择文件…"); fixture.Click("开始"); fixture.WaitForIdle();
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
        fixture.Desktop.Inputs = [file]; fixture.Click("选择文件…");
        fixture.Desktop.Inputs = [folder]; fixture.Click("选择文件…");
        Assert.Contains("已选择 2 个文件或文件夹", fixture.Texts());
        fixture.Click("清空");
        Assert.Contains("请选择要添加的文件或文件夹", fixture.Texts());
        fixture.Desktop.Inputs = [file]; fixture.Click("选择文件…");
        fixture.Desktop.Inputs = [folder]; fixture.Click("选择文件…");
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
        fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.OpenWindow(); fixture.Click("新建压缩"); fixture.Click("选择文件…");
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
        fixture.Click("打开压缩包…"); fixture.WaitForIdle();
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
        fixture.Click("打开压缩包…"); fixture.WaitForIdle();
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
        Assert.Contains(fixture.Host.Focus.Focused, frame.Descendants());
        fixture.Host.Input.KeyPress(Key.Tab);
        Assert.Contains(fixture.Host.Focus.Focused, frame.Descendants());
        fixture.Host.Input.KeyPress(Key.Escape);
        fixture.Host.UpdateLayout();
        Assert.DoesNotContain("添加到压缩包", fixture.Texts());
        Assert.Same(create, fixture.Host.Focus.Focused);
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
        fixture.Click("选择文件…");
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
        fixture.Click("选择文件…");

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
        public MainWindow Window { get; private set; } = null!;
        public HeadlessRoot Host { get; private set; } = null!;
        public ListBox Files { get; private set; } = null!;
        public TextBox Address { get; private set; } = null!;

        public void OpenWindow()
        {
            Window = new MainWindow
            {
                Engine = Engine,
                FileService = new ArchiveFileService(Engine),
                Desktop = Desktop,
                InitialPath = DirectoryPath,
            };
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

        public string CreateZip(params (string Path, string? Content)[] entries)
        {
            var path = Path.Combine(DirectoryPath, "导航测试.zip");
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
            var directory = Path.Combine(current, "EclipseArchive.MainWindow.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private sealed class FakeDesktopServices : IDesktopServices, IAsyncDesktopDialogService
    {
        public TaskCompletionSource<bool>? DialogGate { get; set; }
        public async Task<T> ShowDialogAsync<T>(Func<T> showDialog)
        {
            if (DialogGate is { } gate) await gate.Task;
            return showDialog();
        }
        public IReadOnlyList<string> Inputs { get; set; } = [];
        public string? ArchiveChoice { get; set; }
        public int ArchiveChoices { get; private set; }
        public string? SavePath { get; set; }
        public List<string> SaveSuggestions { get; } = [];
        public int TrashCalls { get; private set; }
        public List<string> OpenedPaths { get; } = [];
        public Exception? OpenFailure { get; set; }

        public IReadOnlyList<string> ChooseInputs() => Inputs;
        public string? ChooseArchive() { ArchiveChoices++; return ArchiveChoice; }
        public string? ChooseFolder(string? initialDirectory = null) => null;
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
