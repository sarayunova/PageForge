# Copyright (c) 2026 LiVi Software Company
# SPDX-License-Identifier: AGPL-3.0-only
# This file is part of PageForge. See LICENSE for the full license text.
#
# Generates the Phase 1 real-document fidelity corpus (TSD §12 exit gate:
# dogfood the viewer/organizer/annotator on real documents with zero crashes).
#
# Produces four deterministic realistic PDFs (contracts, forms, scans,
# multi-column, Unicode) into tools/sample-pdf/corpus/ and a golden page-1
# render for each into tools/sample-pdf/golden/, plus a machine-readable
# manifest (names, sha256, page counts, expected page-0 size) that the
# fidelity harness and CI consume.
#
# Determinism: all PDFs are generated with fixed content and no timestamps,
# using `mutool create -O reproducible` so the committed bytes stay pinned.
# Re-run idempotently to regenerate the golden baseline (hashes will change).
#
# Requires: the local MuPDF tools build (native/out/.../mutool.exe). Pass
# -MutoolPath to override.

param(
    [string]$MutoolPath = ''
)

$ErrorActionPreference = 'Continue'

if (-not $MutoolPath) {
    $candidates = Get-ChildItem -Path (Join-Path $PSScriptRoot '..\native\out') -Recurse -Filter 'mutool.exe' -ErrorAction SilentlyContinue
    if (-not $candidates) { throw 'mutool.exe not found; pass -MutoolPath' }
    $MutoolPath = $candidates | Select-Object -First 1 -ExpandProperty FullName
}

$outCorpus = Join-Path $PSScriptRoot 'sample-pdf\corpus'
$outGolden = Join-Path $PSScriptRoot 'sample-pdf\golden'
New-Item -ItemType Directory -Path $outCorpus -Force | Out-Null
New-Item -ItemType Directory -Path $outGolden -Force | Out-Null

$tmpRoot = Join-Path $env:TEMP 'pf-corpus-gen'
if (Test-Path $tmpRoot) { Remove-Item $tmpRoot -Recurse -Force }
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null

$script:manifest = New-Object System.Collections.ArrayList

function Add-Entry {
    param($Name, $FilePath, $Pages, $Page0, $Source, $Expected)
    $hash = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash
    $len = (Get-Item $FilePath).Length
    [void]$script:manifest.Add([pscustomobject]@{
        name = $Name
        sha256 = $hash
        bytes = $len
        pages = $Pages
        page0 = $Page0
        source = $Source
        expected = $Expected
    })
}

function Draw-Golden {
    param($Name, $PdfPath)
    $golden = Join-Path $outGolden ($Name.Replace('.pdf', '.p1.png'))
    & $MutoolPath draw -o $golden -r 96 $PdfPath 2>$null
    if (-not (Test-Path $golden)) { throw "golden render failed for $Name" }
}

# ---------------------------------------------------------------------------
# 1) contract-multipage.pdf -- 4-page LETTER: title page, two body pages,
#    signature page. Two fonts (Helvetica + Times-Roman). Realistic multi-page
#    legal document (each .txt below is one page).
# ---------------------------------------------------------------------------
function New-Contract {
    $c = Join-Path $tmpRoot 'contract'
    New-Item -ItemType Directory -Path $c -Force | Out-Null
    @'
%%MediaBox 0 0 612 792
%%Font F1 Helvetica Latin
%%Font F2 Times-Roman Latin
BT /F1 28 Tf 72 720 Td (PROFESSIONAL SERVICES AGREEMENT) Tj ET
BT /F2 14 Tf 72 690 Td (This Professional Services Agreement (the Agreement) is made) Tj ET
0.3 0.3 0.3 rg 72 670 468 1 re f
BT /F2 11 Tf 72 640 Td (1. SCOPE OF SERVICES. The Consultant shall provide the professional consulting,) Tj ET
BT /F2 11 Tf 72 626 Td (   analysis and advisory services described in Exhibit A (the Services). Services) Tj ET
BT /F2 11 Tf 72 612 Td (   shall be performed at the locations and on the dates mutually agreed by the) Tj ET
BT /F2 11 Tf 72 598 Td (2. COMPENSATION. Client shall pay Consultant the fees set forth in Exhibit B.) Tj ET
BT /F2 11 Tf 72 584 Td (   All invoices are payable within thirty (30) days of receipt.) Tj ET
BT /F2 11 Tf 72 545 Td (3. RELATIONSHIP OF PARTIES. Consultant is an independent contractor and not) Tj ET
BT /F2 11 Tf 72 531 Td (   an employee, agent or partner of Client. Consultant is responsible for all taxes.) Tj ET
BT /F2 11 Tf 72 492 Td (4. INSURANCE. Consultant shall maintain professional liability insurance with) Tj ET
BT /F2 11 Tf 72 478 Td (   limits of not less than one million dollars during the term of this Agreement.) Tj ET
'@ | Set-Content (Join-Path $c 'p1.txt') -Encoding Ascii
    @'
%%MediaBox 0 0 612 792
%%Font F2 Times-Roman Latin
BT /F2 11 Tf 60 760 Td (5. TERM AND TERMINATION. This Agreement commences on the Effective Date) Tj ET
BT /F2 11 Tf 60 746 Td (   and continues for twelve (12) months unless earlier terminated. Either party) Tj ET
BT /F2 11 Tf 60 732 Td (   may terminate this Agreement upon thirty (30) days written notice. Any) Tj ET
BT /F2 11 Tf 60 718 Td (   termination shall not relieve either party of obligations accrued before the) Tj ET
BT /F2 11 Tf 60 684 Td (6. INTELLECTUAL PROPERTY. All work product created under this Agreement) Tj ET
BT /F2 11 Tf 60 670 Td (   (the Work Product) shall be owned by Client upon full payment of fees. Client) Tj ET
BT /F2 11 Tf 60 656 Td (   grants Consultant a limited license to use the Work Product for its internal) Tj ET
BT /F2 11 Tf 60 622 Td (7. CONFIDENTIALITY. Each party shall hold in confidence all Confidential) Tj ET
BT /F2 11 Tf 60 608 Td (   Information of the other party as described in the NDA dated earlier. This) Tj ET
BT /F2 11 Tf 60 594 Td (   obligation survives the termination of this Agreement for a period of three) Tj ET
'@ | Set-Content (Join-Path $c 'p2a.txt') -Encoding Ascii
    @'
%%MediaBox 0 0 612 792
%%Font F2 Times-Roman Latin
BT /F2 11 Tf 330 760 Td (8.  LIMITATION OF LIABILITY. Neither party shall be liable for indirect or) Tj ET
BT /F2 11 Tf 330 746 Td (    consequential damages arising out of this Agreement. The aggregate) Tj ET
BT /F2 11 Tf 330 732 Td (    liability of either party shall not exceed the fees actually paid under this) Tj ET
BT /F2 11 Tf 330 698 Td (9.  WARRANTY. Consultant warrants that the Services will be performed in a) Tj ET
BT /F2 11 Tf 330 684 Td (    professional and workmanlike manner consistent with industry standards.) Tj ET
BT /F2 11 Tf 330 650 Td (10. GOVERNING LAW. This Agreement shall be governed by the laws of the State) Tj ET
BT /F2 11 Tf 330 636 Td (    of Delaware, without regard to conflict of law principles. Any dispute shall) Tj ET
BT /F2 11 Tf 330 622 Td (    be resolved in the state or federal courts located in Delaware.) Tj ET
BT /F2 11 Tf 330 588 Td (11. ENTIRE AGREEMENT. This Agreement constitutes the entire agreement) Tj ET
BT /F2 11 Tf 330 574 Td (    between the Parties and supersedes all prior agreements and understandings.) Tj ET
BT /F2 11 Tf 330 560 Td (11.1 NO THIRD-PARTY BENEFICIARIES. This Agreement is for the sole) Tj ET
BT /F2 11 Tf 330 546 Td (    benefit of the Parties and their permitted assigns.) Tj ET
'@ | Set-Content (Join-Path $c 'p2b.txt') -Encoding Ascii
    @'
%%MediaBox 0 0 612 792
%%Font F1 Helvetica Latin
%%Font F2 Times-Roman Latin
BT /F1 24 Tf 72 720 Td (SIGNATURE PAGE) Tj ET
BT /F2 11 Tf 72 660 Td (IN WITNESS WHEREOF, the Parties have executed this Agreement by their) Tj ET
BT /F2 11 Tf 72 646 Td (duly authorized representatives as of the Effective Date.) Tj ET
BT /F2 12 Tf 72 580 Td (CLIENT: ____________________________________) Tj ET
BT /F2 12 Tf 72 556 Td (Name: _______________________________ Date: ________) Tj ET
BT /F2 12 Tf 72 480 Td (CONSULTANT: ________________________________) Tj ET
BT /F2 12 Tf 72 456 Td (Name: _______________________________ Date: ________) Tj ET
'@ | Set-Content (Join-Path $c 'p3.txt') -Encoding Ascii
    $out = Join-Path $outCorpus 'contract-multipage.pdf'
    & $MutoolPath create -O reproducible,garbage -o $out `
        (Join-Path $c 'p1.txt') (Join-Path $c 'p2a.txt') (Join-Path $c 'p2b.txt') (Join-Path $c 'p3.txt') 2>$null
    return $out
}

# ---------------------------------------------------------------------------
# 2) form-application.pdf -- 1-page LETTER AcroForm (text field + checkbox).
#    Hand-rolled writer because mutool create has no widget/field DSL.
# ---------------------------------------------------------------------------
function New-Form {
    $content =
        "BT /F1 18 Tf 72 740 Td (Employment Application) Tj ET`n" +
        "0.2 0.2 0.2 rg 72 700 468 1 re f`n" +
        "BT /F1 12 Tf 72 660 Td (Full name:) Tj ET`n" +
        "BT /F1 12 Tf 72 600 Td (I consent to a background check:) Tj ET`n"
    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>',
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
        "<< /Length $($content.Length) >>`nstream`n$content`nendstream",
        '<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /Rect [170 650 420 672] /BS << /W 1 /S /S >> /P 3 0 R >>',
        '<< /Type /Annot /Subtype /Widget /FT /Btn /T (Consent) /V /Off /AS /Off /Rect [170 590 190 610] /BS << /W 1 /S /S >> /P 3 0 R >>',
        '<< /Fields [6 0 R 7 0 R] /NeedAppearances true /DR << /Font << /F1 4 0 R >> >> >>'
    )
    $bytes = New-Object System.Collections.Generic.List[byte]
    $offsets = New-Object 'System.Collections.Generic.List[int]'

    function Append-Bytes { param([string]$s) foreach ($b in [System.Text.Encoding]::ASCII.GetBytes($s)) { $bytes.Add($b) } }

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
    $out = Join-Path $outCorpus 'form-application.pdf'
    [System.IO.File]::WriteAllBytes($out, $bytes.ToArray())
    return $out
}

# ---------------------------------------------------------------------------
# 3) scan-letters.pdf -- 2-page raster/scan proxy (image-only pages). Two
#    source "scans" are derived by rendering a couple of labelled pages, then
#    embedded as full-page images so the PDF is image-only (a scan stand-in).
# ---------------------------------------------------------------------------
function New-Scan {
    $s = Join-Path $tmpRoot 'scan'
    New-Item -ItemType Directory -Path $s -Force | Out-Null
    $srcA = Join-Path $s 'srca.txt'
    $srcB = Join-Path $s 'srcb.txt'
    @"
%%MediaBox 0 0 612 792
%%Font F1 Helvetica Latin
BT /F1 16 Tf 72 700 Td (SCANNED PAGE ONE) Tj ET
1 0 0 rg 72 600 300 200 re f
0 0 1 rg 120 580 60 30 re f
"@ | Set-Content $srcA -Encoding Ascii
    @"
%%MediaBox 0 0 612 792
%%Font F1 Helvetica Latin
BT /F1 16 Tf 72 700 Td (SCANNED PAGE TWO) Tj ET
0 1 0 rg 200 500 250 150 re f
1 1 0 rg 240 480 60 30 re f
"@ | Set-Content $srcB -Encoding Ascii
    $pageA = Join-Path $s 'srca.pdf'
    $pageB = Join-Path $s 'srcb.pdf'
    & $MutoolPath create -O reproducible -o $pageA $srcA 2>$null
    & $MutoolPath create -O reproducible -o $pageB $srcB 2>$null
    $imgA = Join-Path $s 'scan1.png'
    $imgB = Join-Path $s 'scan2.png'
    & $MutoolPath draw -o $imgA -r 150 $pageA 2>$null
    & $MutoolPath draw -o $imgB -r 150 $pageB 2>$null
    # Image pages: full-page image, no text.
    $ctA = Join-Path $s 'img1.txt'
    $ctB = Join-Path $s 'img2.txt'
    @"
%%MediaBox 0 0 612 792
%%Image IMG $imgA
q 612 0 0 792 0 0 cm /IMG Do Q
"@ | Set-Content $ctA -Encoding Ascii
    @"
%%MediaBox 0 0 612 792
%%Image IMG $imgB
q 612 0 0 792 0 0 cm /IMG Do Q
"@ | Set-Content $ctB -Encoding Ascii
    $out = Join-Path $outCorpus 'scan-letters.pdf'
    & $MutoolPath create -O reproducible,garbage -o $out $ctA $ctB 2>$null
    # Image streams are embedded raw (huge); compress them via clean -z -i
    # (deterministic). Also regenerate goldens from the compressed doc.
    $raw = Join-Path $s 'scan-raw.pdf'
    Copy-Item $out $raw -Force
    & $MutoolPath clean -z -i -g $raw $out 2>$null
    return $out
}

# ---------------------------------------------------------------------------
# 4) unicode-multilingual.pdf -- 2-page LETTER with Latin-1 accented text
#    (French, Spanish, German). WinAnsi bytes written directly.
# ---------------------------------------------------------------------------
function New-Unicode {
    $u = Join-Path $tmpRoot 'unicode'
    New-Item -ItemType Directory -Path $u -Force | Out-Null
    # WinAnsi/Latin-1 accents: e-acute E9, e-grave E8, c-cedilla E7, u-umlaut FC, n-tilde F1
    $chars = @{
        eacute = [char]0xE9   # é
        egrave = [char]0xE8   # è
        ccedil = [char]0xE7   # ç
        uuml   = [char]0xFC   # ü
        ntilde = [char]0xF1   # ñ
        agrave = [char]0xE0   # à
    }
    $s1 = "BT /F1 20 Tf 72 720 Td (Bienvenu$($chars.eacute) a l'$($chars.egrave)quipe) Tj ET`n" +
          "BT /F1 14 Tf 72 640 Td (Nuit noire, sur le chemin, l'enfant marchait.) Tj ET`n" +
          "BT /F1 14 Tf 72 620 Td (Fran$($chars.ccedil)ais, espagnol, allemand: un dossier) Tj ET`n"
    $s2 = "BT /F1 20 Tf 72 720 Td (El a$($chars.ntilde)o pr$($chars.agrave)ctico en M$($chars.uuml)nchen) Tj ET`n" +
          "BT /F1 14 Tf 72 640 Td (F$($chars.uuml)r die Bearbeitung der Vertr$($chars.agrave)ge) Tj ET`n" +
          "BT /F1 14 Tf 72 620 Td (La r$($chars.eacute)union a commenc$($chars.eacute) hier soir.) Tj ET`n"
    $f1 = Join-Path $u 'p1.txt'
    $f2 = Join-Path $u 'p2.txt'
    function Write-Bytes([string]$path, [string]$content) {
        $b = New-Object System.Collections.Generic.List[byte]
        foreach ($ch in $content.ToCharArray()) {
            $b.Add([byte]$ch) # all chars here <= 0xFF
        }
        $header = "%%MediaBox 0 0 612 792`n%%Font F1 Helvetica Latin`n"
        $out = New-Object System.Collections.Generic.List[byte]
        foreach ($x in ([System.Text.Encoding]::ASCII.GetBytes($header))) { $out.Add($x) }
        $out.AddRange($b)
        [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    }
    Write-Bytes $f1 $s1
    Write-Bytes $f2 $s2
    $out = Join-Path $outCorpus 'unicode-multilingual.pdf'
    & $MutoolPath create -O reproducible,garbage -o $out $f1 $f2 2>$null
    return $out
}

# ---------------------------------------------------------------------------
# Build all four documents, draw goldens, emit manifest.
# ---------------------------------------------------------------------------
$contract = New-Contract
$form     = New-Form
$scan     = New-Scan
$unicode  = New-Unicode

Add-Entry -Name 'contract-multipage.pdf'  -FilePath $contract -Pages 4 -Page0 '612x792' -Source 'tools/generate-corpus.ps1 (realistic multi-page contract)' -Expected '4 pages, 612x792pt, title + body + signature pages'
Add-Entry -Name 'form-application.pdf'    -FilePath $form     -Pages 1 -Page0 '612x792' -Source 'tools/generate-corpus.ps1 (AcroForm text field + checkbox)' -Expected '1 page, 612x792pt, with AcroForm fields FullName + Consent'
Add-Entry -Name 'scan-letters.pdf'        -FilePath $scan     -Pages 2 -Page0 '612x792' -Source 'tools/generate-corpus.ps1 (raster scan proxy, image-only pages)' -Expected '2 pages, 612x792pt, image-only raster pages'
Add-Entry -Name 'unicode-multilingual.pdf' -FilePath $unicode  -Pages 2 -Page0 '612x792' -Source 'tools/generate-corpus.ps1 (Latin-1 accented text)' -Expected '2 pages, 612x792pt, Latin-1 accented text'

foreach ($e in $script:manifest) { Draw-Golden -Name $e.name -PdfPath (Join-Path $outCorpus $e.name) }

# Manifest (ordered), printed for the fidelity harness to pin.
$manifestPath = Join-Path $env:TEMP 'pf-corpus-manifest.txt'
$script:manifest | Format-List | Out-String | Set-Content $manifestPath
Write-Host "Corpus written to $outCorpus"
Write-Host "Goldens written to $outGolden"
foreach ($e in $script:manifest) {
    Write-Host ("{0}: sha256={1} bytes={2} pages={3} page0={4}" -f $e.name, $e.sha256, $e.bytes, $e.pages, $e.page0)
}
