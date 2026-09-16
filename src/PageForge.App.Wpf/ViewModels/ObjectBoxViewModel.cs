// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using CommunityToolkit.Mvvm.ComponentModel;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.ViewModels;

/// <summary>
/// One editable image/vector object's box on the object-edit surface: where it is
/// on screen, whether it is the current selection, and what to call it.
///
/// Unlike the redaction and form-field boxes, this one is observable and moves.
/// A drag rewrites the screen rectangle live before anything is committed to the
/// engine, and selection changes how it is painted, so the view binds to it
/// rather than being handed a fresh snapshot per frame.
///
/// The PDF-to-screen conversion comes from <see cref="PageBoxGeometry"/> - the
/// Y-flipping one, shared with the redaction overlay, because
/// <see cref="PdfPageObject.Bounds"/> is in PDF user space with a bottom-left
/// origin, exactly like a redaction region. That was confirmed against the real
/// shim rather than assumed: the bbox is the object's `cm` matrix applied to the
/// unit square in user space. Form fields are the odd one out and deliberately
/// do not share this.
/// </summary>
public sealed class ObjectBoxViewModel : ObservableObject
{
    private readonly double _renderDpi;
    private readonly double _pageHeightPx;
    private PdfRect _bounds;
    private bool _isSelected;
    private double _left;
    private double _top;
    private double _width;
    private double _height;

    /// <param name="obj">The object as the engine listed it.</param>
    /// <param name="renderDpi">The DPI the page is currently rendered at.</param>
    /// <param name="pageHeightPx">The rendered page height, for the Y flip.</param>
    public ObjectBoxViewModel(PdfPageObject obj, double renderDpi, double pageHeightPx)
    {
        ArgumentNullException.ThrowIfNull(obj);
        Object = obj;
        _renderDpi = renderDpi;
        _pageHeightPx = pageHeightPx;
        Bounds = obj.Bounds;
        AccessibleName = $"{obj.Label} at ({obj.Bounds.X0:F0}, {obj.Bounds.Y0:F0}) pt";
    }

    /// <summary>The object as the engine listed it. Its <c>Bounds</c> are the last
    /// committed ones; <see cref="Bounds"/> here is what the surface shows now.</summary>
    public PdfPageObject Object { get; }

    public string Id => Object.Id;

    public string AccessibleName { get; }

    /// <summary>The box in PDF points. Setting it moves the screen rectangle with
    /// it, so the two cannot drift apart - which is what a separate "screen rect"
    /// field alongside the bounds invites.</summary>
    public PdfRect Bounds
    {
        get => _bounds;
        set
        {
            _bounds = value;
            ScreenBox box = PageBoxGeometry.ToScreen(value, _renderDpi, _pageHeightPx);
            Left = box.Left;
            Top = box.Top;
            Width = box.Width;
            Height = box.Height;
        }
    }

    /// <summary>Whether a point in overlay coordinates is inside the box.</summary>
    public bool Contains(double x, double y) =>
        x >= Left && x <= Left + Width && y >= Top && y <= Top + Height;

    /// <summary>Whether this is the current selection. The overlay paints a
    /// selected box differently and only a selected box carries handles.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double Left
    {
        get => _left;
        private set => SetProperty(ref _left, value);
    }

    public double Top
    {
        get => _top;
        private set => SetProperty(ref _top, value);
    }

    public double Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        private set => SetProperty(ref _height, value);
    }
}
