using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PageForge.App.Wpf.Resources;
using PageForge.App.Wpf.ViewModels;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Snip tool (FR-VIEW-06): drag a rectangle on a page, then copy it to the
/// clipboard or save it as a PNG. The page is shown at screen resolution for
/// choosing, but the snip itself is cut from a fresh 300 DPI render of that page,
/// so it is sharp rather than a screen grab, and reflects the in-memory
/// document (unsaved edits included). Fully offline.
/// </summary>
internal sealed class SnipWindow : Window
{
    private const float PreviewDpi = 144f;
    private const float SnipDpi = 300f;
    private const double MinSelectionDip = 4;

    private readonly int _pageCount;
    private readonly string _documentName;
    private readonly Func<int, float, CancellationToken, Task<PrintPage>> _render;

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
    private readonly Grid _pageHost = new() { Background = Brushes.White, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Rectangle _selection = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x1a, 0x73, 0xe8)),
        StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 4, 2 },
        Fill = new SolidColorBrush(Color.FromArgb(40, 0x1a, 0x73, 0xe8)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 90, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _hint = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _prev = new() { Content = "◀", Padding = new Thickness(10, 3, 10, 3) };
    private readonly Button _next = new() { Content = "▶", Padding = new Thickness(10, 3, 10, 3) };
    private readonly Button _wholePage = new() { Content = UiStrings.Get("Snip_WholePage"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _copy = new() { Content = UiStrings.Get("Snip_Copy"), Padding = new Thickness(14, 3, 14, 3), IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _save = new() { Content = UiStrings.Get("Snip_Save"), Padding = new Thickness(14, 3, 14, 3), IsEnabled = false };

    private int _index;
    private Point _dragStart;
    private bool _dragging;
    // The chosen region as fractions of the page (0..1), so it applies to a
    // render at any DPI.
    private Rect _fraction = Rect.Empty;
    private CancellationTokenSource? _cts;

    public SnipWindow(
        int pageCount,
        int startIndex,
        string documentName,
        Func<int, float, CancellationToken, Task<PrintPage>> render)
    {
        _pageCount = pageCount;
        _documentName = documentName;
        _render = render;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, pageCount - 1));

        Title = UiStrings.Format("Snip_Title", documentName);
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _hint.Text = UiStrings.Get("Snip_Hint");
        AutomationProperties.SetName(_image, UiStrings.Get("Snip_PageImage_Name"));
        AutomationProperties.SetName(_overlay, UiStrings.Get("Snip_Surface_Name"));
        AutomationProperties.SetName(_wholePage, UiStrings.Get("Snip_WholePage_Name"));

        _prev.Click += async (_, _) => await ShowAsync(_index - 1);
        _next.Click += async (_, _) => await ShowAsync(_index + 1);
        _wholePage.Click += (_, _) => SetSelection(new Rect(0, 0, 1, 1));
        _copy.Click += async (_, _) => await CopyAsync();
        _save.Click += async (_, _) => await SaveAsync();

        _overlay.MouseLeftButtonDown += Overlay_Down;
        _overlay.MouseMove += Overlay_Move;
        _overlay.MouseLeftButtonUp += Overlay_Up;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        _overlay.Children.Add(_selection);
        _pageHost.Children.Add(_image);
        _pageHost.Children.Add(_overlay);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_wholePage);
        actions.Children.Add(_copy);
        actions.Children.Add(_save);

        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(_prev);
        nav.Children.Add(_pageLabel);
        nav.Children.Add(_next);

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = true };
        DockPanel.SetDock(nav, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        bar.Children.Add(nav);
        bar.Children.Add(actions);
        bar.Children.Add(_hint);

        var scroll = new ScrollViewer
        {
            Background = Brushes.Gainsboro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _pageHost,
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(scroll);
        Content = root;

        Loaded += async (_, _) => await ShowAsync(_index);
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task ShowAsync(int index)
    {
        if (index < 0 || index >= _pageCount)
        {
            return;
        }

        _index = index;
        _pageLabel.Text = UiStrings.Format("Print_Preview_Page", index + 1, _pageCount);
        _prev.IsEnabled = index > 0;
        _next.IsEnabled = index < _pageCount - 1;
        ClearSelection();

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            PrintPage page = await _render(index, PreviewDpi, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // Shown at 100% (pt to DIP at 96/72) from a 144 DPI render.
            _pageHost.Width = _image.Width = _overlay.Width = page.Size.WidthPt * 96.0 / 72.0;
            _pageHost.Height = _image.Height = _overlay.Height = page.Size.HeightPt * 96.0 / 72.0;
            _image.Source = Decode(page.PngBytes);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("Snip_Failed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_overlay);
        _dragging = true;
        _overlay.CaptureMouse();
        Canvas.SetLeft(_selection, _dragStart.X);
        Canvas.SetTop(_selection, _dragStart.Y);
        _selection.Width = 0;
        _selection.Height = 0;
        _selection.Visibility = Visibility.Visible;
    }

    private void Overlay_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Rect r = Clamp(new Rect(_dragStart, e.GetPosition(_overlay)));
        Canvas.SetLeft(_selection, r.X);
        Canvas.SetTop(_selection, r.Y);
        _selection.Width = r.Width;
        _selection.Height = r.Height;
    }

    private void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _overlay.ReleaseMouseCapture();
        Rect r = Clamp(new Rect(_dragStart, e.GetPosition(_overlay)));
        if (r.Width < MinSelectionDip || r.Height < MinSelectionDip)
        {
            ClearSelection();
            return;
        }

        SetSelection(new Rect(r.X / _overlay.Width, r.Y / _overlay.Height, r.Width / _overlay.Width, r.Height / _overlay.Height));
    }

    private Rect Clamp(Rect r) => Rect.Intersect(r, new Rect(0, 0, _overlay.Width, _overlay.Height)) is { IsEmpty: false } c ? c : new Rect(0, 0, 0, 0);

    private void SetSelection(Rect fraction)
    {
        _fraction = fraction;
        Canvas.SetLeft(_selection, fraction.X * _overlay.Width);
        Canvas.SetTop(_selection, fraction.Y * _overlay.Height);
        _selection.Width = fraction.Width * _overlay.Width;
        _selection.Height = fraction.Height * _overlay.Height;
        _selection.Visibility = Visibility.Visible;
        _copy.IsEnabled = _save.IsEnabled = true;
        _hint.Text = UiStrings.Get("Snip_Selected");
    }

    private void ClearSelection()
    {
        _fraction = Rect.Empty;
        _selection.Visibility = Visibility.Collapsed;
        _copy.IsEnabled = _save.IsEnabled = false;
        _hint.Text = UiStrings.Get("Snip_Hint");
    }

    /// <summary>Renders the page at 300 DPI and cuts out the chosen region.</summary>
    private async Task<BitmapSource?> CutAsync()
    {
        if (_fraction.IsEmpty)
        {
            return null;
        }

        try
        {
            PrintPage page = await _render(_index, SnipDpi, CancellationToken.None);
            BitmapSource full = Decode(page.PngBytes);
            int x = Math.Clamp((int)Math.Round(_fraction.X * full.PixelWidth), 0, full.PixelWidth - 1);
            int y = Math.Clamp((int)Math.Round(_fraction.Y * full.PixelHeight), 0, full.PixelHeight - 1);
            int w = Math.Clamp((int)Math.Round(_fraction.Width * full.PixelWidth), 1, full.PixelWidth - x);
            int h = Math.Clamp((int)Math.Round(_fraction.Height * full.PixelHeight), 1, full.PixelHeight - y);
            var cut = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
            cut.Freeze();
            return cut;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("Snip_Failed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private async Task CopyAsync()
    {
        BitmapSource? cut = await CutAsync();
        if (cut is null)
        {
            return;
        }

        try
        {
            Clipboard.SetImage(cut);
            _hint.Text = UiStrings.Format("Snip_Copied", cut.PixelWidth, cut.PixelHeight);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process for a moment.
            MessageBox.Show(this, UiStrings.Format("Snip_Failed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_documentName)}-page{_index + 1}-snip.png",
            AddExtension = true,
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BitmapSource? cut = await CutAsync();
        if (cut is null)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cut));
            await using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            _hint.Text = UiStrings.Format("Snip_Saved", System.IO.Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("Snip_Failed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapImage Decode(byte[] png)
    {
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        bitmap.Freeze();
        return bitmap;
    }
}
