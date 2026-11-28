using BubuUI;

namespace BubuZip;

public partial class ArchiveSourcePicker
{
    public IReadOnlyList<string> Sources { get; set; } = [];
    public bool CanAddFolder { get; set; } = true;
    public Action AddFilesRequested { get; set; } = () => { };
    public Action AddFolderRequested { get; set; } = () => { };
    public Action ClearRequested { get; set; } = () => { };
    public Action<string> RemoveRequested { get; set; } = _ => { };
}
