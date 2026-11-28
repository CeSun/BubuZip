using BubuUI;
using BubuUI.Elements;

namespace BubuZip;

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
