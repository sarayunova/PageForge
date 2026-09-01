// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PageForge.App.Wpf.ViewModels;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Views;

/// <summary>
/// Interactive AcroForm fill surface (FR-FORM-01): the current page rendered at
/// the viewer DPI with a dashed box over every form field, plus a right-hand
/// panel listing the page's fields. Text/combo/list fields get a text box,
/// checkbox/radio fields get a check box; setting a value calls
/// <see cref="DocumentTabViewModel.SetFormFieldValueAsync"/> and re-renders so the
/// value shows on the page immediately. The "Flatten form…" button calls
/// <see cref="DocumentTabViewModel.FlattenFormAsync"/> to bake every field value
/// into static content. Field fills are applied directly (not on the undo stack),
/// matching the idempotent set-a-value semantics of the native primitive.
/// </summary>
public partial class FormFillView : UserControl
{
    private DocumentTabViewModel? _vm;
    private double _scale = 1.0;
    private double _pixelW;
    private double _pixelH;
    private bool _busy;
    private bool _justFlattened;

    public FormFillView()
    {
        InitializeComponent();
    }

    /// <summary>Binds this surface to a document tab and (re)loads the current page.</summary>
    public void SetContext(DocumentTabViewModel vm)
    {
        _vm = vm;
        _justFlattened = false;
        Refresh();
    }

    /// <summary>Re-renders the current page and re-lists its form fields. Call after
    /// navigation, zoom, or a fill/flatten while this surface is active.</summary>
    public async void Refresh()
    {
        if (_vm is null || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            int pageIndex = _vm.Core.CurrentPage;
            _scale = _vm.RenderDpi / 72.0;

            PdfPageRegion region = _vm.Core.PageSizes[Math.Min(pageIndex, _vm.Core.PageCount - 1)];
            _pixelW = region.WidthPt * _scale;
            _pixelH = region.HeightPt * _scale;

            PageHost.Width = _pixelW;
            PageHost.Height = _pixelH;
            Overlay.Width = _pixelW;
            Overlay.Height = _pixelH;

            var page = new PageImageViewModel(_vm.Core, pageIndex, _vm.RenderDpi);
            PageImage.Source = page.Bitmap;
            await page.RenderAsync().ConfigureAwait(true);

            IReadOnlyList<PdfFormField> fields = await _vm.ListFormFieldsAsync().ConfigureAwait(true);
            Rebuild(fields);
        }
        catch (Exception ex)
        {
            Hint(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Rebuild(IReadOnlyList<PdfFormField> fields)
    {
        Overlay.Children.Clear();
        FieldsPanel.Children.Clear();

        if (fields.Count == 0)
        {
            Overlay.Children.Clear();
            var none = new TextBlock
            {
                Text = _justFlattened
                    ? "This page has no form fields left — the form has been flattened."
                    : "This page has no fillable form fields.",
                Foreground = Brushes.Gray,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            };
            FieldsPanel.Children.Add(none);
            Hint(_justFlattened
                ? "No fields left on this page after flattening."
                : $"No fillable form fields on page {(_vm?.Core.CurrentPage ?? 0) + 1}.");
            return;
        }

        Hint($"Fill the fields below; values appear on the page at once. {fields.Count} field(s).");

        foreach (PdfFormField field in fields)
        {
            // Dashed box over the field's rect (visual reference only, not interactive).
            var border = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(12, 0x2b, 0x8c, 0xff)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xcc, 0x77, 0x77, 0x77)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Width = (field.Bounds.X1 - field.Bounds.X0) * _scale,
                Height = (field.Bounds.Y1 - field.Bounds.Y0) * _scale,
            };
            Canvas.SetLeft(border, field.Bounds.X0 * _scale);
            Canvas.SetTop(border, _pixelH - field.Bounds.Y1 * _scale);
            Overlay.Children.Add(border);

            FieldsPanel.Children.Add(BuildFieldRow(field));
        }
    }

    private UIElement BuildFieldRow(PdfFormField field)
    {
        bool isCheckable = field.Kind is FormFieldKind.Checkbox or FormFieldKind.Radio;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4),
            Padding = new Thickness(6),
        };

        var stack = new StackPanel();
        var label = new TextBlock
        {
            Text = field.Label,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(label);

        var kindTag = new TextBlock
        {
            Text = field.Kind.ToString(),
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4),
        };
        stack.Children.Add(kindTag);

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (isCheckable)
        {
            bool isChecked = string.Equals(field.Value, "Yes", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(field.Value, "On", StringComparison.OrdinalIgnoreCase);
            var check = new CheckBox
            {
                Content = "Checked",
                IsChecked = isChecked,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            check.Checked += async (_, _) => await SetFieldAsync(field.Id, "Yes");
            check.Unchecked += async (_, _) => await SetFieldAsync(field.Id, "Off");
            row.Children.Add(check);
        }
        else
        {
            var box = new TextBox
            {
                Text = field.Value,
                Width = 150,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var set = new Button { Content = "Set", Padding = new Thickness(8, 2, 8, 2) };
            set.Click += async (_, _) => await SetFieldAsync(field.Id, box.Text);
            row.Children.Add(box);
            row.Children.Add(set);
        }

        stack.Children.Add(row);
        card.Child = stack;
        return card;
    }

    private async System.Threading.Tasks.Task SetFieldAsync(string fieldId, string value)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            await _vm.SetFormFieldValueAsync(fieldId, value).ConfigureAwait(true);
            Hint($"Set field {fieldId}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not fill field {fieldId}:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Refresh();
        }
    }

    private async void Flatten_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            "Flatten the form? Every field value is baked into static page content and the fields stop being interactive.",
            "PageForge", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _vm.FlattenFormAsync().ConfigureAwait(true);
            _justFlattened = true;
            Hint("Form flattened — fields are now static page content.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Flatten failed:\n{ex.Message}", "PageForge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private void Hint(string text) => HintText.Text = text;
}
