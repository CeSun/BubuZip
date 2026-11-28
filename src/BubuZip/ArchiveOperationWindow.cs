using BubuUI;

namespace BubuZip;

public partial class ArchiveOperationWindow
{
    public ArchiveOperationSpec Spec { get; set; } = null!;
    public IDesktopServices Desktop { get; set; } = null!;
    private Action? _backgroundRequested;
    public Action? BackgroundRequested
    {
        get => _backgroundRequested;
        set { _backgroundRequested = value; Invalidate(); }
    }
    private ArchiveOperationView OperationView = null!;
    public ArchiveOperationOutcome Result => OperationView?.Result ?? new(false, false, null);
    public bool TaskIsRunning => OperationView?.TaskIsRunning == true && !IsDisposed;
    private void CloseOperation(ArchiveOperationOutcome outcome) => ArchiveWindowClose.Request(this);
    private void UpdateOperationTitle(string title) => Title = title;
    private void MoveToBackground() => BackgroundRequested?.Invoke();
}
