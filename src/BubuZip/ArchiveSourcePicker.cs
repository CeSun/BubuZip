using EclipseUI;

namespace BubuZip;

public partial class ArchiveSourcePicker
{
    [UiParameter] public IReadOnlyList<string> Sources { get; set; } = [];
    [UiParameter] public bool CanAddFolder { get; set; } = true;
    [UiParameter] public Action AddFilesRequested { get; set; } = () => { };
    [UiParameter] public Action AddFolderRequested { get; set; } = () => { };
    [UiParameter] public Action ClearRequested { get; set; } = () => { };
    [UiParameter] public Action<string> RemoveRequested { get; set; } = _ => { };
}
