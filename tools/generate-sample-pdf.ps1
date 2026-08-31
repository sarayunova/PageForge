# Copyright (c) 2026 LiVi Software Company
# SPDX-License-Identifier: AGPL-3.0-only
# This file is part of PageForge. See LICENSE for the full license text.
#
# Generates the deterministic mock corpus PDF used by Phase 0 verification:
# tools/sample-pdf/sample-phase0.pdf -> tests/PageForge.Fidelity.Tests/corpus/.
# Re-run only to regenerate; the committed bytes are the golden baseline.

$ErrorActionPreference = 'Stop'

$content =
    "BT /F1 24 Tf 72 760 Td (PageForge Phase 0 sample) Tj ET`n" +
    "0.2 0.5 0.9 rg 72 700 460 40 re f`n" +
    "BT /F1 12 Tf 72 655 Td (Rendered through the PageForge shim over MuPDF 1.28) Tj ET`n"

$objects = @(
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    "<< /Length $($content.Length) >>`nstream`n$content`nendstream",
    '<< /Type /Catalog /Pages 2 0 R >>'
)

# Emit 8-bit-safe ASCII bytes; every char below 0x80 so char-count == byte-count.
function Get-AsciiBytes {
    param([string]$s)
    [System.Text.Encoding]::ASCII.GetBytes($s)
}

$bytes = New-Object System.Collections.Generic.List[byte]
$offsets = New-Object 'System.Collections.Generic.List[int]'

function Append-Bytes {
    param([string]$s)
    foreach ($b in Get-AsciiBytes $s) { $bytes.Add($b) }
}

Append-Bytes "%PDF-1.4`n"
for ($i = 0; $i -lt $objects.Count; $i++) {
    $offsets.Add($bytes.Count)
    Append-Bytes "$($i + 1) 0 obj`n"
    Append-Bytes ($objects[$i] + "`n")
    Append-Bytes "endobj`n"
}
$xrefStart = $bytes.Count
Append-Bytes "xref`r`n"
$count = $objects.Count + 1
Append-Bytes "0 $count`r`n"
Append-Bytes "0000000000 65535 f`r`n"
for ($i = 0; $i -lt $objects.Count; $i++) {
    Append-Bytes ("{0:d10} 00000 n`r`n" -f $offsets[$i])
}

Append-Bytes "trailer`r`n"
Append-Bytes "<< /Size $count /Root 1 0 R >>`r`n"
Append-Bytes "startxref`r`n"
Append-Bytes "$xrefStart`r`n"
Append-Bytes "%%EOF`r`n"

$out = Join-Path $PSScriptRoot 'sample-pdf\sample-phase0.pdf'
$outDir = Split-Path $out -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($out, $bytes.ToArray())

Write-Host "wrote $out ($($bytes.Count) bytes, xref at $xrefStart)"
foreach ($o in $offsets) { Write-Host "  offset: $o" }