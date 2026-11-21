namespace EclipseArchive;

/// <summary>Presents settings in a separate window until it is closed.</summary>
public interface IArchiveSettingsWindows
{
    void Show(ArchiveSettingsWindow window);
}
