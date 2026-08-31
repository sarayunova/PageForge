// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// A fully rendered PDF page in PNG form. Decoupled from any native engine --
/// the WinUI layer only ever sees these bytes.
/// </summary>
public sealed class RenderedPdfPage
{
    public required byte[] PngBytes { get; init; }

    public required int WidthPixels { get; init; }

    public required int HeightPixels { get; init; }
}