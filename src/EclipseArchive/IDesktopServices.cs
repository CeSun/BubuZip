namespace EclipseArchive;

/// <summary>Platform dialogs and file actions used by the shared application UI.</summary>
public interface IDesktopServices
{
    IReadOnlyList<string> ChooseInputs();
    string? ChooseArchive();
    string? ChooseFolder(string? initialDirectory = null);
    string? ChooseSavePath(string suggestedName, string? initialDirectory = null);
    bool Confirm(string title, string message, string confirmLabel);
    void ShowMessage(string title, string message);
    void Reveal(string path);
    void Open(string path);

    /// <summary>Move the selected items to the system Trash; the caller obtains confirmation.</summary>
    void Trash(IReadOnlyList<string> paths);
}
