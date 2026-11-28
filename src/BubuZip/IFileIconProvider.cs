using BubuUI;

namespace BubuZip;

public interface IFileIconProvider
{
    Task<IRasterImage?> GetIconAsync(string name, bool isDirectory);
}

public sealed class ArchiveFileIcon : Element
{
    private readonly ArchiveSymbol _fallback;
    private IRasterImage? _image;

    public ArchiveFileIcon(BrowserItem item, IFileIconProvider? provider)
    {
        Width = Height = 20;
        IsHitTestVisible = false;
        _fallback = item.IsDirectory ? ArchiveSymbol.Folder : item.IsArchive ? ArchiveSymbol.Archive : ArchiveSymbol.File;
        if (provider is not null) Load(provider, item);
    }

    private async void Load(IFileIconProvider provider, BrowserItem item)
    {
        try
        {
            _image = await provider.GetIconAsync(item.Name, item.IsDirectory);
            InvalidateRender();
        }
        catch { /* Keep the built-in icon if the platform service is unavailable. */ }
    }

    public override void Render(IElementDrawingContext context)
    {
        if (_image is not null) context.Image(_image, new Rect(0, 0, 20, 20));
        else ArchiveGlyph.Draw(context, _fallback, 0, 0, Foreground);
    }
}
