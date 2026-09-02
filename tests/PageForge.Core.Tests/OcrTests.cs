// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-OCR-01 unit tests: the <see cref="OcrService"/> entry point drives the
/// engine seam to recognize the open document offline and write a searchable
/// PDF. All assertions run against the fake engine with no native dependency.
/// </summary>
public sealed class OcrTests : IDisposable
{
    private readonly string _output = Path.Combine(
        Path.GetTempPath(), $"pageforge-ocr-test-{Guid.NewGuid():N}.pdf");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_output))
            {
                File.Delete(_output);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task OcrAsync_writes_a_searchable_pdf_and_reports_the_result()
    {
        var engine = new FakePdfEngine(2);
        OcrResult result = await OcrService.OcrAsync(engine, _output);

        Assert.Equal(2, result.PageCount);
        Assert.Equal("eng", result.Language);
        Assert.Equal(Path.GetFullPath(_output), result.OutputPath);
        Assert.True(File.Exists(_output), "The engine should leave a new searchable PDF on disk.");

        Assert.Null(engine.LastOcr?.Options);
        Assert.Equal(Path.GetFullPath(_output), engine.LastOcr?.OutputPath);
        Assert.Equal(result, Assert.Single(engine.OcrOutputs));
    }

    [Fact]
    public async Task OcrAsync_forwarding_non_default_options()
    {
        var engine = new FakePdfEngine(1);
        string dataDir = Path.Combine(Path.GetTempPath(), $"pageforge-ocr-tess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var options = new OcrOptions(Language: "deu", DataDirectory: dataDir);
            OcrResult result = await OcrService.OcrAsync(engine, _output, options);

            Assert.Equal("deu", result.Language);
            Assert.Equal(dataDir, result.DataDirectory);
            Assert.Equal(options, engine.LastOcr?.Options);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task OcrAsync_rejects_an_existing_output_path()
    {
        var engine = new FakePdfEngine(1);
        await File.WriteAllTextAsync(_output, "existing");
        await Assert.ThrowsAsync<IOException>(() =>
            OcrService.OcrAsync(engine, _output).AsTask());
    }

    [Fact]
    public async Task OcrAsync_rejects_a_language_with_path_separators()
    {
        var engine = new FakePdfEngine(1);
        var options = new OcrOptions(Language: @"..\eng.traineddata");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OcrService.OcrAsync(engine, _output, options).AsTask());
    }

    [Fact]
    public async Task OcrAsync_rejects_an_empty_output_path()
    {
        var engine = new FakePdfEngine(1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OcrService.OcrAsync(engine, string.Empty).AsTask());
    }

    [Fact]
    public async Task OcrAsync_propagates_engine_failures()
    {
        var engine = new FakePdfEngine(1);
        engine.OnOcr = _ => throw new InvalidOperationException("ocr engine failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OcrService.OcrAsync(engine, _output).AsTask());
        Assert.False(File.Exists(_output), "No output artifact should remain after a failure.");
    }
}