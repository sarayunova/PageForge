// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PageForge.Core.View;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// Lazy per-page image binding (FR-VIEW-01). The page is rendered only when the
/// view first requests it via <see cref="RenderAsync"/>. The resulting
/// <see cref="Bitmap"/> is a frozen <see cref="BitmapSource"/> and the property
/// raises change so an <c>Image.Source</c> binding updates when rendering
/// completes on the UI thread.
/// </summary>
/// <remarks>
/// The bitmap is tied to the DPI it was produced at (<see cref="BitmapDpi"/>).
/// Changing <see cref="RenderDpi"/> marks the cached bitmap stale but deliberately
/// keeps showing it, so a zoom step never flashes the page white: the stale pixels
/// stay up until the higher-resolution render replaces them. A render whose DPI has
/// been superseded before it completes is discarded rather than assigned, which is
/// what stops a slow low-zoom render from overwriting a fast high-zoom one during a
/// rapid zoom sequence.
/// </remarks>
public sealed class PageImageViewModel : ObservableObject
{
    /// <summary>Two render DPIs closer than this are the same request.</summary>
    private const double DpiEpsilon = 1e-6;

    private readonly DocumentViewModel _doc;
    private readonly int _pageIndex;

    /// <summary>Serializes renders for this one page, so two overlapping requests
    /// cannot both decide the cache is empty and render the same page twice.</summary>
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    private BitmapSource? _bitmap;
    private bool _isRendering;
    private double _renderDpi;
    private double _bitmapDpi;
    private string? _renderError;

    public PageImageViewModel(DocumentViewModel doc, int pageIndex, double renderDpi = 96.0)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _renderDpi = renderDpi;
    }

    public int PageIndex => _pageIndex;

    public int DisplayNumber => _pageIndex + 1;

    /// <summary>The DPI this page should render at (1x zoom on a 96-DPI screen is 96).</summary>
    public double RenderDpi
    {
        get => _renderDpi;
        set
        {
            if (Math.Abs(_renderDpi - value) > DpiEpsilon)
            {
                // The cached bitmap is now stale, but it stays on screen until the
                // replacement arrives; only IsStale flips. Callers that need the
                // pixels gone (the page content actually changed) call Clear().
                _renderDpi = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsStale));
            }
        }
    }

    /// <summary>The DPI the currently cached <see cref="Bitmap"/> was rendered at.</summary>
    public double BitmapDpi => _bitmapDpi;

    /// <summary>True when the cached bitmap no longer matches <see cref="RenderDpi"/>
    /// and a re-render is owed. A page with no bitmap at all is not "stale" — it is
    /// simply not loaded yet.</summary>
    public bool IsStale => _bitmap != null && Math.Abs(_bitmapDpi - _renderDpi) > DpiEpsilon;

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
                OnPropertyChanged(nameof(IsStale));
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

    /// <summary>The message from the last failed render, or null when the last
    /// attempt succeeded. Bound to the page's visible error state so a failed render
    /// is never just a blank sheet (FR-VIEW-01 must not fail silently).</summary>
    public string? RenderError
    {
        get => _renderError;
        private set
        {
            if (!string.Equals(_renderError, value, StringComparison.Ordinal))
            {
                _renderError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRenderError));
            }
        }
    }

    public bool HasRenderError => _renderError != null;

    /// <summary>Drops the cached bitmap so the page renders again from scratch. Use
    /// when the page content changed (edit, form fill, redaction); a mere zoom change
    /// should set <see cref="RenderDpi"/> instead, which keeps the stale pixels
    /// visible until the replacement is ready.</summary>
    public void Clear()
    {
        Bitmap = null;
        _bitmapDpi = 0;
    }

    /// <summary>
    /// Renders this page to a frozen bitmap at the current <see cref="RenderDpi"/>.
    /// The engine call runs off the UI thread and the result is applied back on the
    /// dispatcher, so binding refresh is thread-safe. No-op when the cache already
    /// holds this page at this DPI.
    /// </summary>
    public async Task RenderAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || !NeedsRender())
        {
            return;
        }

        await _renderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read after queueing: the target DPI may have moved, or another
            // request may have satisfied this page, while this one waited.
            if (ct.IsCancellationRequested || !NeedsRender())
            {
                return;
            }

            double dpi = _renderDpi;
            IsRendering = true;

            PageForge.Core.Pdf.RenderedPdfPage render =
                await _doc.RenderAsync(_pageIndex, (float)dpi, ct).ConfigureAwait(false);

            var memory = new System.IO.MemoryStream(render.PngBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = memory;
            bmp.EndInit();
            bmp.Freeze();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    // Discard a render the user has already zoomed past, so a slow
                    // low-DPI result cannot overwrite a newer high-DPI one.
                    if (Math.Abs(_renderDpi - dpi) <= DpiEpsilon)
                    {
                        _bitmapDpi = dpi;
                        Bitmap = bmp;
                        RenderError = null;
                    }
                },
                System.Windows.Threading.DispatcherPriority.DataBind);
        }
        catch (OperationCanceledException)
        {
            // A superseded render was cancelled; leave the slot as-is for a retry.
        }
        catch (Exception exception)
        {
            // Surface the failure on the page itself, then let it propagate so the
            // caller can log it. The bound error state is what the user sees.
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => RenderError = exception.Message,
                System.Windows.Threading.DispatcherPriority.DataBind);
            throw;
        }
        finally
        {
            IsRendering = false;
            _renderGate.Release();
        }
    }

    /// <summary>
    /// Re-attempts a render the user was told had failed. Clearing the error first
    /// is what makes the attempt visible: the error panel disappears, and comes back
    /// only if this attempt fails too. Rethrows nothing — the caller is a UI event
    /// handler and the outcome is already on screen.
    /// </summary>
    public async Task RetryAsync(CancellationToken ct = default)
    {
        RenderError = null;

        try
        {
            await RenderAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded; the slot keeps whatever it has.
        }
        catch (Exception)
        {
            // RenderAsync has already set RenderError, which is what the user reads.
        }
    }

    /// <summary>True when the cache cannot satisfy a request at the current DPI.</summary>
    private bool NeedsRender() =>
        _bitmap == null || Math.Abs(_bitmapDpi - _renderDpi) > DpiEpsilon;
}

/// <summary>
/// Attached behavior that renders a page's image as soon as the Image is realized on
/// screen (used by the virtualized page list and thumbnail strip). Attach with
/// <c>views:PageImageBehavior.RenderOnLoad="True"</c> on the Image; the DataContext
/// must be a <see cref="PageSlotViewModel"/>.
/// </summary>
public static class PageImageBehavior
{
    /// <summary>Resolved per call rather than cached in a static field: this type
    /// can be loaded before <see cref="Diagnostics.AppLog.Initialize"/> runs, and a
    /// cached logger would then be the no-op one for the life of the process.</summary>
    private static ILogger Log => Diagnostics.AppLog.For<PageImageViewModel>();

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
            element.Loaded += async (_, _) => await RenderRealizedAsync(element).ConfigureAwait(true);
        }
    }

    private static async Task RenderRealizedAsync(FrameworkElement element)
    {
        PageImageViewModel? image = ResolveImage(element);
        if (image is null)
        {
            return;
        }

        try
        {
            await image.RenderAsync();
        }
        catch (OperationCanceledException)
        {
            // A superseded render was cancelled; the slot keeps whatever it has so
            // the next Loaded / DPI change can retry.
        }
        catch (Exception exception)
        {
            // A failed render must never escape this async-void seam and take the
            // app down. RenderError is already set and bound to a visible error
            // state, so the user is told; this is only the diagnostic trail.
            Log.LogError(
                exception,
                "Render failed for page {PageNumber} at {Dpi} DPI.",
                image.DisplayNumber,
                image.RenderDpi);
        }
    }

    /// <summary>
    /// Picks the image this element is actually showing. The page and thumbnail
    /// templates both bind through a <see cref="PageSlotViewModel"/> — never a
    /// <see cref="PageImageViewModel"/> — so matching the DataContext against
    /// PageImageViewModel silently skipped every render and left the viewer blank.
    /// The two surfaces are told apart by their binding path, because they render
    /// the same page at very different DPIs.
    ///
    /// Public so the --smoke slot-render proof can assert the resolution directly:
    /// this is the seam the original blank-viewer bug hid in, and a proof that only
    /// called RenderAsync would not have caught it.
    /// </summary>
    public static PageImageViewModel? ResolveImage(FrameworkElement element) =>
        element.DataContext switch
        {
            PageSlotViewModel slot => BindsThumbnail(element) ? slot.Thumbnail : slot.Image,
            PageImageViewModel direct => direct,
            _ => null,
        };

    private static bool BindsThumbnail(FrameworkElement element)
    {
        if (element is not System.Windows.Controls.Image image)
        {
            return false;
        }

        System.Windows.Data.BindingExpression? binding =
            image.GetBindingExpression(System.Windows.Controls.Image.SourceProperty);

        return binding?.ParentBinding.Path?.Path?.StartsWith(
            nameof(PageSlotViewModel.Thumbnail), StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Renders a page's accessible text layer (WCAG 1.1.1) as soon as its slot is
    /// realized on screen. Attach with
    /// <c>views:PageImageBehavior.EnsureTextOnLoad="True"</c> on the overlay
    /// TextBlock; the DataContext must be a <see cref="PageSlotViewModel"/>.
    /// </summary>
    public static readonly DependencyProperty EnsureTextOnLoadProperty =
        DependencyProperty.RegisterAttached(
            "EnsureTextOnLoad",
            typeof(bool),
            typeof(PageImageBehavior),
            new PropertyMetadata(false, OnEnsureTextOnLoadChanged));

    public static bool GetEnsureTextOnLoad(DependencyObject obj) => (bool)obj.GetValue(EnsureTextOnLoadProperty);

    public static void SetEnsureTextOnLoad(DependencyObject obj, bool value) => obj.SetValue(EnsureTextOnLoadProperty, value);

    private static void OnEnsureTextOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            element.Loaded += async (_, _) =>
            {
                if (element.DataContext is PageSlotViewModel slot)
                {
                    await slot.EnsureAccessibleTextAsync();
                }
            };
        }
    }
}
