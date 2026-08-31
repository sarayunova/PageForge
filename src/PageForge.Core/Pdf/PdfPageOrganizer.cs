// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns high-level FR-PAGE operations (merge, split, insert,
/// delete, rotate, reorder, extract) into an ordered list of
/// <see cref="PageBuildRef"/> build-job entries and hands them to
/// <see cref="IPdfEngine.BuildPdfAsync"/>. Keeping every operation here means the
/// job-assembly logic is shared between the WPF/WinUI shells and is fully unit
/// testable against a fake engine (no native dependency).
/// </summary>
public static class PdfPageOrganizer
{
    /// <summary>Executes a fully-specified build job and returns the written page
    /// count. The single choke point for every page operation.</summary>
    public static ValueTask<int> BuildAsync(
        IPdfEngine engine,
        string outputPath,
        IReadOnlyList<PageBuildRef> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page must be selected.", nameof(pages));
        }

        return engine.BuildPdfAsync(outputPath, pages, cancellationToken);
    }

    /// <summary>Extract: writes a new document containing only the selected pages
    /// of <paramref name="sourcePath"/> (in the order listed).</summary>
    public static ValueTask<int> ExtractAsync(
        IPdfEngine engine,
        string sourcePath,
        IReadOnlyList<int> selectedPages,
        string outputPath,
        CancellationToken cancellationToken = default)
        => BuildAsync(engine, outputPath, Refs(sourcePath, selectedPages), cancellationToken);

    /// <summary>Delete: writes a new document with every page of
    /// <paramref name="sourcePath"/> except those in <paramref name="pagesToDelete"/>.</summary>
    public static ValueTask<int> DeleteAsync(
        IPdfEngine engine,
        string sourcePath,
        int pageCount,
        IReadOnlySet<int> pagesToDelete,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var keep = Enumerable.Range(0, pageCount).Where(i => !pagesToDelete.Contains(i)).ToArray();
        return BuildAsync(engine, outputPath, Refs(sourcePath, keep), cancellationToken);
    }

    /// <summary>Rotate: writes every page of <paramref name="sourcePath"/>, adding
    /// the clockwise rotation (quarter turns 0..3) supplied per page for any page
    /// present in <paramref name="rotations"/>; all other pages are unchanged.</summary>
    public static ValueTask<int> RotateAsync(
        IPdfEngine engine,
        string sourcePath,
        int pageCount,
        IReadOnlyDictionary<int, int> rotations,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var refs = new List<PageBuildRef>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            bool has = rotations.TryGetValue(i, out int turns);
            refs.Add(new PageBuildRef(sourcePath, i, has ? turns : 0));
        }

        return BuildAsync(engine, outputPath, refs, cancellationToken);
    }

    /// <summary>Reorder: writes the pages of <paramref name="sourcePath"/> in the
    /// new order given by <paramref name="newOrder"/> (a permutation of
    /// 0..pageCount-1).</summary>
    public static ValueTask<int> ReorderAsync(
        IPdfEngine engine,
        string sourcePath,
        IReadOnlyList<int> newOrder,
        string outputPath,
        CancellationToken cancellationToken = default)
        => BuildAsync(engine, outputPath, Refs(sourcePath, newOrder), cancellationToken);

    /// <summary>Split: writes a contiguous half-open page range [start, end) of
    /// <paramref name="sourcePath"/> to a new file (a specialised extract).</summary>
    public static ValueTask<int> SplitAsync(
        IPdfEngine engine,
        string sourcePath,
        int startInclusive,
        int endExclusive,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var selected = new List<int>(endExclusive - startInclusive);
        for (int i = startInclusive; i < endExclusive; i++)
        {
            selected.Add(i);
        }

        return BuildAsync(engine, outputPath, Refs(sourcePath, selected), cancellationToken);
    }

    /// <summary>Merge: writes a document containing all pages of each source, in
    /// order. Each entry of <paramref name="sources"/> pairs a path with its page
    /// count (the caller already knows them from the opened documents).</summary>
    public static ValueTask<int> MergeAsync(
        IPdfEngine engine,
        IReadOnlyList<SourceWithCount> sources,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var refs = new List<PageBuildRef>();
        foreach (SourceWithCount source in sources)
        {
            for (int i = 0; i < source.PageCount; i++)
            {
                refs.Add(new PageBuildRef(source.Path, i, 0));
            }
        }

        return BuildAsync(engine, outputPath, refs, cancellationToken);
    }

    /// <summary>Insert: writes <paramref name="targetPath"/>'s pages, with the pages
    /// <paramref name="pagesToInsert"/> of <paramref name="sourcePath"/> spliced in
    /// starting at 0-based position <paramref name="insertAt"/>.</summary>
    public static ValueTask<int> InsertAsync(
        IPdfEngine engine,
        string targetPath,
        int targetPageCount,
        int insertAt,
        string sourcePath,
        IReadOnlyList<int> pagesToInsert,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (insertAt < 0 || insertAt > targetPageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(insertAt));
        }

        var refs = new List<PageBuildRef>(targetPageCount + pagesToInsert.Count);
        for (int i = 0; i < insertAt; i++)
        {
            refs.Add(new PageBuildRef(targetPath, i, 0));
        }

        foreach (int page in pagesToInsert)
        {
            refs.Add(new PageBuildRef(sourcePath, page, 0));
        }

        for (int i = insertAt; i < targetPageCount; i++)
        {
            refs.Add(new PageBuildRef(targetPath, i, 0));
        }

        return BuildAsync(engine, outputPath, refs, cancellationToken);
    }

    private static List<PageBuildRef> Refs(string sourcePath, IEnumerable<int> pages)
    {
        var refs = new List<PageBuildRef>();
        foreach (int page in pages)
        {
            refs.Add(new PageBuildRef(sourcePath, page, 0));
        }

        return refs;
    }
}

/// <summary>A source PDF together with its known page count, for merge jobs.</summary>
public sealed record SourceWithCount(string Path, int PageCount);
