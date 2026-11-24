using EclipseUI.Elements;

namespace BubuZip;

public partial class MainWindow
{
    public Action? BackgroundOperationRequested { get; set; }
    private ArchiveOperationSpec? _standaloneOperation;
    private ArchiveOperationView? _standaloneOperationView;
    private ContentControl StandaloneOperationHost = null!;
    private string _standaloneOperationTitle = "";
    private TaskCompletionSource<ArchiveOperationOutcome>? _standaloneOperationClosed;
    private bool _standaloneOperationClosing;
    private void MoveStandaloneOperationToBackground() => BackgroundOperationRequested?.Invoke();

    private void UpdateStandaloneOperationTitle(string title)
    {
        _standaloneOperationTitle = title;
        Title = title;
    }

    private void CloseStandaloneOperation(ArchiveOperationOutcome outcome) =>
        _standaloneOperationClosed?.TrySetResult(outcome);

    private async Task<bool> RunStandaloneOperationAsync(ArchiveOperationSpec operation, DialogKind returnPanel)
    {
        _busy = true;
        _panel = DialogKind.None;
        _dialogError = "";
        _error = "";
        _password = "";
        _passwordConfirm = "";
        _standaloneOperation = operation;
        _standaloneOperationTitle = operation.Title;
        var view = new ArchiveOperationView
        {
            Spec = operation,
            Desktop = Desktop,
            BackgroundRequested = MoveStandaloneOperationToBackground,
            CanBackground = BackgroundOperationRequested is not null,
            CloseRequested = CloseStandaloneOperation,
            TitleChanged = UpdateStandaloneOperationTitle,
        };
        _standaloneOperationView = view;
        var closed = new TaskCompletionSource<ArchiveOperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _standaloneOperationClosed = closed;
        StandaloneOperationHost.Content = view;
        Invalidate();
        var closeWindow = false;
        try
        {
            var result = await closed.Task;
            if (_disposed) return false;
            if (result.Succeeded)
            {
                StatusMessage = operation.SuccessMessage;
                if (_openRequests.IsEmpty)
                {
                    closeWindow = true;
                    ArchiveWindowClose.Request(this);
                }
                return true;
            }
            if (result.Cancelled)
            {
                StatusMessage = "操作已取消";
                // A cancelled context-menu operation has nothing left to edit:
                // restoring the form would just pop the window back open.
                closeWindow = true;
                ArchiveWindowClose.Request(this);
                return false;
            }
            // A failed operation stays in its progress view, where the user can retry in place.
            // Confirming it closes the window instead of bouncing back to the extraction form.
            StatusMessage = result.Error ?? "操作未完成，请重试。";
            closeWindow = true;
            ArchiveWindowClose.Request(this);
            return false;
        }
        finally
        {
            _standaloneOperationClosed = null;
            _busy = false;
            // Closing is posted to the next UI turn. Keep the final content and
            // title until disposal, otherwise this turn paints a blank "Preparing"
            // window and resizes it before the native HWND is destroyed.
            if (!closeWindow)
            {
                _standaloneOperation = null;
                _standaloneOperationView = null;
                StandaloneOperationHost.Content = null;
                view.Dispose();
                _standaloneOperationClosing = true;
                if (!_disposed) Invalidate();
            }
        }
    }
}
