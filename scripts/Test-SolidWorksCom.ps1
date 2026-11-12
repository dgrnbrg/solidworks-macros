<#
.SYNOPSIS
Builds/updates ExportFacesToDXF.swp (unless -SkipBuild) and runs live COM compile + DXF export tests in SOLIDWORKS.

.DESCRIPTION
Connects to a running SOLIDWORKS session via COM (SldWorks.Application), runs:
  1. Full VBA project compile verification via ISldWorks::RunMacro2 (CompileCheckOnly).
  2. End-to-end DXF export verification on a temporary 2-level pocketed test part:
     - Mode 1 (Smart CAM Grouping) with pocket overcut = 0.25in
     - Mode 3 (Separate DXF Per Face) with pocket overcut = 6mm
  3. Automated DXF geometry & layer assertions on the exported DXF files.

Safe to run alongside open user models: creates and closes a dedicated temporary .SLDPRT document
in %TEMP% without modifying any open user documents.

.PARAMETER SwpPath
Path to the compiled .swp macro file. Defaults to <repo>\ExportFacesToDXF.swp.

.PARAMETER SkipBuild
Skip rebuilding ExportFacesToDXF.swp from ExportFacesToDXF.bas before testing.

.PARAMETER LaunchIfNotRunning
If SOLIDWORKS is not currently registered in the COM Running Object Table, attempt to start it via CreateInstance.

.PARAMETER KeepArtifacts
Keep the temporary part and exported DXF test folder in %TEMP% after completion for inspection.
#>
[CmdletBinding()]
param(
    [string]$SwpPath,
    [switch]$SkipBuild,
    [switch]$LaunchIfNotRunning,
    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SwpPath)) {
    $SwpPath = Join-Path $repoRoot "ExportFacesToDXF.swp"
}

if (-not $SkipBuild) {
    $buildScript = Join-Path $PSScriptRoot "Build-Swp.ps1"
    & $buildScript -OutputSwp $SwpPath
}

if (-not (Test-Path -LiteralPath $SwpPath)) {
    throw "Macro .swp file not found: $SwpPath"
}
$SwpPath = (Resolve-Path -LiteralPath $SwpPath).Path

# Check VBA Compile On Demand setting
$vbaReg = "HKCU:\Software\Microsoft\VBA\7.1\Common"
if (Test-Path $vbaReg) {
    $cod = (Get-ItemProperty -Path $vbaReg -Name "CompileOnDemand" -ErrorAction SilentlyContinue).CompileOnDemand
    if ($cod -eq 1) {
        Write-Warning "VBA 'Compile On Demand' is enabled (HKCU\Software\Microsoft\VBA\7.1\Common\CompileOnDemand = 1). Uncheck 'Compile On Demand' in SOLIDWORKS VBE (Alt+F11 -> Tools -> Options -> General) so CompileCheckOnly compiles all procedures eagerly."
    } else {
        Write-Host "[INFO] VBA CompileOnDemand = $cod (eager full-project compilation active)."
    }
}

# Attach to SOLIDWORKS via COM
$swApp = $null
try {
    $swApp = [Runtime.InteropServices.Marshal]::GetActiveObject("SldWorks.Application")
} catch {
    if ($LaunchIfNotRunning) {
        Write-Host "[INFO] No active SldWorks.Application in ROT; starting via COM CreateInstance..."
        $swType = [Type]::GetTypeFromProgID("SldWorks.Application")
        if ($null -eq $swType) {
            throw "SldWorks.Application ProgID is not registered on this machine."
        }
        $swApp = [Activator]::CreateInstance($swType)
        $swApp.Visible = $true
    } else {
        throw "Could not attach to running SldWorks.Application via COM ($($_.Exception.Message)). Ensure SOLIDWORKS is open on the same desktop/user session (not Run As Administrator mismatch), or pass -LaunchIfNotRunning."
    }
}

$rev = $swApp.RevisionNumber()
Write-Host "[OK] Connected to SOLIDWORKS Revision $rev"

function Invoke-MacroSub {
    param(
        [Parameter(Mandatory=$true)]$App,
        [Parameter(Mandatory=$true)][string]$MacroPath,
        [Parameter(Mandatory=$true)][string]$ModuleName,
        [Parameter(Mandatory=$true)][string]$ProcName
    )
    [int]$errCode = 0
    # swRunMacroUnloadAfterRun = 1
    $ok = $App.RunMacro2($MacroPath, $ModuleName, $ProcName, 1, [ref]$errCode)
    if (-not $ok -or $errCode -ne 0) {
        $hint = switch ($errCode) {
            2 { "Macros are disabled in SOLIDWORKS (swRunMacroError_MacrosAreDisabled)." }
            6 { "Procedure '$ModuleName.$ProcName' not found (swRunMacroError_InvalidProcname). Rebuild .swp with Build-Swp.ps1." }
            default { "RunMacro2 returned ok=$ok errCode=$errCode." }
        }
        throw "RunMacro2 failed for ${ModuleName}.${ProcName}: $hint"
    }
}

function Parse-DxfLwPolylines {
    param([Parameter(Mandatory=$true)][string]$DxfPath)
    $lines = Get-Content -LiteralPath $DxfPath
    $polys = @()
    $insUnits = $null

    for ($i = 0; $i + 1 -lt $lines.Count; $i += 2) {
        $code = $lines[$i].Trim()
        $val = $lines[$i + 1].Trim()
        if ($code -eq "9" -and $val -eq '$INSUNITS' -and ($i + 3) -lt $lines.Count) {
            $insUnits = $lines[$i + 3].Trim()
        }
        if ($code -eq "0" -and $val -eq "LWPOLYLINE") {
            $layer = ""
            $flags = 0
            $xs = New-Object System.Collections.Generic.List[double]
            $ys = New-Object System.Collections.Generic.List[double]
            $j = $i + 2
            while ($j + 1 -lt $lines.Count) {
                $ec = $lines[$j].Trim()
                $ev = $lines[$j + 1].Trim()
                if ($ec -eq "0") { break }
                if ($ec -eq "8") { $layer = $ev }
                elseif ($ec -eq "70") { $flags = [int]$ev }
                elseif ($ec -eq "10") { $xs.Add([double]::Parse($ev, [Globalization.CultureInfo]::InvariantCulture)) }
                elseif ($ec -eq "20") { $ys.Add([double]::Parse($ev, [Globalization.CultureInfo]::InvariantCulture)) }
                $j += 2
            }
            $polys += [PSCustomObject]@{
                Layer = $layer
                Flags = $flags
                Xs    = $xs.ToArray()
                Ys    = $ys.ToArray()
            }
        }
    }
    return [PSCustomObject]@{
        InsUnits    = $insUnits
        LwPolylines = $polys
    }
}

# Step 1: Compile verification
Write-Host "=== Step 1: Compile Check (CompileCheckOnly) ==="
$compileFlagPath = Join-Path ([IO.Path]::GetTempPath()) "ExportFacesToDXF_compile_ok.txt"
if (Test-Path -LiteralPath $compileFlagPath) { Remove-Item -LiteralPath $compileFlagPath -Force }

Invoke-MacroSub -App $swApp -MacroPath $SwpPath -ModuleName "ExportFacesToDXF1" -ProcName "CompileCheckOnly"

if (-not (Test-Path -LiteralPath $compileFlagPath)) {
    throw "CompileCheckOnly did not produce sentinel file '$compileFlagPath'. VBA compilation likely failed."
}
$compileText = (Get-Content -LiteralPath $compileFlagPath -Raw).Trim()
if ($compileText -ne "OK|COMPILED") {
    throw "Unexpected CompileCheckOnly output: $compileText"
}
Write-Host "[PASS] CompileCheckOnly succeeded ($compileText)."

# Step 2: End-to-End Stepped Part Export Verification
Write-Host "=== Step 2: End-to-End Stepped Part DXF Export Verification ==="
$workDir = Join-Path ([IO.Path]::GetTempPath()) ("sw_dxf_com_test_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$prevActivePath = $null
try {
    if ($null -ne $swApp.ActiveDoc) {
        $prevActivePath = $swApp.ActiveDoc.GetTitle()
    }

    # swDefaultTemplatePart = 8
    $partTemplate = $swApp.GetUserPreferenceStringValue(8)
    if ([string]::IsNullOrWhiteSpace($partTemplate) -or -not (Test-Path -LiteralPath $partTemplate)) {
        throw "Could not locate SOLIDWORKS default part template: '$partTemplate'"
    }

    $doc = $swApp.NewDocument($partTemplate, 0, 0, 0)
    if ($null -eq $doc) {
        throw "Failed to create temporary SOLIDWORKS Part document from '$partTemplate'."
    }
    $docTitle = $doc.GetTitle()

    try {
        # Force IPS units (inches) on the test part: swUnitSystem = 263, swUnitSystem_IPS = 3
        $doc.SetUserPreferenceIntegerValue(263, 3) | Out-Null

        # Select first RefPlane cleanly (language-independent)
        $feat = $doc.FirstFeature()
        $topPlane = $null
        while ($null -ne $feat) {
            if ($feat.GetTypeName2() -eq "RefPlane") {
                # Prefer 2nd RefPlane (typically Top Plane) if available, otherwise 1st
                $topPlane = $feat
                if ($feat.Name -match "Top") { break }
            }
            $feat = $feat.GetNextFeature()
        }
        if ($null -eq $topPlane) {
            throw "No RefPlane found in new part template."
        }

        $topPlane.Select2($false, 0) | Out-Null
        $skMgr = $doc.SketchManager
        $skMgr.AddToDB = $true
        $skMgr.InsertSketch($true)
        # 4 in x 2 in block: 0..0.1016 m x 0..0.0508 m
        $skMgr.CreateCornerRectangle(0.0, 0.0, 0.0, 0.1016, 0.0508, 0.0) | Out-Null
        $skMgr.AddToDB = $false
        $skMgr.InsertSketch($true)

        $featMgr = $doc.FeatureManager
        # Extrude 1.0 in (0.0254 m), swEndCondBlind = 0
        $extrude = $featMgr.FeatureExtrusion2(
            $true, $false, $false, 0, 0, 0.0254, 0.0,
            $false, $false, $false, $false, 0.0, 0.0,
            $false, $false, $false, $false, $true, $true, $true,
            0, 0.0, $false
        )
        if ($null -eq $extrude) { throw "Base FeatureExtrusion2 failed." }

        # Find the upper cap face created by the extrusion (normal aligned with +Z in sketch space, highest center point)
        $body = ($doc.GetBodies2(0, $true))[0]
        $faces = $body.GetFaces()
        $topCap = $null
        $bestProj = -1e9
        $extrudeNorm = $null
        foreach ($f in $faces) {
            $surf = $f.GetSurface()
            if (-not $surf.IsPlane()) { continue }
            $n = $f.Normal
            # Pick face from extrusion cap with largest area among planes if top plane normal unknown
            $area = $f.GetArea()
            if ($null -eq $topCap -or $area -gt $bestProj) {
                # All faces of 4x2x1 box: top & bottom caps have largest area (8 sq in vs 4 and 2 sq in)
                $bestProj = $area
                $topCap = $f
                $extrudeNorm = $n
            }
        }
        if ($null -eq $topCap) { throw "Failed to locate base extrusion cap face." }

        $doc.ClearSelection2($true)
        $topCap.Select4($false, $null) | Out-Null
        $skMgr.AddToDB = $true
        $skMgr.InsertSketch($true)
        # Pocket half of top face: 2 in x 2 in rectangle on one end
        $skMgr.CreateCornerRectangle(0.0, 0.0, 0.0, 0.0508, 0.0508, 0.0) | Out-Null
        $skMgr.AddToDB = $false
        $skMgr.InsertSketch($true)

        # Cut 0.5 in (0.0127 m) blind into the cap face
        $cutFeat = $featMgr.FeatureCut3(
            $true, $false, $false, 0, 0, 0.0127, 0.0,
            $false, $false, $false, $false, 0.0, 0.0,
            $false, $false, $false, $false, $false, $true, $true,
            $false, $false, $false, 0, 0.0, $false
        )
        if ($null -eq $cutFeat) { throw "Pocket FeatureCut3 failed." }

        $partPath = Join-Path $workDir "SteppedPocketTest.SLDPRT"
        $doc.SaveAs3($partPath, 0, 1) | Out-Null

        # Select the 2 parallel faces matching extrudeNorm (upper flange + pocket floor)
        $body = ($doc.GetBodies2(0, $true))[0]
        $faces = $body.GetFaces()
        $selectedFaces = @()
        foreach ($f in $faces) {
            $surf = $f.GetSurface()
            if (-not $surf.IsPlane()) { continue }
            $n = $f.Normal
            $dot = ($n[0] * $extrudeNorm[0]) + ($n[1] * $extrudeNorm[1]) + ($n[2] * $extrudeNorm[2])
            if ($dot -gt 0.9999) {
                $selectedFaces += $f
            }
        }
        if ($selectedFaces.Count -lt 2) {
            throw "Expected at least 2 parallel stepped faces, found $($selectedFaces.Count)."
        }

        function Select-TestFaces {
            param($TargetDoc, $Faces)
            $TargetDoc.ClearSelection2($true)
            foreach ($sf in $Faces) {
                $sf.Select4($true, $null) | Out-Null
            }
        }

        $cfgPath = Join-Path ([IO.Path]::GetTempPath()) "ExportFacesToDXF_test.cfg"
        $resPath = Join-Path $workDir "macro_result.txt"

        # --- Test Case A: Mode 1 with overcut=0.25in ---
        $mode1Dir = Join-Path $workDir "out_mode1"
        New-Item -ItemType Directory -Path $mode1Dir -Force | Out-Null
        Select-TestFaces -TargetDoc $doc -Faces $selectedFaces
        Set-Content -LiteralPath $cfgPath -Value @(
            "mode=1",
            "overcut=0.25in",
            "folder=$mode1Dir",
            "result=$resPath"
        ) -Encoding ASCII
        if (Test-Path -LiteralPath $resPath) { Remove-Item -LiteralPath $resPath -Force }

        Invoke-MacroSub -App $swApp -MacroPath $SwpPath -ModuleName "ExportFacesToDXF1" -ProcName "RunAutomatedFromTempConfig"

        if (-not (Test-Path -LiteralPath $resPath)) { throw "Mode 1 headless macro wrote no result file." }
        $res1 = (Get-Content -LiteralPath $resPath -Raw).Trim()
        if (-not $res1.StartsWith("OK|")) { throw "Mode 1 macro failed: $res1" }

        $dxf1Files = @(Get-ChildItem -LiteralPath $mode1Dir -Filter "*.dxf")
        if ($dxf1Files.Count -ne 1) { throw "Expected 1 DXF in Mode 1, got $($dxf1Files.Count)." }
        $parsed1 = Parse-DxfLwPolylines -DxfPath $dxf1Files[0].FullName

        $outline1 = @($parsed1.LwPolylines | Where-Object { $_.Layer -eq "OUTLINE" })
        $facePolys1 = @($parsed1.LwPolylines | Where-Object { $_.Layer -like "FACE_*" })
        if ($outline1.Count -eq 0) { throw "Mode 1 DXF missing OUTLINE LWPOLYLINE." }
        if ($facePolys1.Count -lt 2) { throw "Mode 1 DXF expected >=2 FACE_* LWPOLYLINEs, got $($facePolys1.Count)." }
        foreach ($p in ($outline1 + $facePolys1)) {
            if (($p.Flags -band 1) -ne 1) { throw "Unclosed LWPOLYLINE on layer $($p.Layer) in $($dxf1Files[0].Name)." }
        }

        # Verify OUTLINE span equals 4.0 x 2.0 in (within tolerance) while at least one FACE_* extends by >= 0.24 in
        $oxMin = ($outline1[0].Xs | Measure-Object -Minimum).Minimum
        $oxMax = ($outline1[0].Xs | Measure-Object -Maximum).Maximum
        $oyMin = ($outline1[0].Ys | Measure-Object -Minimum).Minimum
        $oyMax = ($outline1[0].Ys | Measure-Object -Maximum).Maximum
        $fxMin = ($facePolys1 | ForEach-Object { $_.Xs } | Measure-Object -Minimum).Minimum
        $fxMax = ($facePolys1 | ForEach-Object { $_.Xs } | Measure-Object -Maximum).Maximum

        if ([Math]::Abs(($oxMax - $oxMin) - 4.0) -gt 0.01 -or [Math]::Abs(($oyMax - $oyMin) - 2.0) -gt 0.01) {
            throw "Unexpected OUTLINE dimensions in Mode 1 DXF: X=[$oxMin,$oxMax] Y=[$oyMin,$oyMax]"
        }
        if ($fxMin -gt ($oxMin - 0.24) -and $fxMax -lt ($oxMax + 0.24)) {
            throw "Expected Mode 1 0.25in overcut extension past OUTLINE [$oxMin..$oxMax], got face X=[$fxMin..$fxMax]."
        }
        Write-Host "[PASS] Mode 1 (0.25in overcut): $($dxf1Files[0].Name) OUTLINE=[$oxMin..$oxMax x $oyMin..$oyMax], FACE_X=[$fxMin..$fxMax]"

        # --- Test Case B: Mode 3 with overcut=6mm ---
        $mode3Dir = Join-Path $workDir "out_mode3"
        New-Item -ItemType Directory -Path $mode3Dir -Force | Out-Null
        Select-TestFaces -TargetDoc $doc -Faces $selectedFaces
        Set-Content -LiteralPath $cfgPath -Value @(
            "mode=3",
            "overcut=6mm",
            "folder=$mode3Dir",
            "result=$resPath"
        ) -Encoding ASCII
        if (Test-Path -LiteralPath $resPath) { Remove-Item -LiteralPath $resPath -Force }

        Invoke-MacroSub -App $swApp -MacroPath $SwpPath -ModuleName "ExportFacesToDXF1" -ProcName "RunAutomatedFromTempConfig"

        if (-not (Test-Path -LiteralPath $resPath)) { throw "Mode 3 headless macro wrote no result file." }
        $res3 = (Get-Content -LiteralPath $resPath -Raw).Trim()
        if (-not $res3.StartsWith("OK|")) { throw "Mode 3 macro failed: $res3" }

        $dxf3Files = @(Get-ChildItem -LiteralPath $mode3Dir -Filter "*.dxf")
        if ($dxf3Files.Count -ne 2) { throw "Expected 2 DXFs in Mode 3, got $($dxf3Files.Count)." }
        foreach ($df in $dxf3Files) {
            $parsed3 = Parse-DxfLwPolylines -DxfPath $df.FullName
            $f3 = @($parsed3.LwPolylines | Where-Object { $_.Layer -like "FACE_*" })
            if ($f3.Count -eq 0) { throw "Mode 3 DXF $($df.Name) missing FACE_* LWPOLYLINE." }
        }
        Write-Host "[PASS] Mode 3 (6mm overcut): produced $($dxf3Files.Count) DXFs cleanly."
        Write-Host ""
        Write-Host "[SUCCESS] All SOLIDWORKS COM compile and live DXF export tests passed!"
    } finally {
        $swApp.CloseDoc($docTitle)
    }
} finally {
    $cfgTemp = Join-Path ([IO.Path]::GetTempPath()) "ExportFacesToDXF_test.cfg"
    if (-not $KeepArtifacts) {
        if (Test-Path -LiteralPath $cfgTemp) { Remove-Item -LiteralPath $cfgTemp -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
    } else {
        Write-Host "[INFO] Kept temporary test artifacts at: $workDir"
    }
    if ($null -ne $prevActivePath) {
        [int]$actErr = 0
        try { $swApp.ActivateDoc3($prevActivePath, $false, 0, [ref]$actErr) | Out-Null } catch {}
    }
}
