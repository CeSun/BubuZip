using EclipseUI;
using EclipseUI.Themes.Fluent;

namespace BubuZip;

public partial class MainWindow
{
    [UiParameter] public IShellIntegrationService? ShellIntegration { get; set; }
    public IArchiveSettingsWindows? SettingsWindows { get; set; }
    public ArchiveAppearance? Appearance { get; set; }
    private readonly ArchiveAssociationFormatChoice[] _associationFormats = ArchiveShellFormats.Extensions
        .Select(extension => new ArchiveAssociationFormatChoice(extension)).ToArray();
    private bool _showSettingsRequested;
    private bool _settingsWindowOpen;

    public void RequestShowSettings()
    {
        if (IsDisposed || _settingsWindowOpen) return;
        _showSettingsRequested = true;
        Invalidate();
    }

    private void ShowOptions() => RequestShowSettings();

    private void ShowRequestedSettings()
    {
        if (!_showSettingsRequested || TaskIsRunning || _panel != DialogKind.None || _error.Length > 0) return;
        _showSettingsRequested = false;
        if (SettingsWindows is null) return;
        _settingsWindowOpen = true;
        // Leave the parent's update and native input batch before starting the child loop.
        if (SynchronizationContext.Current is UiSynchronizationContext context)
            context.Post(static state => ((MainWindow)state!).ShowSettingsWindow(), this);
        else ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        try
        {
            if (IsDisposed) return;
            SettingsWindows?.Show(new ArchiveSettingsWindow
            {
                ShellIntegration = ShellIntegration,
                AssociationFormats = _associationFormats,
                Appearance = Appearance,
                SelectedTheme = Appearance?.Variant ?? _theme,
                ThemeChanged = SetSelectedTheme,
                MaxTicks = MaxTicks,
            });
        }
        catch (Exception error)
        {
            if (!IsDisposed) ReportError(error);
        }
        finally
        {
            _settingsWindowOpen = false;
            if (!IsDisposed) Invalidate();
        }
    }

    private void SetSelectedTheme(ThemeVariant theme)
    {
        if (IsDisposed) return;
        try
        {
            if (Appearance is { } appearance) appearance.Variant = theme;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportError(error);
            return;
        }
        _theme = theme;
        Theme = (IHostTheme?)Appearance ?? FluentTheme.For(theme);
        ApplyTheme(Theme);
        Invalidate();
    }
}
