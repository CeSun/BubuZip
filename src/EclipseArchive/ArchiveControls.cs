using EclipseUI;
using EclipseUI.Elements;

namespace EclipseArchive;

public class ArchiveSurface : Grid
{
    public override void Render(IElementDrawingContext c) => c.FillRectangle(new(Point.Zero, FinalRect.Size), c.Background);
}

public sealed class ArchiveWelcomeIcon : Element
{
    public ArchiveWelcomeIcon() { Width = 72; Height = 72; IsHitTestVisible = false; }
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new Rect(0, 0, 72, 72), new UiColor(0, 120, 212), 16);
        c.FillRectangle(new Rect(21, 15, 30, 42), new UiColor(255, 255, 255, 235), 4);
        c.FillRectangle(new Rect(32, 15, 8, 42), new UiColor(0, 120, 212));
        for (var i = 0; i < 6; i++)
            c.FillRectangle(new Rect(34 + i % 2 * 3, 18 + i * 5, 3, 3), new UiColor(255, 255, 255));
        c.FillRectangle(new Rect(33, 49, 6, 6), new UiColor(255, 255, 255), 1);
    }
}

/// <summary>A themed dialog surface; inline theme colors are supplied by the drawing context.</summary>
public sealed class ArchiveDialogBorder : Border
{
    public override Rect? GetDrawingClip() => new(-8, -8, FinalRect.Width + 16, FinalRect.Height + 20);

    public override void Render(IElementDrawingContext c)
    {
        var bounds = new Rect(Point.Zero, FinalRect.Size);
        c.FillRectangle(new Rect(-4, 3, bounds.Width + 8, bounds.Height + 8), new UiColor(0, 0, 0, 20), CornerRadius + 4);
        c.FillRectangle(bounds, c.Background, CornerRadius);
        var thickness = BorderThickness;
        c.StrokeRectangle(new Rect(thickness / 2, thickness / 2,
            Math.Max(0, bounds.Width - thickness), Math.Max(0, bounds.Height - thickness)),
            BorderColor, thickness, Math.Max(0, CornerRadius - thickness / 2));
    }
}

public sealed class ArchiveAddressBox : TextBox
{
    private static readonly ControlTheme AddressTheme = new(typeof(ArchiveAddressBox), new FuncControlTemplate<ArchiveAddressBox>(owner =>
    {
        var presenter = new TextPresenter { Editor = owner, Part = "PART_TextPresenter", SelectionBackground = new(0, 120, 215, 80) };
        return new TemplateInstance(presenter, () =>
        {
            presenter.Foreground = owner.Foreground;
            presenter.CaretBrush = owner.Foreground;
            presenter.PlaceholderForeground = owner.Foreground with { A = 120 };
        });
    }));
    public ArchiveAddressBox() { Theme = AddressTheme; HorizontalPadding = 4; VerticalPadding = 6; MinHeight = 0; }
}

// Vector icons keep the command bar identical on macOS and Windows without an icon-font dependency.
public enum ArchiveSymbol { Add, Extract, Test, Copy, Move, Delete, Info, Settings, Play, Help, More, Up, Down, Folder, File, Archive, Computer }

public sealed class ArchiveGlyph : Element
{
    public static readonly StyledProperty<ArchiveSymbol> SymbolProperty = StyledProperty<ArchiveSymbol>.Register<ArchiveGlyph>(nameof(Symbol), ArchiveSymbol.File, new() { Invalidate = InvalidateKind.Render });
    public ArchiveSymbol Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public ArchiveGlyph() { Width = 20; Height = 20; IsHitTestVisible = false; }
    public override void Render(IElementDrawingContext c) => Draw(c, Symbol, 0, 0, Foreground);

    internal static void Draw(IElementDrawingContext c, ArchiveSymbol symbol, double x, double y, UiColor color)
    {
        void L(double a, double b, double d, double e) => c.Line(new(x + a, y + b), new(x + d, y + e), color, 1);
        void R(double a, double b, double w, double h, double radius = 0) => c.StrokeRectangle(new(x + a, y + b, w, h), color, 1, radius);
        void E(double a, double b, double w, double h) => c.Ellipse(new(x + a, y + b, w, h), UiColor.Transparent, color, 1);
        switch (symbol)
        {
            case ArchiveSymbol.Add: L(10, 3, 10, 17); L(3, 10, 17, 10); break;
            case ArchiveSymbol.Test: L(3, 10, 8, 15); L(8, 15, 18, 5); break;
            case ArchiveSymbol.Extract:
                R(2, 6, 13, 11, 1); L(2, 6, 2, 3); L(2, 3, 8, 3); L(8, 3, 10, 6);
                E(10, 10, 8, 8); L(12, 14, 16, 14); L(14, 12, 16, 14); L(14, 16, 16, 14); break;
            case ArchiveSymbol.Copy: R(7, 3, 9, 12, 1); L(4, 6, 4, 17); L(4, 17, 12, 17); break;
            case ArchiveSymbol.Move:
                E(3, 13, 5, 5); E(12, 13, 5, 5); L(6, 14, 14, 2); L(14, 14, 6, 2); break;
            case ArchiveSymbol.Delete:
                L(3, 5, 17, 5); L(8, 3, 12, 3); L(5, 6, 6, 17); L(6, 17, 14, 17); L(14, 17, 15, 6);
                L(8, 8, 8, 14); L(12, 8, 12, 14); break;
            case ArchiveSymbol.Info: E(2, 2, 16, 16); L(10, 9, 10, 14); L(10, 5, 10, 6); break;
            case ArchiveSymbol.Settings:
                E(6.5, 6.5, 7, 7); E(3, 3, 14, 14);
                for (var i = 0; i < 8; i++) { var angle = Math.PI * i / 4; L(10 + 7 * Math.Cos(angle), 10 + 7 * Math.Sin(angle), 10 + 9 * Math.Cos(angle), 10 + 9 * Math.Sin(angle)); } break;
            case ArchiveSymbol.Play: L(5, 3, 17, 10); L(17, 10, 5, 17); L(5, 17, 5, 3); break;
            case ArchiveSymbol.Help: L(6, 5, 8, 3); L(8, 3, 12, 3); L(12, 3, 14, 5); L(14, 5, 14, 8); L(14, 8, 10, 11); L(10, 11, 10, 13); L(10, 16, 10, 17); break;
            case ArchiveSymbol.More:
                for (var i = 0; i < 3; i++) c.FillRectangle(new(x + 4 + i * 5, y + 9, 2, 2), color); break;
            case ArchiveSymbol.Up: L(4, 9, 10, 3); L(10, 3, 16, 9); L(10, 3, 10, 17); break;
            case ArchiveSymbol.Down: L(5, 8, 10, 13); L(10, 13, 15, 8); break;
            case ArchiveSymbol.Folder:
                c.FillRectangle(new(x + 2, y + 4, 7, 4), new(232, 167, 28), 1);
                c.FillRectangle(new(x + 2, y + 7, 16, 10), new(255, 199, 60), 1);
                c.FillRectangle(new(x + 2, y + 7, 16, 2), new(255, 221, 111)); break;
            case ArchiveSymbol.Computer:
                c.FillRectangle(new(x + 2, y + 3, 16, 11), new(40, 174, 213), 1); R(2, 3, 16, 11);
                L(10, 14, 10, 17); L(6, 17, 14, 17); break;
            case ArchiveSymbol.Archive:
                c.FillRectangle(new(x + 4, y + 2, 12, 16), new(0, 143, 222), 1);
                for (var i = 0; i < 6; i++) c.FillRectangle(new(x + 9 + i % 2, y + 3 + i * 2, 2, 1), new(255, 255, 255)); break;
            default: R(4, 2, 12, 16); L(7, 7, 13, 7); L(7, 10, 13, 10); L(7, 13, 12, 13); break;
        }
    }
}

public class ArchiveCommand : Button
{
    private static readonly ControlTheme FlatTheme = new(typeof(ArchiveCommand),
        new FuncControlTemplate<ArchiveCommand>(_ => new TemplateInstance(new Grid())));
    public ArchiveSymbol Symbol { get; set; }
    public bool ShowLabel { get; set; }
    public bool IsColumnHeader { get; set; }
    public ArchiveCommand() { Theme = FlatTheme; Width = 48; Height = 40; MinHeight = 0; HorizontalPadding = 0; VerticalPadding = 0; }
    public override void Render(IElementDrawingContext c)
    {
        var color = IsEffectiveEnabled ? Foreground : Foreground with { A = 90 };
        if (IsHovered || IsPressed)
            c.FillRectangle(new(2, 2, Math.Max(0, FinalRect.Width - 4), Math.Max(0, FinalRect.Height - 4)),
                new(128, 128, 128, IsPressed ? (byte)38 : (byte)22), IsColumnHeader ? 0 : 4);
        if (ShowLabel)
        {
            c.PushClip(new(6, 0, Math.Max(0, FinalRect.Width - 12), FinalRect.Height));
            var font = GetValue(TextBlock.FontSizeProperty);
            var metrics = TextMeasurer.Current.Measure(Label, font);
            c.Text(Label, new(LabelAlignment == HorizontalAlignment.Right ? FinalRect.Width - metrics.Width - 8 : 8,
                (FinalRect.Height - metrics.Height) / 2), font, color);
            c.PopClip();
        }
        else ArchiveGlyph.Draw(c, Symbol, (FinalRect.Width - 20) / 2, (FinalRect.Height - 20) / 2, color);
        if (IsColumnHeader) c.Line(new(FinalRect.Width - .5, 0), new(FinalRect.Width - .5, FinalRect.Height), new(128, 128, 128, 50));
    }
}

public sealed class ArchiveSeparator : Element
{
    public ArchiveSeparator() { Width = 8; Height = 32; VerticalAlignment = VerticalAlignment.Center; }
    public override void Render(IElementDrawingContext c) => c.Line(new(4, 0), new(4, FinalRect.Height), new(128, 128, 128, 40));
}

public sealed class ArchiveColumnGuides : Element
{
    public ScrollViewer? ScrollOwner { get; set; }
    public ArchiveColumnGuides() => IsHitTestVisible = false;
    public override void Render(IElementDrawingContext c)
    {
        var color = IsLightTheme ? new UiColor(232, 240, 248) : new UiColor(58, 64, 78);
        foreach (var x in new[] { 200, 310, 470, 580, 700 })
            c.Line(new(x - (ScrollOwner?.HorizontalOffset ?? 0) - .5, 0), new(x - (ScrollOwner?.HorizontalOffset ?? 0) - .5, FinalRect.Height), color);
    }
}
