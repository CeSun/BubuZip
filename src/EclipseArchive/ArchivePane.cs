using EclipseUI;
using EclipseUI.Elements;

namespace EclipseArchive;

public partial class ArchivePane
{
    public Grid PaneRoot = null!;
    public FileListBox Files = null!;
    public TextBox Address = null!;
    public ArchiveCommand UpButton = null!;
    public ScrollViewer HeaderScroll = null!, FileScroll = null!;
    private ArchiveColumnGuides Guides = null!;
    [UiParameter] public Action UpRequested { get; set; } = () => { };
    [UiParameter] public Action<string> SortRequested { get; set; } = _ => { };
    public BrowserSnapshot? Snapshot { get; set; }
    public string? Password { get; set; }
    public Stack<(BrowserSnapshot Snapshot, string? Password)> History { get; } = new();
    public IReadOnlyList<BrowserItem> Rows { get; set; } = [];
    public string SortColumn { get; set; } = "name";
    public bool SortDescending { get; set; }
    private bool CanGoUp => Snapshot?.ArchivePath is not null && Snapshot.ArchivePrefix.Length > 0;
    public string Status { get; set; } = "就绪";
    private string SelectionText => $"{Files?.SelectedItems.Count ?? 0} / {Rows.Count} 个对象已选择";
    private string SizeText => Files?.SelectedItems.OfType<BrowserItem>().Where(item => !item.IsDirectory).Sum(item => item.Size) is > 0 and var size ? BrowserItem.FormatSize(size) : "";
    private string Header(string label, string column) => SortColumn == column ? label + (SortDescending ? "  ˅" : "  ˄") : label;
    public void RefreshView() => Invalidate();
    protected override void OnInitialized()
    {
        base.OnInitialized();
        Guides.ScrollOwner = FileScroll;
        FileScroll.PropertyChanged += (_, property) =>
        {
            if (property != ScrollViewer.HorizontalOffsetProperty) return;
            HeaderScroll.ScrollToHorizontalOffset(FileScroll.HorizontalOffset);
            Guides.InvalidateRender();
        };
    }
}
