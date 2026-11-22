using System.Diagnostics;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

// A native close must join the worker while its UI synchronization context is not being pumped.
#pragma warning disable xUnit1031

namespace BubuZip.Ui.Tests;

public sealed class ArchiveOperationWindowTests
{
    [Theory]
    [InlineData(680, 390, false)]
    [InlineData(544, 312, false)]
    [InlineData(680, 390, true)]
    [InlineData(544, 312, true)]
    public void DefaultClientAreaShowsAllStatisticsWithoutScrollingEvenWithLongPaths(int width, int height, bool completed)
    {
        using var entered = new ManualResetEventSlim();
        var release = Signal();
        var longPath = string.Join('/', Enumerable.Repeat("very-long-folder-name", 20)) + "/file.bin";
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在解压", longPath, "提取完成。", async (progress, token) =>
        {
            progress.Report(new(longPath, 2, 5) { TotalBytes = 1000, ProcessedBytes = 400, CompressedBytes = 200 });
            entered.Set();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        }, longPath, "/output", longPath, true));
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        if (completed) { release.SetResult(); fixture.WaitForFinished(); }
        var size = new Size(width, height);
        fixture.Host.AvailableSize = size;
        fixture.Update();
        fixture.Window.Content!.Arrange(new Rect(Point.Zero, size));
        Assert.Equal(0, Assert.Single(fixture.Elements<ScrollViewer>(), viewer => viewer.IsEffectivelyVisible).ScrollableHeight);
        foreach (var label in new[] { "速度：", "总大小：", "已处理：", "压缩后大小：", "压缩率：" })
        {
            var block = Assert.Single(fixture.Elements<TextBlock>(), block => string.Concat(block.Runs.Select(run => run.Text)) == label);
            var origin = block.PointFromRoot(Point.Zero);
            Assert.InRange(-origin.Y + block.FinalRect.Height, 0, height - 50);
        }
        Assert.Contains(fixture.VisibleButtons(), button => button.Label == (completed ? "确认" : "取消"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedWorkStopsAtTheNextProgressBoundaryAndCanResumeOrCancel(bool cancel)
    {
        using var entered = new ManualResetEventSlim();
        using var attempting = new ManualResetEventSlim();
        using var passed = new ManualResetEventSlim();
        var next = Signal();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "大文件.bin", "压缩完成。", async (progress, token) =>
        {
            progress.Report(new("大文件.bin", 0, 1) { ProcessedBytes = 38 * 1024, TotalBytes = 100 * 1024, CompressedBytes = 24 * 1024 });
            entered.Set();
            await next.Task.WaitAsync(token).ConfigureAwait(false);
            attempting.Set();
            progress.Report(new("大文件.bin", 1, 1) { ProcessedBytes = 100 * 1024, TotalBytes = 100 * 1024 });
            passed.Set();
        }));
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        fixture.Update();
        Assert.Equal(38, Assert.Single(fixture.Elements<ProgressBar>()).Value);
        Assert.Contains("100.00 KiB", fixture.Texts());
        Assert.Contains("38.00 KiB", fixture.Texts());
        Assert.Contains("24.00 KiB", fixture.Texts());
        Assert.Contains("63 %", fixture.Texts());
        fixture.Click("暂停");
        Assert.Contains("已暂停", fixture.Texts());
        next.SetResult();
        Assert.True(attempting.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(passed.Wait(TimeSpan.FromMilliseconds(100)));
        fixture.Click(cancel ? "取消" : "继续");
        fixture.WaitForFinished();
        Assert.Equal(cancel, fixture.Window.Result.Cancelled);
        Assert.Equal(!cancel, fixture.Window.Result.Succeeded);
        Assert.Equal(!cancel, passed.IsSet);
    }

    [Fact]
    public void BackgroundActionKeepsTheOperationAliveAndNativeCloseCancelsPausedWork()
    {
        using var entered = new ManualResetEventSlim();
        var next = Signal();
        var backgroundCalls = 0;
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在解压", "archive.zip", "完成", async (progress, token) =>
        {
            entered.Set();
            await next.Task.WaitAsync(token).ConfigureAwait(false);
            progress.Report(new("file.bin", 0, 1));
        }));
        fixture.Window.BackgroundRequested = () => backgroundCalls++;
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        fixture.Click("后台");
        Assert.Equal(1, backgroundCalls);
        Assert.True(fixture.Window.TaskIsRunning);
        fixture.Click("暂停");
        next.SetResult();
        fixture.Window.Dispose();
        Assert.True(fixture.Window.Result.Cancelled);
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Fact]
    public void InitializationDoesNotStartTheOperationBeforeTheFirstHostedUpdate()
    {
        using var entered = new ManualResetEventSlim();
        SynchronizationContext? executionContext = new SynchronizationContext();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。", (_, _) =>
        {
            executionContext = SynchronizationContext.Current;
            entered.Set();
            return Task.CompletedTask;
        }));

        Assert.False(entered.IsSet);
        Assert.False(fixture.Window.TaskIsRunning);
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        fixture.WaitForFinished();

        Assert.Null(executionContext);
        Assert.True(fixture.Window.Result.Succeeded);
        Assert.False(fixture.Window.IsDisposed);
    }

    [Fact]
    public void ProgressAndSuccessRemainVisibleUntilConfirmationAndTheResultFolderCanBeOpened()
    {
        using var entered = new ManualResetEventSlim();
        var release = Signal();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在提取", "archive.zip", "提取完成。", async (progress, token) =>
        {
            progress.Report(new("目录/文件.txt", 2, 5));
            entered.Set();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        }, "/output/archive", "/output/archive"));
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        fixture.Update();

        Assert.Equal(40, Assert.Single(fixture.Elements<ProgressBar>()).Value);
        Assert.False(Assert.Single(fixture.Elements<ProgressBar>()).IsIndeterminate);
        Assert.Contains("2 / 5", fixture.Texts());
        Assert.Contains("目录/文件.txt", fixture.Texts());
        Assert.DoesNotContain(fixture.VisibleButtons(), button => button.Label == "打开文件夹");

        release.SetResult();
        fixture.WaitForFinished();

        Assert.Equal(new ArchiveOperationOutcome(true, false, null), fixture.Window.Result);
        Assert.Equal(100, Assert.Single(fixture.Elements<ProgressBar>()).Value);
        Assert.Contains("操作成功", fixture.Texts());
        Assert.Contains("提取完成。", fixture.Texts());
        Assert.Contains("输出位置：/output/archive", fixture.Texts());
        fixture.Click("打开文件夹");
        Assert.Equal(["/output/archive"], fixture.Desktop.OpenedPaths);
        fixture.Click("确认");
        Assert.True(fixture.Window.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureDisplaysAndReturnsItsFriendlyError(bool cryptographic)
    {
        Exception error = cryptographic
            ? new SharpCompress.Common.CryptographicException("Invalid password")
            : new IOException("无法写入输出文件。");
        var expected = cryptographic ? "密码不正确，或压缩包的加密数据已损坏。" : error.Message;
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。",
            (_, _) => Task.FromException(error), "/output/archive.zip", "/output"));
        fixture.Update();
        fixture.WaitForFinished();

        Assert.Equal(new ArchiveOperationOutcome(false, false, expected), fixture.Window.Result);
        Assert.Contains("操作失败", fixture.Texts());
        Assert.Contains(expected, fixture.Texts());
        Assert.DoesNotContain(fixture.VisibleButtons(), button => button.Label == "打开文件夹");
        Assert.False(Assert.Single(fixture.Elements<ProgressBar>()).IsIndeterminate);
        fixture.Click("确认");
        Assert.True(fixture.Window.IsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmingWithRealPointerEventsDefersDisposalUntilTheEventBatchHasFinished(bool succeeds)
    {
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。",
            (_, _) => succeeds ? Task.CompletedTask : Task.FromException(new IOException("无法写入输出文件。"))));
        fixture.Update();
        fixture.WaitForFinished();
        var expected = new ArchiveOperationOutcome(succeeds, false, succeeds ? null : "无法写入输出文件。");
        Assert.Equal(expected, fixture.Window.Result);
        var button = Assert.Single(fixture.VisibleButtons(), button => button.Label == "确认");
        var localOrigin = button.PointFromRoot(Point.Zero);
        var point = new Point(button.FinalRect.Width / 2 - localOrigin.X, button.FinalRect.Height / 2 - localOrigin.Y);
        Assert.Contains(fixture.Host.Input.HitTest(point), button.Descendants().Prepend(button));

        fixture.Host.PointerPress(point);
        fixture.Host.PointerRelease(point);

        // The native pump can still deliver hover/focus events after the release.
        Assert.False(fixture.Window.IsDisposed);
        fixture.Host.PointerMove(new Point(1, 1));
        Assert.False(fixture.Window.IsDisposed);
        Assert.True(fixture.Host.DrainPostedWork() > 0);
        Assert.True(fixture.Window.IsDisposed);
        Assert.Equal(expected, fixture.Window.Result);
        Assert.False(fixture.Window.TaskIsRunning);
    }

    [Fact]
    public void ClosingRunningWorkJoinsAsynchronousCleanupWithoutPumpingTheUi()
    {
        using var entered = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。", async (_, token) =>
        {
            entered.Set();
            try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
            finally
            {
                await Task.Yield();
                using var registration = token.Register(() => { });
                cleaned.Set();
            }
        }));
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        fixture.Window.Dispose();

        Assert.True(cleaned.IsSet);
        Assert.Equal(new ArchiveOperationOutcome(false, true, null), fixture.Window.Result);
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.Equal(0, fixture.Host.SynchronizationContext!.PendingCount);
    }

    [Fact]
    public void CancellingAnOperationThatAlreadyCommittedStillReportsSuccess()
    {
        using var committed = new ManualResetEventSlim();
        var finishCleanup = Signal();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。", async (_, token) =>
        {
            // The output has been committed; cancellation may only shorten the remaining cleanup.
            using var registration = token.Register(() => finishCleanup.TrySetResult());
            committed.Set();
            await finishCleanup.Task.ConfigureAwait(false);
        }, "/output/archive.zip", "/output"));
        fixture.Update();
        Assert.True(committed.Wait(TimeSpan.FromSeconds(5)));

        fixture.Click("取消");
        fixture.WaitForFinished();

        Assert.Equal(new ArchiveOperationOutcome(true, false, null), fixture.Window.Result);
        Assert.Contains("操作成功", fixture.Texts());
        Assert.DoesNotContain("操作已取消", fixture.Texts());
        Assert.Equal(100, Assert.Single(fixture.Elements<ProgressBar>()).Value);
    }

    [Fact]
    public void CancellingBeforeCommitShowsCancellationWithoutAnOutputAction()
    {
        using var entered = new ManualResetEventSlim();
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在提取", "archive.zip", "提取完成。", async (_, token) =>
        {
            entered.Set();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }, "/output/archive", "/output/archive"));
        fixture.Update();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        fixture.Click("取消");
        fixture.WaitForFinished();

        Assert.Equal(new ArchiveOperationOutcome(false, true, null), fixture.Window.Result);
        Assert.Contains("操作已取消", fixture.Texts());
        Assert.DoesNotContain("操作成功", fixture.Texts());
        Assert.DoesNotContain(fixture.VisibleButtons(), button => button.Label == "打开文件夹");
        Assert.Empty(fixture.Desktop.OpenedPaths);
    }

    [Fact]
    public void ClosingBeforeTheFirstUpdateDoesNotStartWorkAndReturnsCancellation()
    {
        var ran = false;
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。", (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        fixture.Window.Dispose();

        Assert.False(ran);
        Assert.Equal(new ArchiveOperationOutcome(false, true, null), fixture.Window.Result);
    }

    [Fact]
    public void CancellingBeforeTheFirstUpdateDoesNotSubmitWorkToAStoppedOwner()
    {
        var ran = false;
        using var fixture = new OperationFixture(new ArchiveOperationSpec("正在压缩", "文件.txt", "压缩完成。", (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }));

        fixture.Click("取消");

        Assert.False(ran);
        Assert.Equal(new ArchiveOperationOutcome(false, true, null), fixture.Window.Result);
        Assert.Contains("操作已取消", fixture.Texts());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OperationFixture : IDisposable
    {
        private readonly HeadlessRoot.UiScope _ui;
        public ArchiveOperationWindow Window { get; }
        public HeadlessRoot Host { get; }
        public FakeDesktopServices Desktop { get; } = new();

        public OperationFixture(ArchiveOperationSpec operation)
        {
            Window = new ArchiveOperationWindow { Spec = operation, Desktop = Desktop };
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = new Size(680, 390) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _ui = Host.InstallSynchronizationContext();
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();
        public IEnumerable<Button> VisibleButtons() => Elements<Button>().Where(button => button.IsEffectivelyVisible);
        public IEnumerable<string> Texts() => Elements<TextBlock>().Select(block => string.Concat(block.Runs.Select(run => run.Text)));
        public void Update() => Host.UpdateLayout();

        public void Click(string label)
        {
            var button = Assert.Single(VisibleButtons(), button => button.Label == label);
            Assert.True(button.IsEffectiveEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Host.DrainPostedWork();
            if (!Window.IsDisposed) Update();
        }

        public void WaitForFinished()
        {
            var elapsed = Stopwatch.StartNew();
            var spin = new SpinWait();
            while (true)
            {
                // Worker state is applied by updates; no posted UI continuation is required.
                Update();
                if (!Window.TaskIsRunning) return;
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Operation window did not finish.");
                spin.SpinOnce();
            }
        }

        public void Dispose()
        {
            try { Window.Dispose(); }
            finally { _ui.Dispose(); Host.Dispose(); }
        }
    }

    private sealed class FakeDesktopServices : IDesktopServices
    {
        public List<string> OpenedPaths { get; } = [];
        public IReadOnlyList<string> ChooseFiles(string? initialDirectory = null) => [];
        public string? ChooseArchive() => null;
        public string? ChooseFolder(string? initialDirectory = null) => null;
        public string? ChooseSavePath(string suggestedName, string? initialDirectory = null) => null;
        public bool Confirm(string title, string message, string confirmLabel) => false;
        public void ShowMessage(string title, string message) { }
        public void Reveal(string path) { }
        public void Open(string path) => OpenedPaths.Add(path);
        public void Trash(IReadOnlyList<string> paths) { }
    }
}
