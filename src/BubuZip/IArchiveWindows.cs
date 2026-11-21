namespace BubuZip;

/// <summary>Opens independent desktop windows without changing the current document.</summary>
public interface IArchiveWindows
{
    void Create(string initialPath);
    void Open(string archivePath);
}
