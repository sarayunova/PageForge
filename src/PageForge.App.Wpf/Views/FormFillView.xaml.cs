// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PageForge.App.Wpf.ViewModels;
using PageForge.Core.Pdf;
using PageForge.App.Wpf.Resources;

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
            AutomationProperties.SetName(PageImage, UiStrings.Format("FormFill_PageImage_Name", pageIndex + 1));

            PdfPageRegion region = _vm.Core.PageSizes[Math.Min(pageIndex, _vm.Core.PageCount - 1)];
            _pixelW = region.WidthPt * _scale;
            _pixelH = region.HeightPt * _scale;

            PageHost.Width = _pixelW;
            PageHost.Height = _pixelH;
            Overlay.Width = _pixelW;
            Overlay.Height = _pixelH;

            // Assigned AFTER the render. It used to be assigned before, when
            // Bitmap is still null, and nothing reassigned it afterwards - so this
            // surface showed a blank sheet and the field outlines floated over
            // white. Same silent shape as the original blank-viewer bug: no
            // exception, no log, just nothing on screen.
            var page = new PageImageViewModel(_vm.Core, pageIndex, _vm.RenderDpi);
            await page.RenderAsync().ConfigureAwait(true);
            PageImage.Source = page.Bitmap;

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

    /// <summary>The field outlines drawn over the page, bound by the XAML through a
    /// RelativeSource on the UserControl. Geometry only; the editable controls are
    /// in the side panel.</summary>
    public ObservableCollection<FormFieldBoxViewModel> FieldBoxes { get; } = new();

    /// <summary>The editable cards in the side panel, bound by the XAML. Each one
    /// owns a value and a command; the view no longer builds controls for them.</summary>
    public ObservableCollection<FormFieldCardViewModel> FieldCards { get; } = new();

    /// <summary>What the panel says when the page has no fields. A property rather
    /// than a label added to the panel, so its visibility can follow the card count
    /// and no rebuild path can leave a stale one behind.</summary>
    public string EmptyStateText
    {
        get => (string)GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    public static readonly DependencyProperty EmptyStateTextProperty =
        DependencyProperty.Register(
            nameof(EmptyStateText),
            typeof(string),
            typeof(FormFillView),
            new PropertyMetadata(UiStrings.Get("FormFill_EmptyState")));

    private void Rebuild(IReadOnlyList<PdfFormField> fields)
    {
        FieldBoxes.Clear();
        FieldCards.Clear();

        // The empty-state text is a bound property whose visibility follows the
        // card count, so there is no longer a label to add here and remember to
        // remove. It used to be appended to the panel, which meant every path
        // that rebuilt the list had to clear it first or it would stack up.
        EmptyStateText = _justFlattened
            ? UiStrings.Get("FormFill_EmptyStateFlattened")
            : UiStrings.Get("FormFill_EmptyState");

        if (fields.Count == 0)
        {
            Overlay.Children.Clear();
            Hint(_justFlattened
                ? UiStrings.Get("FormFill_NoFieldsAfterFlatten")
                : UiStrings.Format("FormFill_NoFields", (_vm?.Core.CurrentPage ?? 0) + 1));
            return;
        }

        Hint(UiStrings.Format("FormFill_FillTheFields", fields.Count));

        foreach (PdfFormField field in fields)
        {
            FieldBoxes.Add(new FormFieldBoxViewModel(field, _vm?.RenderDpi ?? 96.0));
            FieldCards.Add(new FormFieldCardViewModel(field, SetFieldAsync));
        }
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
            Hint(UiStrings.Format("FormFill_SetField", fieldId));
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiStrings.Format("Error_CouldNotFillField", fieldId, ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            UiStrings.Get("FormFill_ConfirmFlatten"),
            UiStrings.Get("Common_AppTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await _vm.FlattenFormAsync().ConfigureAwait(true);
            _justFlattened = true;
            Hint(UiStrings.Get("FormFill_Flattened"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiStrings.Format("Error_FlattenFailed", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private void Hint(string text) => HintText.Text = text;

    private async void NewField_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string? name = PromptFieldName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            // Place the new field near the top-left of the current page, sized and
            // positioned in PDF points from the on-screen pixel rect.
            double scale = _scale > 0 ? _scale : 1.0;
            double wPt = 160.0;
            double hPt = 22.0;
            double leftPt = _pixelW / scale * 0.06;
            double topPt = _pixelH / scale * 0.06;

            var spec = new FormFieldSpec(
                FormFieldKind.Text,
                name.Trim(),
                new PdfRect(leftPt, topPt, leftPt + wPt, topPt + hPt),
                Flags: FormFieldFlags.Required);

            await _vm.CreateFormFieldAsync(spec).ConfigureAwait(true);
            Hint(UiStrings.Format("FormFill_CreatedField", name.Trim()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiStrings.Format("Error_CouldNotCreateTheField", ex.Message), UiStrings.Get("Common_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Refresh();
        }
    }

    private static string? PromptFieldName()
    {
        var window = new Window
        {
            Title = "New text field",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        }.Themed(Window.BackgroundProperty, "SurfaceAppBrush");

        var grid = new StackPanel { Margin = new Thickness(12) };
        grid.Children.Add(new TextBlock
        {
            Text = "Field name (shown on this page, e.g. TaxRef):",
            Margin = new Thickness(0, 0, 0, 6),
        }.Themed(TextBlock.ForegroundProperty, "ContentBrush"));
        var nameBox = new TextBox { Width = 330 };
        AutomationProperties.SetName(nameBox, UiStrings.Get("FormFill_FieldName_Name"));
        grid.Children.Add(nameBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        grid.Children.Add(buttons);

        window.Content = grid;
        window.Loaded += (_, _) => { nameBox.Focus(); };

        string? result = null;
        ok.Click += (_, _) => { result = nameBox.Text; window.DialogResult = true; };

        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Moves keyboard focus onto the page area so the skip-navigation
    /// bypass (WCAG 2.4.1) can land here when form-fill mode is active.</summary>
    public void FocusSurface() => Keyboard.Focus(Overlay);
}
