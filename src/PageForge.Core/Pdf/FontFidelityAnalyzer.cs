// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure FR-EDIT-03 analyzer: scans a proposed replacement text for a run and
/// flags every character the run's font may not render faithfully — a font not
/// fully embedded in the document, a character with no glyph in the (possibly
/// subset) font, or a character outside the family's character set — resolving a
/// bundled substitution where one exists. Core-domain and UI-free so the 
/// detection/substitution/surfacing rules are unit-testable (TSD §6).
///
/// The engine's rewrite hard-gates encodability natively; this analyzer runs
/// BEFORE commit so the shell can surface the substitution inline and in the
/// properties panel (FR-EDIT-03) instead of failing the edit.
/// </summary>
public static class FontFidelityAnalyzer
{
    /// <summary>
    /// Analyzes replacing <paramref name="target"/>'s text with
    /// <paramref name="newText"/>. Returns a <see cref="FontFidelityResult"/>
    /// describing every flagged character and its resolved substitution (or the
    /// lack of one). A single character appears once even if repeated in the
    /// text, so the inline marker and properties panel stay concise.
    /// </summary>
    public static FontFidelityResult Analyze(
        PdfTextRun target,
        string newText,
        FontFallbackTable? table = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(newText);
        FontFallbackTable source = table ?? FontFallbackTable.Default;

        var seen = new HashSet<int>();
        var issues = new List<FontFidelityIssue>();

        foreach (Rune rune in newText.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp < 0x20 || (cp >= 0x7F && cp <= 0x9F) || cp >= 0xA0)
            {
                char c = (char)cp;
                if (seen.Add(cp))
                {
                    issues.Add(new FontFidelityIssue(c, cp, source.Resolve(cp, target.FontName, target.FontEmbedded)));
                }
            }
        }

        return new FontFidelityResult(target.FontName, target.FontEmbedded, issues);
    }
}
