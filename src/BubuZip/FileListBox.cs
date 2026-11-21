using EclipseUI;
using EclipseUI.Elements;

namespace BubuZip;

/// <summary>A dense file list that retains the framework's selection and virtualization behavior.</summary>
public sealed class FileListBox : ListBox
{
    private static readonly ControlTheme RowTheme = new(typeof(ListBoxItem), new FuncControlTemplate<ListBoxItem>(row =>
    {
        var grid = new Grid();
        var chrome = new RowChrome(row);
        grid.Children.Add(chrome);
        grid.Children.Add(new ItemsPresenter { Owner = row });
        return new TemplateInstance(grid, chrome.InvalidateRender);
    }));
    protected override void OnContainerRealized(Element container, int index)
    {
        base.OnContainerRealized(container, index);
        if (container is ListBoxItem row) { row.Theme = RowTheme; row.Padding = 0; row.CornerRadius = 0; }
    }

    private sealed class RowChrome(ListBoxItem row) : Element
    {
        public override void Render(IElementDrawingContext c)
        {
            var color = row.IsSelected ? row.IsLightTheme ? new UiColor(204, 232, 255) : new UiColor(48, 65, 91)
                : row.IsHovered ? new UiColor(100, 160, 220, 24) : UiColor.Transparent;
            c.FillRectangle(new(Point.Zero, FinalRect.Size), color);
            if (row.IsSelected) c.StrokeRectangle(new(.5, .5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 1)), new UiColor(100, 160, 220, 110));
        }
    }
}
