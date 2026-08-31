# Copyright (c) 2026 LiVi Software Company
# SPDX-License-Identifier: AGPL-3.0-only
# This file is part of PageForge. See LICENSE for the full license text.
#
# Generates a deterministic 3-page mock PDF used by FR-PAGE fidelity verification:
# tools/sample-pdf/sample-pages3.pdf. Each page carries a distinct label and a
# distinct filled rectangle so page order/rotation is observable in renders.
# Modeled on generate-sample-pdf.ps1 (Phase 0 single-page mock).

$ErrorActionPreference = 'Stop'

$pages = @(
    @{ Label = 'Page 1 of Pages 3'; Color = '0.9 0.2 0.2' },
    @{ Label = 'Page 2 of Pages 3'; Color = '0.2 0.9 0.2' },
    @{ Label = 'Page 3 of Pages 3'; Color = '0.2 0.2 0.9' }
)

function New-ContentString {
    param([pscustomobject]$p)
    # Length must be byte-length; all chars below 0x80 so len == byte count.
    return ("BT /F1 20 Tf 72 780 Td ($($p.Label)) Tj ET`n" +
            "$($p.Color) rg 72 700 460 40 re f`n" +
            "BT /F1 12 Tf 72 655 Td (Sample page for PageForge FR-PAGE verification) Tj ET`n")
}

# Object layout:
#   1 Catalog, 2 Pages tree, 3/4/5 the three page dicts,
#   6 /F1 font, 7/8/9 the three content streams (page 3 -> stream 7, etc.)
$contents = @()
foreach ($p in $pages) { $contents += New-ContentString $p }

$o = New-Object System.Collections.Generic.List[string]
$o.Add('<< /Type /Catalog /Pages 2 0 R >>')
$kids = (3..5 | ForEach-Object { "$_ 0 R" }) -join ' '
$o.Add("<< /Type /Pages /Kids [$kids] /Count 3 >>")
for ($i = 0; $i -lt 3; $i++) {
    $o.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] " +
           "/Resources << /Font << /F1 6 0 R >> >> /Contents $(7 + $i) 0 R >>")
}
$o.Add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>')
foreach ($c in $contents) {
    $o.Add("<< /Length $($c.Length) >>`nstream`n$c`nendstream")
}

$bytes = New-Object System.Collections.Generic.List[byte]
$offsets = New-Object 'System.Collections.Generic.List[int]'

function Append-Bytes { param([string]$s) foreach ($b in [System.Text.Encoding]::ASCII.GetBytes($s)) { $bytes.Add($b) } }

Append-Bytes "%PDF-1.4`n"
for ($i = 0; $i -lt $o.Count; $i++) {
    $offsets.Add($bytes.Count)
    Append-Bytes "$($i + 1) 0 obj`n"
    Append-Bytes ($o[$i] + "`n")
    Append-Bytes "endobj`n"
}
$xrefStart = $bytes.Count
Append-Bytes "xref`r`n"
$count = $o.Count + 1
Append-Bytes "0 $count`r`n"
Append-Bytes "0000000000 65535 f`r`n"
for ($i = 0; $i -lt $o.Count; $i++) {
    Append-Bytes ("{0:d10} 00000 n`r`n" -f $offsets[$i])
}
Append-Bytes "trailer`r`n"
Append-Bytes "<< /Size $count /Root 1 0 R >>`r`n"
Append-Bytes "startxref`r`n"
Append-Bytes "$xrefStart`r`n"
Append-Bytes "%%EOF`r`n"

$out = Join-Path $PSScriptRoot 'sample-pdf\sample-pages3.pdf'
$outDir = Split-Path $out -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllBytes($out, $bytes.ToArray())
Write-Host "wrote $out ($($bytes.Count) bytes, xref at $xrefStart)"
