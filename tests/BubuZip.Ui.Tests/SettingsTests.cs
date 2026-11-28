using System.Diagnostics;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuZip.Ui.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void SettingsTabsRetainFormatSelectionWithoutChangingSystemIntegration()
    {
        var integration = new FakeShellIntegration();
        using var fixture = new SettingsFixture(integration);
        fixture.Click("取消全选");
        var zip = fixture.Elements<CheckBox>().Single(box => box.Label == ".zip");
        zip.Toggle();
        var tabs = Assert.Single(fixture.Elements<TabControl>());
        Assert.Equal(new[] { "系统", "右键菜单", "外观" }, tabs.Children.Cast<TabItem>().Select(tab => tab.Header));
        tabs.SelectedIndex = 1;
        fixture.Host.UpdateLayout();
        Assert.True(fixture.Button("注册右键菜单").IsEffectivelyVisible);
        Assert.False(zip.IsEffectivelyVisible);
        tabs.SelectedIndex = 2;
        fixture.Host.UpdateLayout();
        Assert.True(Assert.Single(fixture.Elements<ComboBox>()).IsEffectivelyVisible);
        tabs.SelectedIndex = 0;
        fixture.Host.UpdateLayout();
        Assert.True(zip.IsEffectivelyVisible && zip.Checked);
        Assert.Equal(0, integration.AssociationCalls);
        Assert.Equal(0, integration.RegisterCalls);
    }

    [Theory]
    [InlineData(720, 640, 5)]
    [InlineData(480, 640, 4)]
    [InlineData(400, 640, 3)]
    public void FormatChoicesWrapIntoCompactRowsAndSystemActionsRemainWithinReach(int width, int height, int columns)
    {
        using var fixture = new SettingsFixture(new FakeShellIntegration(), availableSize: new Size(width, height));
        var selector = Assert.Single(fixture.Elements<ArchiveAssociationFormatSelector>());
        var boxes = selector.Children.Cast<CheckBox>().ToArray();
        Assert.Equal(ArchiveShellFormats.Extensions.Count, boxes.Length);
        var rows = boxes.GroupBy(box => Math.Round(box.FinalRect.Y, 2)).ToArray();
        Assert.Equal(columns, rows.Max(row => row.Count()));
        Assert.InRange(rows.Length, 5, 9);
        Assert.All(boxes, box => Assert.True(box.FinalRect.X + box.FinalRect.Width <= selector.FinalRect.Width + .1));
        var selectorBounds = RelativeBounds(selector, fixture.Host.Content);
        Assert.True(selectorBounds.X >= 0 && selectorBounds.X + selectorBounds.Width <= width);

        foreach (var label in new[] { "关联所选格式", "注册右键菜单" })
        {
            var button = fixture.Button(label);
            var viewer = Assert.Single(fixture.Elements<ScrollViewer>(), candidate => IsAncestor(candidate, button));
            Assert.True(viewer.ViewportHeight > 0);
            Assert.True(viewer.ExtentHeight < viewer.ViewportHeight * 2.5,
                $"Settings should not need several screens of scrolling: {viewer.ExtentHeight}/{viewer.ViewportHeight}.");
            var bounds = RelativeBounds(button, viewer);
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bounds.Y);
            fixture.Host.UpdateLayout();
            bounds = RelativeBounds(button, viewer);
            Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= viewer.ViewportWidth + .1);
            Assert.True(bounds.Y >= -.1 && bounds.Y + bounds.Height <= viewer.ViewportHeight + .1);
        }
    }

    [Fact]
    public void SettingsStatusReadWaitsForTheFirstUpdateAndRunsOnce()
    {
        var integration = new FakeShellIntegration();
        using var fixture = new SettingsFixture(integration, updateLayout: false);
        Assert.Equal(0, integration.StatusReads);

        fixture.Host.UpdateLayout();
        fixture.Host.UpdateLayout();

        Assert.Equal(1, integration.StatusReads);
        Assert.Contains("文件格式关联", fixture.Texts());
        Assert.Equal(0, integration.AssociationCalls);
        Assert.Equal(0, integration.RegisterCalls);
    }

    [Fact]
    public void IndependentSettingsWindowOnlyReadsSystemStatusWhenOpened()
    {
        var integration = new FakeShellIntegration();
        using var fixture = new SettingsFixture(integration);

        Assert.Equal("BubuZip 设置", fixture.Window.Title);
        Assert.Equal(720, fixture.Window.Width);
        Assert.Equal(640, fixture.Window.Height);
        Assert.Equal(1, integration.StatusReads);
        Assert.Equal(0, integration.AssociationCalls);
        Assert.Equal(0, integration.RegisterCalls);
        Assert.Equal(0, integration.UnregisterCalls);
        Assert.Equal(ArchiveShellFormats.Extensions, fixture.Elements<CheckBox>().Select(box => box.Label));
        Assert.Contains("文件格式关联", fixture.Texts());
        Assert.Contains("访达右键菜单", fixture.Texts());
        Assert.Contains("尚无已确认的默认关联。", fixture.Texts());
        Assert.Contains(fixture.Elements<ComboBox>(), _ => true);
    }

    [Fact]
    public void AssociationUsesOnlySelectedFormatsAndDoesNotConfuseRegistrationWithDefaultSelection()
    {
        var integration = new FakeShellIntegration();
        using var fixture = new SettingsFixture(integration);
        fixture.Click("取消全选");
        Assert.False(fixture.Button("关联所选格式").IsEffectiveEnabled);
        Assert.All(fixture.Elements<CheckBox>(), box => Assert.False(box.Checked));

        var zip = fixture.Elements<CheckBox>().Single(box => box.Label == ".zip");
        zip.Toggle();
        fixture.Host.UpdateLayout();
        Assert.True(zip.Checked);
        Assert.True(Assert.Single(fixture.Elements<ArchiveAssociationFormatSelector>()).Choices.Single(choice => choice.Extension == ".zip").Selected);
        Assert.True(fixture.Button("关联所选格式").IsEffectiveEnabled);
        fixture.Click("关联所选格式");

        Assert.Equal(new[] { ".zip" }, integration.RequestedExtensions);
        Assert.Equal(1, integration.AssociationCalls);
        Assert.Equal(2, integration.StatusReads);
        Assert.Contains("已注册支持，请到系统设置确认默认应用。", fixture.Texts());
        Assert.Contains("尚无已确认的默认关联。", fixture.Texts());
        fixture.Click("全选");
        Assert.All(fixture.Elements<CheckBox>(), box => Assert.True(box.Checked));
    }

    [Fact]
    public void RegisteringAMenuPreventsDuplicateRequestsAndWaitsForActualSystemStatus()
    {
        var integration = new FakeShellIntegration { RegisterGate = new() };
        using var fixture = new SettingsFixture(integration);
        fixture.Click("注册右键菜单");

        Assert.Equal(1, integration.RegisterCalls);
        Assert.False(fixture.Button("注册右键菜单").IsEffectiveEnabled);
        Assert.False(fixture.Button("关联所选格式").IsEffectiveEnabled);
        Assert.Contains("正在处理系统设置…", fixture.Texts());
        fixture.Button("注册右键菜单").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, integration.RegisterCalls);

        integration.RegisterGate.SetResult(true);
        fixture.PumpUntil(() => integration.StatusReads == 2 && !fixture.Texts().Contains("正在处理系统设置…"));

        Assert.Contains("已安装，等待在系统设置中启用。", fixture.Texts());
        Assert.True(fixture.Button("移除右键菜单").IsEffectiveEnabled);
        Assert.False(integration.Status.MenuEnabled);
        fixture.Click("移除右键菜单");
        Assert.Equal(1, integration.UnregisterCalls);
        Assert.False(fixture.Button("移除右键菜单").IsEffectiveEnabled);
        Assert.Contains("菜单未注册。", fixture.Texts());
    }

    [Fact]
    public void RegistrationFailureIsVisibleAndCanBeRetried()
    {
        var integration = new FakeShellIntegration { RegisterError = new IOException("扩展文件缺失") };
        using var fixture = new SettingsFixture(integration);
        fixture.Click("注册右键菜单");

        Assert.Contains("操作未完成：扩展文件缺失", fixture.Texts());
        Assert.Contains("菜单未注册。", fixture.Texts());
        Assert.True(fixture.Button("注册右键菜单").IsEffectiveEnabled);
        integration.RegisterError = null;
        fixture.Click("注册右键菜单");
        Assert.Equal(2, integration.RegisterCalls);
        Assert.DoesNotContain("操作未完成：扩展文件缺失", fixture.Texts());
        Assert.Contains("已安装，等待在系统设置中启用。", fixture.Texts());
    }

    [Fact]
    public void RefreshShowsOnlyFormatsThatAreActuallyTheDefault()
    {
        var integration = new FakeShellIntegration();
        using var fixture = new SettingsFixture(integration);
        integration.Status = integration.Status with { AssociatedExtensions = [".zip", ".7z"] };

        fixture.Click("刷新状态");

        Assert.Contains("已默认关联：.zip、.7z", fixture.Texts());
        Assert.Equal(0, integration.AssociationCalls);
    }

    [Fact]
    public void PlatformsWithoutIntegrationRetainAppearanceSettingsAndExplainAvailability()
    {
        using var fixture = new SettingsFixture(null);

        Assert.Contains("当前平台暂不提供文件格式关联和系统右键菜单设置。", fixture.Texts());
        Assert.Contains(fixture.Elements<ComboBox>(), _ => true);
        Assert.DoesNotContain(fixture.Elements<Button>(), button => button.Label == "关联所选格式");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ClosingWithAThemeDropdownDoesNotApplyAThemeDuringWindowCleanup(int selectedIndex, bool open)
    {
        using var fixture = new SettingsFixture(null);
        var themes = new List<ThemeVariant>();
        fixture.Window.ThemeChanged = theme =>
        {
            Assert.False(fixture.Window.IsDisposed, "Window cleanup must not change the native host theme.");
            themes.Add(theme);
        };
        var combo = Assert.Single(fixture.Elements<ComboBox>());
        combo.SelectedIndex = selectedIndex;
        fixture.Host.UpdateLayout();
        combo.IsDropDownOpen = open;
        fixture.Host.UpdateLayout();
        themes.Clear();

        fixture.Window.Dispose();

        Assert.Empty(themes);
    }

    [Fact]
    public void ThemeSelectionUpdatesTheSettingsWindowAndNotifiesItsOwnerOnce()
    {
        using var fixture = new SettingsFixture(null, selectedTheme: ThemeVariant.Light);
        var changed = new List<ThemeVariant>();
        fixture.Window.ThemeChanged = changed.Add;
        var combo = Assert.Single(fixture.Elements<ComboBox>());
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(ThemeVariant.Light, fixture.Window.SelectedTheme);

        combo.SelectedIndex = 2;
        fixture.Host.UpdateLayout();
        combo.SelectedIndex = 2;
        fixture.Host.UpdateLayout();

        Assert.Equal(ThemeVariant.Dark, fixture.Window.SelectedTheme);
        Assert.Same(FluentTheme.Dark, fixture.Window.Theme);
        Assert.Equal(new[] { ThemeVariant.Dark }, changed);
    }

    [Fact]
    public void ReopeningSettingsWithTheSameFormatChoicesRetainsTheSelection()
    {
        var choices = new[] { new ArchiveAssociationFormatChoice(".zip"), new ArchiveAssociationFormatChoice(".7z") };
        using (var first = new SettingsFixture(new FakeShellIntegration(), associationFormats: choices))
        {
            first.Click("取消全选");
            first.Elements<CheckBox>().Single(box => box.Label == ".zip").Toggle();
            first.Host.UpdateLayout();
        }

        var integration = new FakeShellIntegration();
        using var reopened = new SettingsFixture(integration, associationFormats: choices);

        Assert.Same(choices, reopened.Window.AssociationFormats);
        Assert.True(reopened.Elements<CheckBox>().Single(box => box.Label == ".zip").Checked);
        Assert.False(reopened.Elements<CheckBox>().Single(box => box.Label == ".7z").Checked);
        reopened.Click("关联所选格式");
        Assert.Equal(new[] { ".zip" }, integration.RequestedExtensions);
    }

    [Fact]
    public void CloseButtonDefersDisposalUntilTheCurrentInputBatchFinishes()
    {
        using var fixture = new SettingsFixture(null);

        fixture.Button("关闭").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(fixture.Window.IsDisposed);
        fixture.Host.DrainPostedWork();
        Assert.True(fixture.Window.IsDisposed);
    }

    [Fact]
    public void ClosingDuringRegistrationCancelsTheRequestAndDoesNotReadStatusAgain()
    {
        var integration = new FakeShellIntegration { RegisterGate = new() };
        using var fixture = new SettingsFixture(integration);
        fixture.Click("注册右键菜单");
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.False(integration.RegisterToken.IsCancellationRequested);

        fixture.Window.Dispose();

        Assert.True(integration.RegisterToken.IsCancellationRequested);
        Assert.False(fixture.Window.TaskIsRunning);
        integration.RegisterGate.SetResult(true);
        fixture.DrainUntil(() => integration.RegisterFinished);
        Assert.Equal(1, integration.StatusReads);
        Assert.False(integration.Status.MenuInstalled);
    }

    [Fact]
    public void ClosingWhileStatusReadIsPendingIgnoresItsLateResult()
    {
        var integration = new FakeShellIntegration { StatusGate = new() };
        using var fixture = new SettingsFixture(integration);
        Assert.True(fixture.Window.TaskIsRunning);
        Assert.Equal(1, integration.StatusReads);

        fixture.Window.Dispose();
        Assert.True(integration.StatusToken.IsCancellationRequested);
        // This service deliberately ignores cancellation and finishes after the UI is gone.
        var completion = new Thread(() => integration.StatusGate.SetResult(integration.Status));
        completion.Start();
        Assert.True(completion.Join(TimeSpan.FromSeconds(5)), "The late status response did not finish.");
        var context = fixture.Host.SynchronizationContext!;
        Assert.True(context.PendingCount > 0, "The late response must return to the window's UI context.");
        fixture.DrainUntil(() => context.PendingCount == 0);

        Assert.True(fixture.Window.IsDisposed);
        Assert.False(fixture.Window.TaskIsRunning);
        Assert.Equal(1, integration.StatusReads);
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly HeadlessRoot.UiScope _scope;
        public ArchiveSettingsWindow Window { get; }
        public HeadlessRoot Host { get; }

        public SettingsFixture(IShellIntegrationService? integration, Size? availableSize = null,
            bool updateLayout = true, ThemeVariant selectedTheme = ThemeVariant.System,
            IReadOnlyList<ArchiveAssociationFormatChoice>? associationFormats = null)
        {
            Window = new ArchiveSettingsWindow
            {
                ShellIntegration = integration,
                SelectedTheme = selectedTheme,
            };
            if (associationFormats is not null) Window.AssociationFormats = associationFormats;
            Window.EnsureInitialized();
            Host = new HeadlessRoot(Window) { AvailableSize = availableSize ?? new Size(720, 640) };
            Host.Content.SetThemeDefaults(FluentTheme.Light);
            _scope = Host.InstallSynchronizationContext();
            if (updateLayout) Host.UpdateLayout();
        }

        public IEnumerable<T> Elements<T>() where T : Element => Host.Content.Descendants().OfType<T>();
        public IEnumerable<string> Texts() => Elements<TextBlock>().Select(text => string.Concat(text.Runs.Select(run => run.Text)));
        public Button Button(string label) => Assert.Single(Elements<Button>(), button => button is not MenuItem && button.Label == label);
        public void Click(string label)
        {
            Button(label).RaiseEvent(new RoutedEventArgs(BubuUI.Elements.Button.ClickEvent));
            Host.UpdateLayout();
        }

        public void PumpUntil(Func<bool> completed)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                Host.DrainPostedWork();
                Host.UpdateLayout();
                if (completed()) return;
                Thread.Sleep(1);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("Settings operation did not finish.");
        }

        public void DrainUntil(Func<bool> completed)
        {
            var context = Host.SynchronizationContext!;
            var timer = Stopwatch.StartNew();
            do
            {
                // The owning window is already disposed; only finish captured continuations.
                context.Drain();
                if (completed() && context.PendingCount == 0) return;
                Thread.Sleep(1);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("Settings continuation did not finish.");
        }

        public void Dispose()
        {
            _scope.Dispose();
            Host.Dispose();
            Window.Dispose();
        }
    }

    private static bool IsAncestor(Element ancestor, Element element)
    {
        for (var current = element.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static Rect RelativeBounds(Element element, Element ancestor)
    {
        double x = 0, y = 0;
        for (var current = element; !ReferenceEquals(current, ancestor); current = current.Parent
                 ?? throw new InvalidOperationException("The expected ancestor is not in the element tree."))
        {
            x += current.FinalRect.X;
            y += current.FinalRect.Y;
        }
        return new(x, y, element.FinalRect.Width, element.FinalRect.Height);
    }

    private sealed class FakeShellIntegration : IShellIntegrationService
    {
        public string PlatformName => "访达";
        public ShellIntegrationStatus Status { get; set; } = new([], false, false, "尚未关联格式。", "菜单未注册。");
        public int StatusReads, AssociationCalls, RegisterCalls, UnregisterCalls;
        public IReadOnlyList<string> RequestedExtensions { get; private set; } = [];
        public TaskCompletionSource<bool>? RegisterGate;
        public TaskCompletionSource<ShellIntegrationStatus>? StatusGate;
        public CancellationToken RegisterToken, StatusToken;
        public bool RegisterFinished;
        public Exception? RegisterError;

        public Task<ShellIntegrationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusReads++;
            StatusToken = cancellationToken;
            return StatusGate?.Task ?? Task.FromResult(Status);
        }

        public Task<ShellIntegrationResult> AssociateAsync(IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
        {
            AssociationCalls++;
            RequestedExtensions = extensions;
            return Task.FromResult(new ShellIntegrationResult("已注册支持，请到系统设置确认默认应用。"));
        }

        public async Task<ShellIntegrationResult> RegisterMenuAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            RegisterToken = cancellationToken;
            try
            {
                if (RegisterError is { } error) throw error;
                if (RegisterGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                Status = Status with { MenuInstalled = true, MenuEnabled = false, MenuMessage = "已安装，等待在系统设置中启用。" };
                return new("已注册菜单，请在系统设置完成启用。");
            }
            finally { RegisterFinished = true; }
        }

        public Task<ShellIntegrationResult> UnregisterMenuAsync(CancellationToken cancellationToken = default)
        {
            UnregisterCalls++;
            Status = Status with { MenuInstalled = false, MenuEnabled = false, MenuMessage = "菜单未注册。" };
            return Task.FromResult(new ShellIntegrationResult("已移除右键菜单。"));
        }
    }

}
