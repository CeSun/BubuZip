using BubuUI;
using BubuUI.Themes.Fluent;

namespace BubuZip.Desktop;

internal sealed class DesktopArchiveSettingsWindows(Application application) : IArchiveSettingsWindows
{
    public void Show(ArchiveSettingsWindow window)
    {
        window.Theme = (IHostTheme?)window.Appearance ?? FluentTheme.For(window.SelectedTheme);
        application.ShowDialog(window);
    }
}
