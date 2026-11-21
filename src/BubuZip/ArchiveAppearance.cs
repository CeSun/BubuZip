using EclipseUI;
using EclipseUI.Themes.Fluent;

namespace BubuZip;

/// <summary>One persisted theme preference shared by every BubuZip window and process.</summary>
public sealed class ArchiveAppearance(string path, TimeProvider? clock = null) : IHostThemeSource
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;
    private ThemeVariant _variant = ThemeVariant.System;
    private IHostTheme? _resolved;
    public event Action? Changed;

    private void UpdateVariant(ThemeVariant value)
    {
        if (_variant == value) return;
        _variant = value;
        Changed?.Invoke();
    }

    public ThemeVariant Variant
    {
        get
        {
            var now = _clock.GetUtcNow();
            if (now < _nextRead) return _variant;
            _nextRead = now.AddMilliseconds(250);
            try
            {
                var value = File.ReadAllText(path).Trim();
                UpdateVariant(value switch
                {
                    "Light" => ThemeVariant.Light,
                    "Dark" => ThemeVariant.Dark,
                    _ => ThemeVariant.System,
                });
            }
            catch (FileNotFoundException) { UpdateVariant(ThemeVariant.System); }
            catch (DirectoryNotFoundException) { UpdateVariant(ThemeVariant.System); }
            catch (IOException) { } // Retry transient sharing failures on the next frame interval.
            catch (UnauthorizedAccessException) { }
            return _variant;
        }
        set
        {
            if (value is not (ThemeVariant.System or ThemeVariant.Light or ThemeVariant.Dark))
                throw new ArgumentOutOfRangeException(nameof(value));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, value.ToString());
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _nextRead = _clock.GetUtcNow().AddMilliseconds(250);
            UpdateVariant(value);
        }
    }

    // Hosts resolve this source on their own UI threads, including modal loops.
    // Keeping the source attached also lets other processes and system changes propagate.
    public IHostTheme Resolve(ISystemThemeProvider? systemTheme = null)
    {
        var theme = FluentTheme.For(Variant);
        var resolved = theme is IHostThemeSource source ? source.Resolve(systemTheme) : theme;
        if (!ReferenceEquals(_resolved, resolved))
        {
            var hadTheme = _resolved is not null;
            _resolved = resolved;
            // The selected preference can remain System while its palette changes.
            // Notify paused owners too; set the cache first because hosts resolve again.
            if (hadTheme) Changed?.Invoke();
        }
        return resolved;
    }
}
