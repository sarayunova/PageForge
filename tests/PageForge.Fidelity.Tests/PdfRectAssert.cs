// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// Shared rectangle assertion for the object gates.
///
/// Shared rather than copied into each test class on purpose: a duplicated
/// geometry helper is how this codebase ended up with the PDF-to-screen
/// conversion written once per overlay, one of which had its Y flip backwards
/// for as long as nobody could see it.
/// </summary>
internal static class PdfRectAssert
{
    /// <summary>
    /// Compares two PDF rectangles to a quarter of a point. The tolerance is for
    /// the trip through the content stream as decimal text and back through a
    /// float transform, not for placement error: a quarter point is far below
    /// anything visible, and the defects this guards against were off by hundreds
    /// of points or by a factor of a thousand.
    /// </summary>
    public static void Equal(PdfRect expected, PdfRect actual, string what)
    {
        const double tolerance = 0.25;
        Assert.True(
            Math.Abs(expected.X0 - actual.X0) <= tolerance &&
            Math.Abs(expected.Y0 - actual.Y0) <= tolerance &&
            Math.Abs(expected.X1 - actual.X1) <= tolerance &&
            Math.Abs(expected.Y1 - actual.Y1) <= tolerance,
            $"{what}: expected bounds ({expected.X0},{expected.Y0})-({expected.X1},{expected.Y1}), " +
            $"got ({actual.X0},{actual.Y0})-({actual.X1},{actual.Y1}).");
    }
}
