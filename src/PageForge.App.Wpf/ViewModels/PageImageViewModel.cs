// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Media.Imaging;
using PageForge.Core.View;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// Lazy per-page image binding (FR-VIEW-01). The page is rendered only when the
/// view first requests it via <see cref="RenderAsync"/>. The resulting
/// <see cref="Bitmap"/> is a frozen <see cref="BitmapSource"/> and the property
/// raises change so an <c>Image.Source</c> binding updates when rendering
/// completes on the UI thread.
/// </summary>
public sealed class PageImageViewModel : ObservableObject
{
    private readonly DocumentViewModel _doc;
    private readonly int _pageIndex;
    private BitmapSource? _bitmap;
    private bool _isRendering;
    private double _renderDpi;

    public PageImageViewModel(DocumentViewModel doc, int pageIndex, double renderDpi = 96.0)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _renderDpi = renderDpi;
    }

    public int PageIndex => _pageIndex;

    public int DisplayNumber => _pageIndex + 1;

    /// <summary>The DPI this page renders at (1x zoom on a 96-DPI screen is 96).</summary>
    public double RenderDpi
    {
        get => _renderDpi;
        set
        {
            if (Math.Abs(_renderDpi - value) > 1e-6)
            {
                // A change in target DPI implies the cached bitmap is stale.
                _renderDpi = value;
                Bitmap = null;
            }
        }
    }

    public BitmapSource? Bitmap
    {
        get => _bitmap;
        private set
        {
            if (!ReferenceEquals(_bitmap, value))
            {
                _bitmap = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public bool IsLoaded => _bitmap != null;

    public bool IsRendering
    {
        get => _isRendering;
        private set
        {
            if (_isRendering != value)
            {
                _isRendering = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Drops the cached bitmap so the page renders again at RenderDpi.</summary>
    public void Clear() => Bitmap = null;

    /// <summary>
    /// Renders this page to a frozen bitmap. The caller runs this off the UI
    /// thread and the result is applied back on the dispatcher, so binding
    /// refresh is thread-safe. No-op once loaded at the current RenderDpi.
    /// </summary>
    public async Task RenderAsync(CancellationToken ct = default)
    {
        if (_bitmap != null || _isRendering || ct.IsCancellationRequested)
        {
            return;
        }

        IsRendering = true;
        try
        {
            PageForge.Core.Pdf.RenderedPdfPage render = await _doc.RenderAsync(_pageIndex, (float)RenderDpi, ct).ConfigureAwait(false);
            var memory = new System.IO.MemoryStream(render.PngBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = memory;
            bmp.EndInit();
            bmp.Freeze();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Bitmap = bmp;
            }, System.Windows.Threading.DispatcherPriority.DataBind);
        }
        catch (OperationCanceledException)
        {
            // A superseded render was cancelled; leave the slot empty for a retry.
        }
        finally
        {
            IsRendering = false;
        }
    }
}

/// <summary>
/// Attached behavior that renders a page's image as soon as the Image is
/// realized on screen (used by the virtualized page/thumbnail lists). Attach
/// with <c>behaviors:PageImageBehavior.RenderOnLoad="True"</c> and set the
/// DataContext to a <see cref="PageImageViewModel"/>.
/// </summary>
public static class PageImageBehavior
{
    public static readonly DependencyProperty RenderOnLoadProperty =
        DependencyProperty.RegisterAttached(
            "RenderOnLoad",
            typeof(bool),
            typeof(PageImageBehavior),
            new PropertyMetadata(false, OnRenderOnLoadChanged));

    public static bool GetRenderOnLoad(DependencyObject obj) => (bool)obj.GetValue(RenderOnLoadProperty);

    public static void SetRenderOnLoad(DependencyObject obj, bool value) => obj.SetValue(RenderOnLoadProperty, value);

    private static void OnRenderOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            element.Loaded += async (_, _) =>
            {
                if (element.DataContext is PageImageViewModel page)
                {
                    await page.RenderAsync();
                }
            };
        }
    }
}
