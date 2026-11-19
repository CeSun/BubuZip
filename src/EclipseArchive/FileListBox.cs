using EclipseUI;
using EclipseUI.Elements;

namespace EclipseArchive;

/// <summary>A dense file list that retains the framework's selection and virtualization behavior.</summary>
public sealed class FileListBox : ListBox
{
    protected override void OnContainerRealized(Element container, int index)
    {
        base.OnContainerRealized(container, index);
        if (container is ListBoxItem row) { row.Padding = 0; row.CornerRadius = 0; }
    }
}
