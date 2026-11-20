using EclipseUI;
using EclipseUI.Elements;

namespace EclipseArchive;

public partial class MainWindow
{
    [UiParameter] public IShellIntegrationService? ShellIntegration { get; set; }

    private readonly ArchiveAssociationFormatChoice[] _associationFormats = ArchiveShellFormats.Extensions
        .Select(extension => new ArchiveAssociationFormatChoice(extension)).ToArray();
    private ArchiveAssociationFormatSelector? _associationSelector;
    private ShellIntegrationStatus? _integrationStatus;
    private bool _settingsBusy;
    private string _settingsFeedback = "";
    private string _settingsError = "";
    private bool _showSettingsRequested;

    public void RequestShowSettings()
    {
        _showSettingsRequested = true;
        Invalidate();
    }

    private void ShowRequestedSettings()
    {
        if (!_showSettingsRequested || TaskIsRunning || _panel != DialogKind.None || _error.Length > 0) return;
        _showSettingsRequested = false;
        ShowOptions();
    }
    private int SelectedAssociationCount => _associationFormats.Count(format => format.Selected);
    private void AssociationFormatsChanged() => Invalidate();
    private string SystemMenuTitle => ShellIntegration?.PlatformName ?? "系统文件管理器";
    private string AssociationStatusText => _integrationStatus?.AssociationMessage ?? "尚未读取系统关联状态。";
    private string MenuStatusText => _integrationStatus?.MenuMessage ?? "尚未读取右键菜单状态。";
    private string AssociatedFormatText => _integrationStatus is not { AssociatedExtensions.Count: > 0 } status
        ? "尚无已确认的默认关联。"
        : "已默认关联：" + string.Join("、", status.AssociatedExtensions);

    private async void ShowOptions()
    {
        if (_settingsBusy || _busy || _pickerBusy) return;
        _dialogError = "";
        _settingsFeedback = "";
        _settingsError = "";
        _panel = DialogKind.Options;
        Invalidate();
        await RunSettingsActionAsync(null);
    }

    private void SelectAllAssociationFormats()
    {
        foreach (var format in _associationFormats) format.Selected = true;
        _associationSelector?.SynchronizeChoices();
        Invalidate();
    }

    private void ClearAssociationFormats()
    {
        foreach (var format in _associationFormats) format.Selected = false;
        _associationSelector?.SynchronizeChoices();
        Invalidate();
    }

    private async void AssociateSelectedFormats()
    {
        var extensions = _associationFormats.Where(format => format.Selected).Select(format => format.Extension).ToArray();
        if (extensions.Length == 0 || ShellIntegration is not { } service) return;
        await RunSettingsActionAsync(token => service.AssociateAsync(extensions, token));
    }

    private async void RegisterSystemMenu()
    {
        if (ShellIntegration is not { } service) return;
        await RunSettingsActionAsync(service.RegisterMenuAsync);
    }

    private async void UnregisterSystemMenu()
    {
        if (ShellIntegration is not { } service) return;
        await RunSettingsActionAsync(service.UnregisterMenuAsync);
    }

    private async void RefreshSystemIntegration() => await RunSettingsActionAsync(null);

    private async Task RunSettingsActionAsync(Func<CancellationToken, Task<ShellIntegrationResult>>? action)
    {
        if (_settingsBusy || ShellIntegration is not { } service || _disposed) return;
        _settingsBusy = true;
        _settingsError = "";
        if (action is not null) _settingsFeedback = "";
        Invalidate();
        try
        {
            if (action is not null)
            {
                var result = await action(_lifetime.Token);
                if (_disposed) return;
                _settingsFeedback = result.Message;
            }
            var status = await service.GetStatusAsync(_lifetime.Token);
            if (!_disposed) _integrationStatus = status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed) _settingsError = "操作未完成：" + error.Message;
        }
        finally
        {
            _settingsBusy = false;
            if (!_disposed) Invalidate();
        }
    }
}

public sealed class ArchiveAssociationFormatChoice(string extension)
{
    public string Extension { get; } = extension;
    public bool Selected { get; set; } = true;
}

/// <summary>Direct checkbox children allow WrapPanel to wrap individual formats, rather than a DSL region.</summary>
public sealed class ArchiveAssociationFormatSelector : WrapPanel
{
    private IReadOnlyList<ArchiveAssociationFormatChoice> _choices = [];
    private bool _synchronizing;
    public Action? SelectionChanged { get; set; }

    public ArchiveAssociationFormatSelector()
    {
        Gap = 4;
        MaxWidth = 500;
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    public IReadOnlyList<ArchiveAssociationFormatChoice> Choices
    {
        get => _choices;
        set
        {
            _synchronizing = true;
            try
            {
                if (!ReferenceEquals(_choices, value))
                {
                    _choices = value;
                    Children.Clear();
                    foreach (var choice in _choices)
                    {
                        var box = new CheckBox { Width = 94, Label = choice.Extension, Checked = choice.Selected };
                        box.AddHandler(CheckBox.CheckedChangedEvent, () =>
                        {
                            if (_synchronizing) return;
                            choice.Selected = box.Checked;
                            SelectionChanged?.Invoke();
                        });
                        Children.Add(box);
                    }
                }
                else SynchronizeChoices();
            }
            finally { _synchronizing = false; }
        }
    }

    // The UI compiler skips a property push when its collection reference is unchanged.
    // Bulk model edits therefore refresh the existing checkbox values explicitly.
    public void SynchronizeChoices()
    {
        var wasSynchronizing = _synchronizing;
        _synchronizing = true;
        try
        {
            for (var index = 0; index < _choices.Count; index++)
                ((CheckBox)Children[index]).Checked = _choices[index].Selected;
        }
        finally { _synchronizing = wasSynchronizing; }
    }
}
