using EclipseUI;

namespace BubuZip;

public partial class MainWindow
{
    private ArchiveDialogPopup CreatePopup = null!;
    private bool _synchronizingCreatePopup;
    private TaskCompletionSource<bool>? _createPopupClosedForOperation;
    private bool UsesCreatePopup => !OperationOnly && _panel == DialogKind.Create;

    private void SynchronizeCreatePopup()
    {
        _synchronizingCreatePopup = true;
        try
        {
            if (UsesCreatePopup)
            {
                if (CreatePopup.Content is null)
                {
                    DialogOverlay.Children.Remove(DialogFrame);
                    CreatePopup.Content = DialogFrame;
                }
                CreatePopup.Open();
            }
            else
            {
                CreatePopup.Close();
                if (CreatePopup.Content is not null)
                {
                    CreatePopup.Content = null;
                    DialogOverlay.Children.Add(DialogFrame);
                }
            }
        }
        finally { _synchronizingCreatePopup = false; }
    }

    private void CloseCreatePopup()
    {
        if (!_synchronizingCreatePopup && !CreatePopup.IsOpen && UsesCreatePopup) CloseDialog();
    }

    private async Task<bool> RunOperationWindowAsync(ArchiveOperationSpec operation, DialogKind returnPanel)
    {
        if (OperationOnly) return await RunStandaloneOperationAsync(operation, returnPanel);
        if ((CreatePopup.IsOpen || _presentedPanel == DialogKind.Create) &&
            SynchronizationContext.Current is UiSynchronizationContext)
        {
            _busy = true;
            _panel = DialogKind.None;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _createPopupClosedForOperation = closed;
            Invalidate();
            try
            {
                // OnUpdated closes the popup in this frame. Its continuation runs during
                // the next UI drain, after the desktop host has released native modality.
                if (!await closed.Task || _disposed) return false;
            }
            finally { _createPopupClosedForOperation = null; }
        }
        return !_disposed && RunOperationWindow(operation, returnPanel);
    }
}
