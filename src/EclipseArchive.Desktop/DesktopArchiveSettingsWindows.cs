using EclipseUI;
using EclipseUI.Themes.Fluent;

namespace EclipseArchive.Desktop;

internal sealed class DesktopArchiveSettingsWindows(Application application) : IArchiveSettingsWindows
{
    public void Show(ArchiveSettingsWindow window)
    {
        var themeChanged = window.ThemeChanged;
        window.Theme = FluentTheme.For(window.SelectedTheme);
        window.ThemeChanged = theme =>
        {
            if (window.IsDisposed) return;
            themeChanged(theme);
            application.SetTheme(FluentTheme.For(theme));
        };
        try { application.ShowDialog(window); }
        finally { window.ThemeChanged = themeChanged; }
    }
}
