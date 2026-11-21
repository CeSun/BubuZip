using EclipseUI;
using EclipseUI.Themes.Fluent;

namespace BubuZip;

public partial class ArchiveSettingsWindow
{
    [UiParameter] public IShellIntegrationService? ShellIntegration { get; set; }
    public ArchiveAppearance? Appearance { get; set; }
    private ThemeVariant _selectedTheme = ThemeVariant.System;
    [UiParameter] public ThemeVariant SelectedTheme
    {
        get => Appearance?.Variant ?? _selectedTheme;
        set => _selectedTheme = value;
    }
    [UiParameter] public Action<ThemeVariant> ThemeChanged { get; set; } = _ => { };
    [UiParameter] public IReadOnlyList<ArchiveAssociationFormatChoice> AssociationFormats { get; set; } = ArchiveShellFormats.Extensions
        .Select(extension => new ArchiveAssociationFormatChoice(extension)).ToArray();

    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ArchiveAssociationFormatChoice> _associationFormats => AssociationFormats;
    private ArchiveAssociationFormatSelector? _associationSelector;
    private ShellIntegrationStatus? _integrationStatus;
    private bool _statusRequested, _settingsBusy;
    private string _settingsFeedback = "";
    private string _settingsError = "";
    private readonly string[] _themeNames = ["跟随系统", "浅色", "深色"];

    public bool TaskIsRunning => _settingsBusy && !IsDisposed;
    private int SelectedAssociationCount => _associationFormats.Count(format => format.Selected);
    private string SystemMenuTitle => ShellIntegration?.PlatformName ?? "系统文件管理器";
    private string AssociationStatusText => _integrationStatus?.AssociationMessage ?? "尚未读取系统关联状态。";
    private string MenuStatusText => _integrationStatus?.MenuMessage ?? "尚未读取右键菜单状态。";
    private string AssociatedFormatText => _integrationStatus is not { AssociatedExtensions.Count: > 0 } status
        ? "尚无已确认的默认关联。"
        : "已默认关联：" + string.Join("、", status.AssociatedExtensions);

    private int ThemeIndex
    {
        get => SelectedTheme switch { ThemeVariant.Light => 1, ThemeVariant.Dark => 2, _ => 0 };
        set
        {
            if (IsDisposed || value is < 0 or > 2 || value == ThemeIndex) return;
            SelectedTheme = value switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.System };
            ThemeChanged(_selectedTheme);
            Theme = (IHostTheme?)Appearance ?? FluentTheme.For(SelectedTheme);
            Invalidate();
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        if (Appearance is { } appearance) appearance.Changed += AppearanceChanged;
        Theme = (IHostTheme?)Appearance ?? FluentTheme.For(SelectedTheme);
        Invalidate();
    }

    private void AppearanceChanged()
    {
        if (!IsDisposed) Invalidate();
    }

    protected override void OnUpdating()
    {
        base.OnUpdating();
        if (_statusRequested || IsDisposed) return;
        _statusRequested = true;
        // Native hosts install their UI context after initialization.
        _ = RunSettingsActionAsync(null);
    }

    public void Close() => ArchiveWindowClose.Request(this);

    private void AssociationFormatsChanged() => Invalidate();

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
        if (ShellIntegration is { } service) await RunSettingsActionAsync(service.RegisterMenuAsync);
    }

    private async void UnregisterSystemMenu()
    {
        if (ShellIntegration is { } service) await RunSettingsActionAsync(service.UnregisterMenuAsync);
    }

    private async void RefreshSystemIntegration() => await RunSettingsActionAsync(null);

    private async Task RunSettingsActionAsync(Func<CancellationToken, Task<ShellIntegrationResult>>? action)
    {
        if (_settingsBusy || ShellIntegration is not { } service || IsDisposed) return;
        _settingsBusy = true;
        _settingsError = "";
        if (action is not null) _settingsFeedback = "";
        Invalidate();
        try
        {
            if (action is not null)
            {
                var result = await action(_lifetime.Token);
                if (IsDisposed) return;
                _settingsFeedback = result.Message;
            }
            var status = await service.GetStatusAsync(_lifetime.Token);
            if (!IsDisposed) _integrationStatus = status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed) _settingsError = "操作未完成：" + error.Message;
        }
        finally
        {
            _settingsBusy = false;
            if (!IsDisposed) Invalidate();
        }
    }

    protected override void OnDispose()
    {
        if (Appearance is { } appearance) appearance.Changed -= AppearanceChanged;
        try { _lifetime.Cancel(); }
        finally { _lifetime.Dispose(); base.OnDispose(); }
    }
}
