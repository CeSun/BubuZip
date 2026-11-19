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
    /// <summary>Open a file with its associated app, or a directory in the system file manager.</summary>
    void Open(string path);

    /// <summary>Move the selected items to the system Trash; the caller obtains confirmation.</summary>
    void Trash(IReadOnlyList<string> paths);
}

/// <summary>Dispatch dialogs whose native implementation must not block the application UI thread.</summary>
public interface IAsyncDesktopDialogService
{
    Task<T> ShowDialogAsync<T>(Func<T> showDialog);
}
