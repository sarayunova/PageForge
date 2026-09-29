using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PageForge.App.Wpf.ViewModels;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Feeds the Windows print pipeline one PDF page at a time (FR-VIEW-05). Each
/// page is rendered only when the spooler asks for it and dropped afterwards,
/// so printing hundreds of pages never holds them all as decoded bitmaps. Each
/// page keeps its own PDF dimensions (pt to DIP at 96/72).
/// </summary>
internal sealed class PdfPrintPaginator : DocumentPaginator
{
    private readonly Func<int, PrintPage> _render;
    private Size _pageSize = new(816, 1056);

    public PdfPrintPaginator(int pageCount, Func<int, PrintPage> render)
    {
        PageCount = pageCount;
        _render = render;
    }

    public override bool IsPageCountValid => true;

    public override int PageCount { get; }

    public override Size PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        PrintPage page = _render(pageNumber);
        double width = page.Size.WidthPt * 96.0 / 72.0;
        double height = page.Size.HeightPt * 96.0 / 72.0;
        _pageSize = new Size(width, height);

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(page.PngBytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
        }

        var size = new Size(width, height);
        return new DocumentPage(visual, size, new Rect(size), new Rect(size));
    }
}
