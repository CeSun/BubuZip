using BubuUI;
using BubuUI.Elements;

namespace BubuZip;

/// <summary>Two compact form columns, stacked when a narrow window cannot fit them.</summary>
public sealed class ArchiveFormColumns : Panel
{
    private const double Gap = 24;
    private static bool IsWide(double width) => width >= 600;

    protected override Size MeasureCore(Size available)
    {
        if (Children.Count == 0) return Size.Empty;
        var availableWidth = double.IsFinite(available.Width) ? available.Width : 720;
        var wide = IsWide(availableWidth);
        var width = wide ? Math.Max(0, (availableWidth - Gap) / 2) : availableWidth;
        foreach (var child in Children) child.Measure(new(width, double.PositiveInfinity));
        return new(availableWidth, wide ? Children.Max(child => child.DesiredSize.Height)
            : Children.Sum(child => child.DesiredSize.Height) + Gap * Math.Max(0, Children.Count - 1));
    }

    protected override void ArrangeCore(Rect final)
    {
        var wide = IsWide(final.Width);
        var width = wide ? Math.Max(0, (final.Width - Gap) / 2) : final.Width;
        double offset = 0;
        foreach (var child in Children)
        {
            child.Arrange(new(wide ? offset : 0, wide ? 0 : offset, width, child.DesiredSize.Height));
            offset += (wide ? width : child.DesiredSize.Height) + Gap;
        }
    }
}

/// <summary>Desktop-style group border with its caption inset into the upper edge.</summary>
public sealed class ArchiveGroupBox : ContentControl
{
    public string Header { get; set; } = "";
    protected override Size MeasureCore(Size available)
    {
        Content?.Measure(new(Math.Max(0, available.Width - 24), Math.Max(0, available.Height - 38)));
        return new((Content?.DesiredSize.Width ?? 0) + 24, (Content?.DesiredSize.Height ?? 0) + 38);
    }
    protected override void ArrangeCore(Rect final) => Content?.Arrange(new(12, 26,
        Math.Max(0, final.Width - 24), Math.Max(0, final.Height - 38)));
    public override void Render(IElementDrawingContext context)
    {
        context.FillRectangle(new(Point.Zero, FinalRect.Size), context.Background);
        context.StrokeRectangle(new(.5, 8.5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 9)),
            new UiColor(128, 128, 128, 65), 1);
        var caption = TextMeasurer.Current.Measure(Header, 13);
        context.FillRectangle(new(8, 0, caption.Width + 8, caption.Height), context.Background);
        context.Text(Header, new(12, 0), 13, context.Foreground);
    }
}

public sealed class ArchivePasswordBox : PasswordBox
{
    private bool _showPassword;
    public bool ShowPassword
    {
        get => _showPassword;
        set { if (_showPassword == value) return; _showPassword = value; InvalidateMeasure(); InvalidateRender(); }
    }
    protected override string DisplayText => ShowPassword ? Text : base.DisplayText;
    protected override string DisplayComposition => ShowPassword ? CompositionText : base.DisplayComposition;
}
