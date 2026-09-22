// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PageForge.App.Wpf.Resources;
using Xunit;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// TRD section 6: "UI strings externalized to resource files". This is the
/// guard that keeps that true, and it exists because the alternative was the
/// failure this codebase keeps repeating - a migration that is 90% done, looks
/// finished, and leaves the remaining literals invisible until someone reads
/// every XAML file again.
///
/// <para>
/// The check is deliberately bidirectional, in the same spirit as the fidelity
/// corpus manifest. A key used but not defined is the obvious half: it renders
/// as the key, which is a visible defect. A resource defined but never used is
/// the half nobody catches - it is dead weight that a translator will still be
/// asked to translate. Both fail here.
/// </para>
///
/// <para>
/// The third test is the one that matters most, and the reason this lives in a
/// project that references the app rather than only scanning text: it proves
/// the resource actually RESOLVES. A wrong base name in the ResourceManager
/// compiles perfectly, passes any text-scanning check, and ships an app whose
/// every button reads "Document_NextPage_Name".
/// </para>
/// </summary>
public sealed class UiStringsTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PageForge.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("PageForge.sln not found above the test output.");
        }
    }

    private static string AppDir => Path.Combine(RepoRoot, "src", "PageForge.App.Wpf");

    private static string ResxPath => Path.Combine(AppDir, "Resources", "UiStrings.resx");

    /// <summary>Every key defined in UiStrings.resx.</summary>
    private static HashSet<string> DefinedKeys()
    {
        XDocument doc = XDocument.Load(ResxPath);
        return doc.Root!
            .Elements("data")
            .Select(d => (string)d.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> SourceFiles(string extension)
        => Directory.EnumerateFiles(AppDir, extension, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Every key referenced from XAML markup or C#, with where it came from.</summary>
    private static Dictionary<string, string> ReferencedKeys()
    {
        var used = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in SourceFiles("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{loc:Str\s+([A-Za-z0-9_]+)\s*\}"))
            {
                used[m.Groups[1].Value] = Path.GetFileName(file);
            }
        }

        foreach (string file in SourceFiles("*.cs"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file),
                         @"UiStrings\.(?:Get|Format)\(\s*""([A-Za-z0-9_]+)"""))
            {
                used[m.Groups[1].Value] = Path.GetFileName(file);
            }
        }

        return used;
    }

    [Fact]
    public void Every_key_the_ui_asks_for_is_defined()
    {
        var defined = DefinedKeys();
        var missing = ReferencedKeys()
            .Where(kv => !defined.Contains(kv.Key))
            .Select(kv => $"  {kv.Key}  (used in {kv.Value})")
            .ToList();

        Assert.True(missing.Count == 0,
            "These keys are used but not defined in UiStrings.resx, so they would render as the key itself:\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void Every_defined_string_is_actually_used()
    {
        var used = ReferencedKeys().Keys.ToHashSet(StringComparer.Ordinal);
        var orphans = DefinedKeys().Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(orphans.Count == 0,
            "These strings are defined but nothing references them. Delete them, or the next translator "
            + "is paid to translate text no user can reach:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void The_resource_resolves_rather_than_echoing_the_key()
    {
        // Proves the ResourceManager base name matches the embedded resource.
        // If it does not, Get() returns the key and the app ships unreadable.
        var defined = DefinedKeys();
        Assert.NotEmpty(defined);

        var echoed = defined
            .Where(key => string.Equals(UiStrings.Get(key), key, StringComparison.Ordinal))
            .ToList();

        Assert.True(echoed.Count == 0,
            $"{echoed.Count} of {defined.Count} resources resolved to their own key, which means the "
            + "ResourceManager base name does not match the embedded resource. First few: "
            + string.Join(", ", echoed.Take(5)));
    }

    [Fact]
    public void No_readable_literal_is_left_in_the_markup()
    {
        // The attributes a user can read. A literal here is a string that
        // escaped the migration.
        string[] attributes =
        {
            "AutomationProperties.Name", "AutomationProperties.HelpText",
            "ToolTip", "Header", "Title", "Content", "Text",
        };

        var offenders = new List<string>();
        foreach (string file in SourceFiles("*.xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (string attribute in attributes)
            {
                string pattern = @"(?<=\s)" + Regex.Escape(attribute) + @"=""(?<v>[^""{][^""]*)""";
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    string value = m.Groups["v"].Value;

                    // Values with no letters are glyphs, numbers and sizes -
                    // nothing a translator would ever be given.
                    if (Regex.IsMatch(value, "[A-Za-z]"))
                    {
                        offenders.Add($"  {Path.GetFileName(file)}: {attribute}=\"{value}\"");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These readable strings are still hard-coded in markup (TRD section 6):\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The C# half of the markup check. "User-facing" cannot be decided from a
    /// literal alone - a log message and a button label look identical to a
    /// regex - so this works from the sinks instead: the handful of calls whose
    /// arguments a user reads. Anything literal that reaches one of those is a
    /// string that escaped the migration.
    ///
    /// <para>
    /// Scanning is span-based rather than line-based because the strings that
    /// hide longest are the ones broken across lines - a MessageBox whose text
    /// sits on the line after the call, or a Hint built from two concatenated
    /// pieces. Both were present when this test was written, and neither shows
    /// up in a per-line grep.
    /// </para>
    /// </summary>
    [Fact]
    public void No_readable_literal_reaches_a_ui_sink()
    {
        // The leading boundary matters: without it "Hint(" also matches inside
        // "ShowStatusHint(", and every status hint is reported twice.
        const string SinkPattern =
            @"(?<![A-Za-z0-9_])(?:Hint|ShowStatusHint|MessageBox\.Show|AutomationProperties\.SetName)\s*\(";

        var offenders = new List<string>();
        foreach (string file in SourceFiles("*.cs"))
        {
            string text = File.ReadAllText(file);

            {
                foreach (Match sinkMatch in Regex.Matches(text, SinkPattern))
                {
                    int at = sinkMatch.Index;
                    string sink = sinkMatch.Value.Trim();
                    string span = ArgumentSpan(text, sinkMatch.Index + sinkMatch.Length - 1);

                    // A key passed to the resource lookup is a key, not a label.
                    string scrubbed = Regex.Replace(span, @"UiStrings\.(?:Get|Format)\(\s*""[^""]*""", "UiStrings.Resolved(");

                    foreach (Match lit in Regex.Matches(scrubbed, @"\$?""(?<v>[^""]*)"""))
                    {
                        string value = lit.Groups["v"].Value;

                        // Two letters and a space: enough to be a sentence
                        // fragment rather than a format specifier or a name.
                        if (Regex.IsMatch(value, @"[A-Za-z]{2}") && value.Contains(' ', StringComparison.Ordinal))
                        {
                            int line = text.Take(at).Count(c => c == '\n') + 1;
                            offenders.Add($"  {Path.GetFileName(file)}:{line}  {sink}… \"{value}\"");
                        }
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These readable strings still reach a UI sink as literals (TRD section 6):\n"
            + string.Join("\n", offenders.Distinct()));
    }

    /// <summary>
    /// The text between <paramref name="openParen"/> and its matching close,
    /// so a call broken across lines is read whole. Quoted text is skipped so a
    /// bracket inside a message cannot end the span early.
    /// </summary>
    private static string ArgumentSpan(string text, int openParen)
    {
        int depth = 0;
        bool inString = false;
        for (int i = openParen; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (c == '\\') { i++; }
                else if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '(': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return text[openParen..(i + 1)];
                    }

                    break;
            }
        }

        return text[openParen..];
    }
}
