using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    // Keyboard selection: a crosshair the arrow keys move over the page. Enter
    // starts a selection at the crosshair and a second Enter finishes it.
    private readonly Ellipse _keyCursorMark = new()
    {
        Width = 10,
        Height = 10,
        Stroke = Brushes.Black,
        StrokeThickness = 1.5,
        Fill = Brushes.White,
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

    // Annotations are vector shapes on their own layer (page DIP coordinates),
    // drawn above the page and below the interaction overlay. On export the layer
    // is painted over the 300 DPI crop, so marks stay crisp at print resolution.
    private enum Tool { Select, Pen, Arrow, Box, Highlight, Text }

    private static readonly (string Name, Color Color)[] Palette =
    {
        (UiStrings.Get("Snip_Color_Red"), Color.FromRgb(0xd9, 0x2d, 0x20)),
        (UiStrings.Get("Snip_Color_Blue"), Color.FromRgb(0x1a, 0x56, 0xdb)),
        (UiStrings.Get("Snip_Color_Green"), Color.FromRgb(0x1e, 0x8e, 0x3e)),
        (UiStrings.Get("Snip_Color_Yellow"), Color.FromRgb(0xf5, 0xc4, 0x00)),
        (UiStrings.Get("Snip_Color_Black"), Color.FromRgb(0x20, 0x20, 0x20)),
    };

    private readonly Canvas _ink = new() { IsHitTestVisible = false };
    private readonly List<Button> _swatches = new();
    private Tool _tool = Tool.Select;
    private Color _color = Palette[0].Color;
    private Polyline? _stroke;
    private System.Windows.Shapes.Path? _arrow;
    private Rectangle? _box;
    private TextBox? _textEditor;

    private int _index;
    private Point _dragStart;
    private bool _dragging;
    private Point _keyCursor;
    private Point? _keyAnchor;
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
        _overlay.Children.Add(_keyCursorMark);
        _pageHost.Children.Add(_image);
        _pageHost.Children.Add(_ink);
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

        // The page sits in a ScrollViewer, which is the element that takes keyboard
        // focus and is exposed to screen readers (a Canvas is neither). The keys are
        // read here, before the ScrollViewer would use the arrows to scroll.
        var scroll = new ScrollViewer
        {
            Background = Brushes.Gainsboro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _pageHost,
        };
        AutomationProperties.SetName(scroll, UiStrings.Get("Snip_Surface_Name"));
        AutomationProperties.SetHelpText(scroll, UiStrings.Get("Snip_Surface_Help"));
        scroll.PreviewKeyDown += Page_KeyDown;
        scroll.GotKeyboardFocus += (_, _) => _keyCursorMark.Visibility = Visibility.Visible;
        scroll.LostKeyboardFocus += (_, _) => _keyCursorMark.Visibility = Visibility.Collapsed;

        var tools = BuildToolRow();

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(tools);
        root.Children.Add(scroll);
        Content = root;

        Loaded += async (_, _) => await ShowAsync(_index);
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>Marks are drawn by hand and cannot be recovered once cleared.</summary>
    private bool ConfirmDiscardMarks(string message) => MessageBox.Show(
        this,
        message,
        UiStrings.Get("Common_AppTitle"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task ShowAsync(int index)
    {
        if (index < 0 || index >= _pageCount)
        {
            return;
        }

        CommitText();
        if (index != _index && _ink.Children.Count > 0 && !ConfirmDiscardMarks(UiStrings.Get("Snip_ConfirmPageChange")))
        {
            return;
        }

        _index = index;
        _pageLabel.Text = UiStrings.Format("Print_Preview_Page", index + 1, _pageCount);
        _prev.IsEnabled = index > 0;
        _next.IsEnabled = index < _pageCount - 1;
        ClearSelection();
        _ink.Children.Clear();

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
        CommitText();
        _keyAnchor = null;
        if (_tool != Tool.Select)
        {
            BeginMark(e.GetPosition(_overlay));
            return;
        }

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
        if (_tool != Tool.Select)
        {
            if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                ExtendMark(e.GetPosition(_overlay));
            }

            return;
        }

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
        if (_tool != Tool.Select)
        {
            EndMark(e.GetPosition(_overlay));
            return;
        }

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

    private StackPanel BuildToolRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
        var toggles = new List<(ToggleButton Button, Tool Tool)>();

        void AddTool(string label, Tool tool)
        {
            var toggle = new ToggleButton { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0), IsChecked = tool == _tool };
            toggle.Click += (_, _) =>
            {
                CommitText();
                _tool = tool;
                foreach ((ToggleButton b, Tool t) in toggles)
                {
                    b.IsChecked = t == tool;
                }

                _overlay.Cursor = tool switch
                {
                    Tool.Text => Cursors.IBeam,
                    Tool.Select => Cursors.Cross,
                    _ => Cursors.Pen,
                };
            };
            toggles.Add((toggle, tool));
            row.Children.Add(toggle);
        }

        AddTool(UiStrings.Get("Snip_Tool_Select"), Tool.Select);
        AddTool(UiStrings.Get("Snip_Tool_Pen"), Tool.Pen);
        AddTool(UiStrings.Get("Snip_Tool_Arrow"), Tool.Arrow);
        AddTool(UiStrings.Get("Snip_Tool_Box"), Tool.Box);
        AddTool(UiStrings.Get("Snip_Tool_Highlight"), Tool.Highlight);
        AddTool(UiStrings.Get("Snip_Tool_Text"), Tool.Text);
        row.Children.Add(new Separator { Margin = new Thickness(8, 0, 8, 0) });

        foreach ((string name, Color color) in Palette)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(color == _color ? 3 : 1),
                ToolTip = name,
            };
            AutomationProperties.SetName(swatch, name);
            swatch.Click += (_, _) =>
            {
                _color = color;
                foreach (Button s in _swatches)
                {
                    s.BorderThickness = new Thickness(ReferenceEquals(s, swatch) ? 3 : 1);
                }
            };
            _swatches.Add(swatch);
            row.Children.Add(swatch);
        }

        row.Children.Add(new Separator { Margin = new Thickness(8, 0, 8, 0) });
        var undo = new Button { Content = UiStrings.Get("Snip_Undo"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0) };
        undo.Click += (_, _) =>
        {
            CommitText();
            if (_ink.Children.Count > 0)
            {
                _ink.Children.RemoveAt(_ink.Children.Count - 1);
            }
        };
        var clear = new Button { Content = UiStrings.Get("Snip_ClearMarks"), Padding = new Thickness(10, 3, 10, 3) };
        clear.Click += (_, _) =>
        {
            CommitText();
            if (_ink.Children.Count > 0 && !ConfirmDiscardMarks(UiStrings.Get("Snip_ConfirmClear")))
            {
                return;
            }

            _ink.Children.Clear();
        };
        row.Children.Add(undo);
        row.Children.Add(clear);
        return row;
    }

    private Brush Stroke(byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, _color.R, _color.G, _color.B));

    private void BeginMark(Point p)
    {
        if (_tool == Tool.Text)
        {
            var editor = new TextBox
            {
                MinWidth = 80,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Stroke(),
                Background = Brushes.White,
                BorderBrush = Stroke(),
            };
            AutomationProperties.SetName(editor, UiStrings.Get("Snip_TextBox_Name"));
            Canvas.SetLeft(editor, p.X);
            Canvas.SetTop(editor, p.Y);
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitText();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    editor.Text = string.Empty;
                    CommitText();
                    e.Handled = true;
                }
            };
            editor.LostKeyboardFocus += (_, _) => CommitText();
            _textEditor = editor;
            _overlay.Children.Add(editor);
            Dispatcher.BeginInvoke(new Action(() => editor.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        _dragStart = p;
        _dragging = true;
        _overlay.CaptureMouse();
        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Highlight:
                bool marker = _tool == Tool.Highlight;
                _stroke = new Polyline
                {
                    Stroke = Stroke(marker ? (byte)110 : (byte)255),
                    StrokeThickness = marker ? 16 : 3,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _stroke.Points.Add(p);
                _ink.Children.Add(_stroke);
                break;
            case Tool.Arrow:
                _arrow = new System.Windows.Shapes.Path { Stroke = Stroke(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _ink.Children.Add(_arrow);
                break;
            case Tool.Box:
                _box = new Rectangle { Stroke = Stroke(), StrokeThickness = 3 };
                Canvas.SetLeft(_box, p.X);
                Canvas.SetTop(_box, p.Y);
                _ink.Children.Add(_box);
                break;
        }
    }

    private void ExtendMark(Point raw)
    {
        Point p = new(Math.Clamp(raw.X, 0, _overlay.Width), Math.Clamp(raw.Y, 0, _overlay.Height));
        if (_stroke is not null)
        {
            _stroke.Points.Add(p);
        }
        else if (_arrow is not null)
        {
            _arrow.Data = ArrowGeometry(_dragStart, p);
        }
        else if (_box is not null)
        {
            var r = new Rect(_dragStart, p);
            Canvas.SetLeft(_box, r.X);
            Canvas.SetTop(_box, r.Y);
            _box.Width = r.Width;
            _box.Height = r.Height;
        }
    }

    private void EndMark(Point raw)
    {
        if (!_dragging)
        {
            return;
        }

        ExtendMark(raw);
        _dragging = false;
        _overlay.ReleaseMouseCapture();

        // A click with no drag leaves an invisible mark; drop it so Undo does not
        // appear to do nothing.
        UIElement? last = _stroke ?? (UIElement?)_arrow ?? _box;
        Point end = new(Math.Clamp(raw.X, 0, _overlay.Width), Math.Clamp(raw.Y, 0, _overlay.Height));
        bool tiny = (end - _dragStart).Length < 3;
        if (last is not null && tiny && last is not Polyline)
        {
            _ink.Children.Remove(last);
        }

        _stroke = null;
        _arrow = null;
        _box = null;
    }

    private static Geometry ArrowGeometry(Point from, Point to)
    {
        Vector v = to - from;
        var group = new StreamGeometry();
        using (StreamGeometryContext ctx = group.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(to, true, true);
            if (v.Length > 1)
            {
                v.Normalize();
                const double head = 16;
                foreach (double angle in new[] { 155.0, -155.0 })
                {
                    double rad = angle * Math.PI / 180.0;
                    var rotated = new Vector(
                        v.X * Math.Cos(rad) - v.Y * Math.Sin(rad),
                        v.X * Math.Sin(rad) + v.Y * Math.Cos(rad));
                    ctx.BeginFigure(to, false, false);
                    ctx.LineTo(to + rotated * head, true, true);
                }
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>Turns the text being typed into a permanent mark (or drops it
    /// when empty). Safe to call when nothing is being edited.</summary>
    private void CommitText()
    {
        TextBox? editor = _textEditor;
        if (editor is null)
        {
            return;
        }

        _textEditor = null;
        _overlay.Children.Remove(editor);
        if (string.IsNullOrWhiteSpace(editor.Text))
        {
            return;
        }

        var label = new TextBlock
        {
            Text = editor.Text,
            FontSize = editor.FontSize,
            FontWeight = editor.FontWeight,
            Foreground = editor.Foreground,
        };
        Canvas.SetLeft(label, Canvas.GetLeft(editor));
        Canvas.SetTop(label, Canvas.GetTop(editor));
        _ink.Children.Add(label);
    }

    /// <summary>Paints the annotation layer over the 300 DPI crop, at the crop's
    /// own pixel resolution.</summary>
    private BitmapSource Annotate(BitmapSource cut)
    {
        if (_ink.Children.Count == 0)
        {
            return cut;
        }

        var sel = new Rect(
            _fraction.X * _overlay.Width, _fraction.Y * _overlay.Height,
            _fraction.Width * _overlay.Width, _fraction.Height * _overlay.Height);
        var target = new Rect(0, 0, sel.Width, sel.Height);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(cut, target);
            dc.DrawRectangle(
                new VisualBrush(_ink) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = sel, Stretch = Stretch.Fill },
                null,
                target);
        }

        var rendered = new RenderTargetBitmap(
            cut.PixelWidth,
            cut.PixelHeight,
            96.0 * cut.PixelWidth / sel.Width,
            96.0 * cut.PixelHeight / sel.Height,
            PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    /// <summary>Arrow keys move the crosshair (Ctrl for bigger steps); Enter or Space
    /// starts a selection at it and a second press finishes the selection.</summary>
    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (_tool != Tool.Select)
        {
            return;
        }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 16 : 2;
        switch (e.Key)
        {
            case Key.Left:
                MoveKeyCursor(-step, 0);
                break;
            case Key.Right:
                MoveKeyCursor(step, 0);
                break;
            case Key.Up:
                MoveKeyCursor(0, -step);
                break;
            case Key.Down:
                MoveKeyCursor(0, step);
                break;
            case Key.Enter:
            case Key.Space:
                ToggleKeyboardSelection();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveKeyCursor(double dx, double dy)
    {
        _keyCursor = new Point(
            Math.Clamp(_keyCursor.X + dx, 0, _overlay.Width),
            Math.Clamp(_keyCursor.Y + dy, 0, _overlay.Height));
        Canvas.SetLeft(_keyCursorMark, _keyCursor.X - _keyCursorMark.Width / 2);
        Canvas.SetTop(_keyCursorMark, _keyCursor.Y - _keyCursorMark.Height / 2);

        if (_keyAnchor is Point anchor)
        {
            ShowSelectionRect(Clamp(new Rect(anchor, _keyCursor)));
        }
    }

    private void ToggleKeyboardSelection()
    {
        if (_keyAnchor is not Point anchor)
        {
            _keyAnchor = _keyCursor;
            ShowSelectionRect(new Rect(_keyCursor, new Size(0, 0)));
            _hint.Text = UiStrings.Get("Snip_Selecting");
            return;
        }

        _keyAnchor = null;
        Rect r = Clamp(new Rect(anchor, _keyCursor));
        if (r.Width < MinSelectionDip || r.Height < MinSelectionDip)
        {
            ClearSelection();
            return;
        }

        SetSelection(new Rect(r.X / _overlay.Width, r.Y / _overlay.Height, r.Width / _overlay.Width, r.Height / _overlay.Height));
    }

    private void ShowSelectionRect(Rect r)
    {
        Canvas.SetLeft(_selection, r.X);
        Canvas.SetTop(_selection, r.Y);
        _selection.Width = r.Width;
        _selection.Height = r.Height;
        _selection.Visibility = Visibility.Visible;
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
            CommitText();
            return Annotate(cut);
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
