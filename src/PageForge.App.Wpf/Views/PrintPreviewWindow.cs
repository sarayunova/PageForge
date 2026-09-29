using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PageForge.App.Wpf.Resources;
using PageForge.App.Wpf.ViewModels;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Print preview (FR-VIEW-05): one page at a time, rendered on demand at screen
/// resolution, with All / Current page / page-range selection and a Print button.
/// The Windows print dialog's own preview pane is empty for apps that print
/// through the WPF dialog, so this is the real preview. Only one low-resolution
/// page is ever held, which keeps a thousand-page document as cheap as a
/// one-page one. Fully offline.
/// </summary>
internal sealed class PrintPreviewWindow : Window
{
    private readonly int _pageCount;
    private readonly Func<int, CancellationToken, Task<PrintPage>> _renderPreview;
    private readonly Func<int, int, bool> _print;
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 90, TextAlignment = TextAlignment.Center };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
    private readonly Button _prev = new() { Content = "◀", Padding = new Thickness(10, 3, 10, 3) };
    private readonly Button _next = new() { Content = "▶", Padding = new Thickness(10, 3, 10, 3) };
    private readonly RadioButton _all = new() { Content = UiStrings.Get("Print_Range_All"), IsChecked = true, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _current = new() { Content = UiStrings.Get("Print_Range_Current"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RadioButton _range = new() { Content = UiStrings.Get("Print_Range_Pages"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _rangeText = new() { Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "7-10" };
    private int _index;
    private CancellationTokenSource? _cts;

    public PrintPreviewWindow(
        int pageCount,
        int startIndex,
        string documentName,
        Func<int, CancellationToken, Task<PrintPage>> renderPreview,
        Func<int, int, bool> print)
    {
        _pageCount = pageCount;
        _renderPreview = renderPreview;
        _print = print;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, pageCount - 1));

        Title = UiStrings.Format("Print_Preview_Title", documentName);
        Width = 900;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var printButton = new Button { Content = UiStrings.Get("Print_Button"), Padding = new Thickness(18, 3, 18, 3), IsDefault = true, Margin = new Thickness(16, 0, 0, 0) };
        printButton.Click += (_, _) => PrintClicked();
        _prev.Click += async (_, _) => await ShowAsync(_index - 1);
        _next.Click += async (_, _) => await ShowAsync(_index + 1);
        _rangeText.GotFocus += (_, _) => _range.IsChecked = true;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        bar.Children.Add(_prev);
        bar.Children.Add(_pageLabel);
        bar.Children.Add(_next);
        bar.Children.Add(new Separator { Margin = new Thickness(12, 0, 12, 0) });
        bar.Children.Add(_all);
        bar.Children.Add(_current);
        bar.Children.Add(_range);
        bar.Children.Add(_rangeText);
        bar.Children.Add(printButton);

        var scroll = new ScrollViewer
        {
            Background = Brushes.Gainsboro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _image,
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

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            PrintPage page = await _renderPreview(index, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(page.PngBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("Error_PrintFailed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrintClicked()
    {
        int first = 0;
        int last = _pageCount - 1;
        if (_current.IsChecked == true)
        {
            first = last = _index;
        }
        else if (_range.IsChecked == true && !TryParseRange(_rangeText.Text, _pageCount, out first, out last))
        {
            MessageBox.Show(this, UiStrings.Format("Print_Range_Invalid", _pageCount), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_print(first, last))
        {
            Close();
        }
    }

    /// <summary>Parses "7" or "7-10" (1-based, inclusive) into a 0-based range
    /// within the document; false when malformed or out of range.</summary>
    internal static bool TryParseRange(string text, int pageCount, out int first, out int last)
    {
        first = last = 0;
        string[] parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out int a))
        {
            return false;
        }

        int b = a;
        if (parts.Length == 2 && !int.TryParse(parts[1], out b))
        {
            return false;
        }

        if (b < a)
        {
            (a, b) = (b, a);
        }

        if (a < 1 || b > pageCount)
        {
            return false;
        }

        first = a - 1;
        last = b - 1;
        return true;
    }
}
