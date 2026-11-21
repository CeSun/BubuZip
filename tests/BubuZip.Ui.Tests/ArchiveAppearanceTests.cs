using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Themes.Fluent;
using Xunit;
using Path = System.IO.Path;

namespace BubuZip.Ui.Tests;

public sealed class ArchiveAppearanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BubuZip-appearance-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "appearance.txt");

    [Theory]
    [InlineData(ThemeVariant.System)]
    [InlineData(ThemeVariant.Light)]
    [InlineData(ThemeVariant.Dark)]
    public void FreshProcessesLoadTheSavedPreference(ThemeVariant variant)
    {
        var first = new ArchiveAppearance(SettingsPath);
        Assert.Equal(ThemeVariant.System, first.Variant);
        first.Variant = variant;
        Assert.Equal(variant, new ArchiveAppearance(SettingsPath).Variant);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExistingHostsInDifferentProcessesFollowSavedChangesAndSystemChanges()
    {
        var clock = new ManualClock();
        var writer = new ArchiveAppearance(SettingsPath, clock);
        var reader = new ArchiveAppearance(SettingsPath, clock);
        var system = new SystemPreference();
        var first = new ThemeBinding(writer, system);
        var second = new ThemeBinding(reader, system);
        var painter = new ElementPainter(FluentTheme.Light);
        using var root = new Grid();
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.System })
        {
            writer.Variant = variant;
            clock.Advance();
            first.Update(painter, root);
            second.Update(painter, root);
            Assert.Equal(variant != ThemeVariant.Dark, first.Current.IsLight);
            Assert.Same(first.Current, second.Current);
        }
        system.Variant = ThemeVariant.Dark;
        first.Update(painter, root);
        second.Update(painter, root);
        Assert.False(first.Current.IsLight);
        Assert.Same(first.Current, second.Current);
    }

    [Fact]
    public void SystemPaletteChangesNotifyPausedHostsWithoutRecursiveNotifications()
    {
        var settings = new ArchiveAppearance(SettingsPath);
        var system = new SystemPreference();
        Assert.Same(FluentTheme.Light, settings.Resolve(system));
        var observed = new List<IHostTheme>();
        settings.Changed += () => observed.Add(settings.Resolve(system));
        system.Variant = ThemeVariant.Dark;
        Assert.Same(FluentTheme.Dark, settings.Resolve(system));
        Assert.Same(FluentTheme.Dark, settings.Resolve(system));
        Assert.Same(FluentTheme.Dark, Assert.Single(observed));
    }

    [Fact]
    public void InvalidOrRemovedPreferenceFallsBackToSystem()
    {
        var clock = new ManualClock();
        var settings = new ArchiveAppearance(SettingsPath, clock) { Variant = ThemeVariant.Dark };
        File.WriteAllText(SettingsPath, "invalid");
        clock.Advance();
        Assert.Equal(ThemeVariant.System, settings.Variant);
        settings.Variant = ThemeVariant.Light;
        File.Delete(SettingsPath);
        clock.Advance();
        Assert.Equal(ThemeVariant.System, settings.Variant);
    }

    [Fact]
    public void OpenSettingsDropdownTracksAnotherProcessEvenWhenResolvedPaletteStaysLight()
    {
        var clock = new ManualClock();
        var first = new ArchiveAppearance(SettingsPath, clock) { Variant = ThemeVariant.Light };
        var second = new ArchiveAppearance(SettingsPath, clock);
        using var window = new ArchiveSettingsWindow
        {
            Appearance = second,
            ThemeChanged = value => second.Variant = value,
        };
        window.EnsureInitialized();
        using var host = new HeadlessRoot(window) { AvailableSize = new Size(720, 640) };
        var binding = new ThemeBinding(second, new SystemPreference());
        var painter = new ElementPainter(FluentTheme.Light);
        binding.Update(painter, host.Content);
        host.UpdateLayout();
        var combo = Assert.Single(host.Content.Descendants().OfType<ComboBox>());
        Assert.Equal(1, combo.SelectedIndex);

        first.Variant = ThemeVariant.System;
        clock.Advance();
        binding.Update(painter, host.Content);
        host.UpdateLayout();
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Same(second, window.Theme);

        combo.SelectedIndex = 2;
        host.UpdateLayout();
        Assert.Equal(ThemeVariant.Dark, new ArchiveAppearance(SettingsPath).Variant);
        Assert.Same(second, window.Theme);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(1);
    }

    private sealed class SystemPreference : ISystemThemeProvider
    {
        public ThemeVariant Variant { get; set; } = ThemeVariant.Light;
    }
}
