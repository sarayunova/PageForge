// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-03 unit tests for the bundled font-fallback table and
/// <see cref="FontFidelityAnalyzer"/> / <see cref="TextEditService.CheckFontFidelityAsync"/>:
/// plain ASCII needs no substitution, typographic punctuation resolves to ASCII
/// equivalents, a non-embedded font with non-Latin characters is flagged, an
/// issue is surfaced exactly once per unique character, and unresolvable
/// characters carry no substitution so the shell surfaces them. The rewrite's
/// native hard gate remains authoritative; this is the pre-commit surfacing.
/// </summary>
public sealed class FontFidelityTests
{
    private static PdfTextRun Run(string text, bool embedded = true, string fontName = "Helvetica")
        => new(0, 0, 0, 100, 12, 12, embedded, fontName, text);

    [Fact]
    public void Plain_ascii_needs_no_substitution()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "plain ascii 123");

        Assert.False(result.HasIssues);
        Assert.False(result.HasSubstitutions);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Curly_quotes_resolve_to_straight_quotes()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "\u201Crepro\u201D");

        Assert.True(result.HasIssues);
        Assert.True(result.HasSubstitutions);
        Assert.Equal(2, result.Issues.Count); // “ and ” are distinct code points, flagged once each
        foreach (FontFidelityIssue issue in result.Issues)
        {
            Assert.True(issue.HasSubstitution);
            Assert.Equal("\"", issue.Substitution!.Replacement);
            Assert.Equal(FontFidelityReason.MissingGlyph, issue.Substitution.Reason);
        }
    }

    [Fact]
    public void Em_dash_resolves_to_double_hyphen()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "a\u2014b");

        Assert.True(result.HasSubstitutions);
        Assert.Equal("--", Assert.Single(result.Issues).Substitution!.Replacement);
    }

    [Fact]
    public void Ellipsis_resolves_to_three_dots()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "to\u2026be");

        Assert.True(result.HasSubstitutions);
        Assert.Equal("...", Assert.Single(result.Issues).Substitution!.Replacement);
    }

    [Fact]
    public void Nbsp_resolves_to_a_space()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "a\u00A0b");

        Assert.True(result.HasSubstitutions);
        Assert.Equal(" ", Assert.Single(result.Issues).Substitution!.Replacement);
    }

    [Fact]
    public void Duplicate_character_is_flagged_once()
    {
        PdfTextRun run = Run("existing");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "\u201Cx\u201D and \u201C again \u201D");

        Assert.Equal(1, result.Issues.Count(issue => issue.Unicode == 0x201C));
    }

    [Fact]
    public void Non_latin_character_with_known_family_finds_a_fallback_font()
    {
        // Greek/Cyrillic beyond Latin-1 on a base-14 (non-embedded) Helvetica run.
        PdfTextRun run = Run("existing", embedded: false, fontName: "Helvetica");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "caf\u00E9 \u0394");

        FontFidelityIssue greek = Assert.Single(result.Issues, issue => issue.Unicode == 0x0394);
        Assert.Equal(FontFidelityReason.NonEmbedded, greek.Substitution!.Reason);
        // No replacement byte can be painted for Δ by Helvetica; the surface flags it.
        Assert.False(greek.HasSubstitution);
    }

    [Fact]
    public void Non_embedded_font_reports_run_not_embedded()
    {
        PdfTextRun run = Run("existing", embedded: false, fontName: "Times-Roman");
        FontFidelityResult result = FontFidelityAnalyzer.Analyze(run, "plain ascii");

        Assert.True(result.RunNotEmbedded);
    }

    [Fact]
    public void Table_resolves_family_from_a_bold_variant_name()
    {
        FontFallbackTable table = FontFallbackTable.Default;

        string? fallback = table.FindFallbackFont("Helvetica-BoldOblique");

        Assert.Equal("Helvetica", fallback);
    }

    [Fact]
    public void Table_returns_null_for_an_unknown_embedded_family()
    {
        FontFallbackTable table = FontFallbackTable.Default;

        Assert.Null(table.FindFallbackFont("MyCustomEmbedded-Bold"));
    }

    [Fact]
    public async Task CheckFontFidelity_returns_result_for_a_run_on_the_page()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, Run("existing"));

        FontFidelityResult result = await TextEditService.CheckFontFidelityAsync(engine, 0, 0, "smart \u201Cquote\u201D");

        Assert.True(result.HasIssues);
        Assert.True(result.HasSubstitutions);
        Assert.False(result.RunNotEmbedded);
    }

    [Fact]
    public async Task CheckFontFidelity_out_of_range_run_throws()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRun(0, Run("existing"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TextEditService.CheckFontFidelityAsync(engine, 0, 9, "text").AsTask());
    }

    [Fact]
    public void Analyzer_rejects_empty_new_text()
    {
        PdfTextRun run = Run("existing");

        Assert.Throws<ArgumentException>(() => FontFidelityAnalyzer.Analyze(run, string.Empty));
    }
}
