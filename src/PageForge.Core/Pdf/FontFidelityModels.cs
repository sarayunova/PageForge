// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>The reason a character cannot be cleanly rendered by a run's font.</summary>
public enum FontFidelityReason
{
    /// <summary>The character has no glyph in the run's (possibly embedded-subset) font.</summary>
    MissingGlyph,

    /// <summary>The run's font is not embedded in the document, so fidelity is not guaranteed.</summary>
    NonEmbedded,

    /// <summary>The character is outside the character set the run's font family can encode.</summary>
    UnsupportedCharacter,
}

/// <summary>
/// A bundled font-fallback substitution: the replacement character to paint and,
/// when a different font family is required, the fallback PostScript font name.
/// <see cref="Reason"/> records why the substitution is needed; a null
/// <see cref="Replacement"/> means no substitution could be resolved.
/// </summary>
public sealed record FontSubstitution(string? Replacement, string? FallbackFontName, FontFidelityReason Reason);

/// <summary>
/// One character that triggered a font-fidelity flag during a FR-EDIT-03 check,
/// with the resolved substitution (when available).
/// </summary>
public sealed record FontFidelityIssue(char Character, int Unicode, FontSubstitution? Substitution)
{
    /// <summary>True when a substitution was resolved, so the character can be painted via a substitute.</summary>
    public bool HasSubstitution => Substitution?.Replacement is not null;
}

/// <summary>
/// The FR-EDIT-03 outcome for a proposed text edit of one run. Carries whether
/// the run's font is embedded, the list of flagged characters and their
/// substitutions, and the summary flags a shell uses to surface the issue inline
/// and in the properties panel.
/// </summary>
public sealed record FontFidelityResult(
    string FontName,
    bool FontEmbedded,
    IReadOnlyList<FontFidelityIssue> Issues)
{
    /// <summary>True when at least one character needs attention.</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>True when any flagged character was resolved by a substitution.</summary>
    public bool HasSubstitutions => Issues.Any(issue => issue.HasSubstitution);

    /// <summary>True when the run's font is not fully embedded in the document.</summary>
    public bool RunNotEmbedded => !FontEmbedded;
}
