// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// The bundled font-fallback table (FR-EDIT-03). Pure Core, no font binaries:
/// it encodes the substitution RULES the desktop uses to resolve a character a
/// run's font cannot safely paint — a typographic-punctuation normalization map
/// plus a base family classification. A character that resolves to an ASCII
/// equivalent (curly quote → straight quote, em dash → "--", ellipsis → "...",
/// etc.) can be painted through the run's own font; a character on the other
/// side of the base-14 gap is flagged unresolvable unless a fallback font name
/// is supplied.
///
/// The logic deliberately stays in Core (TSD §6, winui-net-conventions) so the
/// substitution rules are unit-testable and shared by every shell.
/// </summary>
public sealed class FontFallbackTable
{
    /// <summary>
    /// Typographic punctuation / spacing characters commonly introduced by
    /// "smart" text that the base-14 core fonts and many embedded subsets cannot
    /// encode, mapped to an ASCII rendering that any PDF text font can paint.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> CharacterNormalizations =
        new Dictionary<int, string>
        {
            { 0x2018, "'" }, // ‘ left single quote
            { 0x2019, "'" }, // ’ right single quote
            { 0x201A, "'" }, // ‚ low single quote
            { 0x201B, "'" }, // ‛ reversed single quote
            { 0x201C, "\"" }, // “ left double quote
            { 0x201D, "\"" }, // ” right double quote
            { 0x201E, "\"" }, // „ low double quote
            { 0x201F, "\"" }, // ‟ reversed double quote
            { 0x2013, "-" }, // – en dash
            { 0x2014, "--" }, // — em dash
            { 0x2015, "--" }, // ― horizontal bar
            { 0x2026, "..." }, // … ellipsis
            { 0x00A0, " " }, // non-breaking space
            { 0x2009, " " }, // thin space
            { 0x200B, "" }, // zero-width space
            { 0xFEFF, "" }, // zero-width no-break space
            { 0x2122, "(TM)" }, // ™ trade mark
            { 0x00AE, "(R)" }, // ® registered
            { 0x00A9, "(c)" }, // © copyright
        };

    private readonly IReadOnlyDictionary<string, FontFallbackFamily> _families;

    /// <summary>A fallback family with a substitute PostScript font name.</summary>
    public sealed record FontFallbackFamily(string PostScriptName, string? FallbackFont);

    /// <summary>Default table with the common families pre-registered.</summary>
    public static FontFallbackTable Default { get; } = CreateDefault();

    public FontFallbackTable(IReadOnlyDictionary<string, FontFallbackFamily>? families = null)
        => _families = families ?? new Dictionary<string, FontFallbackFamily>();

    private static FontFallbackTable CreateDefault() => new(
        new Dictionary<string, FontFallbackFamily>(StringComparer.OrdinalIgnoreCase)
        {
            // Base-14 core families. PostScript names are matched by prefix so
            // Helvetica-Bold, HelveticaItalic, etc. all resolve to Helvetica.
            ["Helvetica"] = new("Helvetica", null),
            ["Times"] = new("Times-Roman", null),
            ["Times-Roman"] = new("Times-Roman", null),
            ["TimesNewRoman"] = new("Times-Roman", null),
            ["Courier"] = new("Courier", null),
            ["CourierNew"] = new("Courier", null),
        });

    /// <summary>
    /// Resolves a substitution for <paramref name="c"/> given the run's font
    /// name and embedded flag. Returns null when the character needs no
    /// substitution (it is within the plain-ASCII set the font can always paint,
    /// or is already a base Latin-1 code point a base-14 font covers).
    /// </summary>
    public FontSubstitution? Resolve(int rune, string fontName, bool fontEmbedded)
    {
        // Non-characters / control code points are never paintable.
        if (rune <= 0x1F || (rune >= 0x7F && rune <= 0x9F))
        {
            return new(null, null, FontFidelityReason.UnsupportedCharacter);
        }

        // Plain ASCII is the universal lowest common denominator.
        if (rune >= 0x20 && rune <= 0x7E)
        {
            return null;
        }

        // A typographic character with a known ASCII normalization.
        if (CharacterNormalizations.TryGetValue(rune, out string? normalized))
        {
            return new FontSubstitution(normalized, null, FontFidelityReason.MissingGlyph);
        }

        // Latin-1 (0x00A0..0x00FF) beyond our normalizations: base-14 fonts
        // (WinAnsi) can encode these, but an unembedded or subset font may not.
        // Flag as a substitution only if we can name a fallback family.
        if (rune >= 0x00A1 && rune <= 0x00FF)
        {
            string? fallback = FindFallbackFont(fontName);
            FontFidelityReason reason = fontEmbedded ? FontFidelityReason.MissingGlyph : FontFidelityReason.NonEmbedded;
            // We cannot paint from a substitute without a fallback font that
            // covers it; report the issue with no replacement so the shell
            // surfaces it (the engine's own encode gate is authoritative).
            return new FontSubstitution(null, fallback, reason);
        }

        // Beyond Latin-1: an embedded subset or core font almost certainly lacks
        // the glyph. Resolve only if a fallback family can cover it.
        string? fb = FindFallbackFont(fontName);
        FontFidelityReason nonEmbeddedReason = fontEmbedded ? FontFidelityReason.MissingGlyph : FontFidelityReason.NonEmbedded;
        return new FontSubstitution(null, fb, nonEmbeddedReason);
    }

    /// <summary>
    /// Classifies a font's PostScript name to a registered fallback family, or
    /// null when the family is unknown (a fully embedded custom font we trust).
    /// </summary>
    public string? FindFallbackFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return null;
        }

        // Try an exact match first, then a case-insensitive prefix so e.g.
        // "Helvetica-BoldOblique" resolves to the Helvetica family.
        if (_families.TryGetValue(fontName, out FontFallbackFamily? family))
        {
            return family.FallbackFont ?? family.PostScriptName;
        }

        foreach ((string key, FontFallbackFamily f) in _families)
        {
            if (fontName.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return f.FallbackFont ?? f.PostScriptName;
            }
        }

        return null;
    }
}
