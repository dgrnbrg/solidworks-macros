<#
.SYNOPSIS
Builds/updates ExportFacesToDXF.swp (unless -SkipBuild) and runs live COM compile + DXF export tests in SOLIDWORKS.

.DESCRIPTION
Connects to a running SOLIDWORKS session via early-bound SolidWorks.Interop.sldworks.dll COM interfaces
(avoiding IDispatch::GetTypeInfo TypeLib 1.0/win64 TYPE_E_ELEMENTNOTFOUND issues), and runs:
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

# Locate SolidWorks.Interop.sldworks.dll from registered SldWorks.Application LocalServer32
$interopDll = $null
$swType = [Type]::GetTypeFromProgID("SldWorks.Application")
if ($null -ne $swType) {
    $clsidKey = "Registry::HKEY_CLASSES_ROOT\CLSID\{$($swType.GUID)}\LocalServer32"
    if (Test-Path -LiteralPath $clsidKey) {
        $exePath = (Get-ItemProperty -LiteralPath $clsidKey).'(default)'
        if (-not [string]::IsNullOrWhiteSpace($exePath)) {
            $exePath = $exePath.Trim('"')
            $swDir = Split-Path -Parent $exePath
            $candidate = Join-Path $swDir "api\redist\SolidWorks.Interop.sldworks.dll"
            if (Test-Path -LiteralPath $candidate) {
                $interopDll = $candidate
            }
        }
    }
}
if ($null -eq $interopDll) {
    $fallbackRoots = @(
        "C:\Program Files\Dassault Systemes",
        "C:\Program Files\SOLIDWORKS Corp"
    )
    foreach ($root in $fallbackRoots) {
        if (Test-Path -LiteralPath $root) {
            $found = Get-ChildItem -LiteralPath $root -Filter "SolidWorks.Interop.sldworks.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $found) {
                $interopDll = $found.FullName
                break
            }
        }
    }
}
if ($null -eq $interopDll) {
    throw "Could not locate SolidWorks.Interop.sldworks.dll on this machine."
}

$csharpRunner = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

public static class SwComTestRunner
{
    private static ISldWorks _swApp;

    public static string Connect(bool launchIfNotRunning)
    {
        object raw = null;
        try
        {
            raw = Marshal.GetActiveObject("SldWorks.Application");
        }
        catch (COMException)
        {
            if (!launchIfNotRunning)
            {
                throw;
            }
        }

        if (raw == null && launchIfNotRunning)
        {
            Type t = Type.GetTypeFromProgID("SldWorks.Application");
            if (t == null) throw new InvalidOperationException("SldWorks.Application ProgID is not registered.");
            raw = Activator.CreateInstance(t);
            ISldWorks created = (ISldWorks)raw;
            created.Visible = true;
            _swApp = created;
            return _swApp.RevisionNumber();
        }

        _swApp = (ISldWorks)raw;
        return _swApp.RevisionNumber();
    }

    public static void InvokeMacro(string swpPath, string moduleName, string procName)
    {
        int errCode = 0;
        // 2 = swRunMacroUnloadAfterRun (prevents SOLIDWORKS from holding the .swp open/locked)
        bool ok = _swApp.RunMacro2(swpPath, moduleName, procName, 2, out errCode);
        if (!ok || errCode != 0)
        {
            string hint;
            if (errCode == 2)
                hint = "Macros are disabled in SOLIDWORKS (swRunMacroError_MacrosAreDisabled).";
            else if (errCode == 6)
                hint = "Procedure '" + moduleName + "." + procName + "' not found (swRunMacroError_InvalidProcname). Rebuild .swp with Build-Swp.ps1.";
            else
                hint = "RunMacro2 returned ok=" + ok + " errCode=" + errCode + ".";
            throw new InvalidOperationException("RunMacro2 failed for " + moduleName + "." + procName + ": " + hint);
        }
    }

    public static string RunExportTests(string swpPath, string workDir, string mode1Dir, string mode3Dir, string cfgPath, string resPath)
    {
        ISldWorks swApp = _swApp;
        string prevTitle = null;
        IModelDoc2 active = (IModelDoc2)swApp.ActiveDoc;
        if (active != null)
        {
            prevTitle = active.GetTitle();
        }

        // swDefaultTemplatePart = 8
        string partTemplate = swApp.GetUserPreferenceStringValue(8);
        if (string.IsNullOrEmpty(partTemplate) || !File.Exists(partTemplate))
        {
            throw new InvalidOperationException("Could not locate SOLIDWORKS default part template: '" + partTemplate + "'");
        }

        IModelDoc2 doc = (IModelDoc2)swApp.NewDocument(partTemplate, 0, 0.0, 0.0);
        if (doc == null)
        {
            throw new InvalidOperationException("Failed to create temporary SOLIDWORKS Part document from '" + partTemplate + "'.");
        }
        string docTitle = doc.GetTitle();

        try
        {
            // Force IPS units (inches): swUnitSystem = 263, swUnitSystem_IPS = 3
            doc.SetUserPreferenceIntegerValue(263, 3);

            IFeature feat = (IFeature)doc.FirstFeature();
            IFeature topPlane = null;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase))
                {
                    topPlane = feat;
                    if (feat.Name != null && feat.Name.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        break;
                    }
                }
                feat = (IFeature)feat.GetNextFeature();
            }
            if (topPlane == null)
            {
                throw new InvalidOperationException("No RefPlane found in new part template.");
            }

            topPlane.Select2(false, 0);
            ISketchManager skMgr = doc.SketchManager;
            skMgr.AddToDB = true;
            skMgr.InsertSketch(true);
            // 4 in x 2 in block: 0..0.1016 m x 0..0.0508 m
            skMgr.CreateCornerRectangle(0.0, 0.0, 0.0, 0.1016, 0.0508, 0.0);
            skMgr.AddToDB = false;
            skMgr.InsertSketch(true);

            IFeatureManager featMgr = doc.FeatureManager;
            // Extrude 1.0 in (0.0254 m), swEndCondBlind = 0
            IFeature extrude = (IFeature)featMgr.FeatureExtrusion2(
                true, false, false, 0, 0, 0.0254, 0.0,
                false, false, false, false, 0.0, 0.0,
                false, false, false, false, true, true, true,
                0, 0.0, false);
            if (extrude == null) throw new InvalidOperationException("Base FeatureExtrusion2 failed.");

            IPartDoc part = (IPartDoc)doc;
            object[] bodiesObj = (object[])part.GetBodies2(0, true);
            IBody2 body = (IBody2)bodiesObj[0];
            object[] facesObj = (object[])body.GetFaces();

            IFace2 topCap = null;
            double bestArea = -1.0;
            double[] extrudeNorm = null;
            foreach (object fo in facesObj)
            {
                IFace2 f = (IFace2)fo;
                ISurface surf = (ISurface)f.GetSurface();
                if (!surf.IsPlane()) continue;
                double area = f.GetArea();
                if (topCap == null || area > bestArea)
                {
                    bestArea = area;
                    topCap = f;
                    extrudeNorm = (double[])f.Normal;
                }
            }
            if (topCap == null) throw new InvalidOperationException("Failed to locate base extrusion cap face.");

            doc.ClearSelection2(true);
            ((IEntity)topCap).Select4(false, null);
            skMgr.AddToDB = true;
            skMgr.InsertSketch(true);
            // Pocket half of top face: 2 in x 2 in rectangle on one end
            skMgr.CreateCornerRectangle(0.0, 0.0, 0.0, 0.0508, 0.0508, 0.0);
            skMgr.AddToDB = false;
            skMgr.InsertSketch(true);

            // Cut 0.5 in (0.0127 m) blind into the cap face
            IFeature cutFeat = (IFeature)featMgr.FeatureCut3(
                true, false, false, 0, 0, 0.0127, 0.0,
                false, false, false, false, 0.0, 0.0,
                false, false, false, false, false, true, true,
                false, false, false, 0, 0.0, false);
            if (cutFeat == null) throw new InvalidOperationException("Pocket FeatureCut3 failed.");

            string partPath = Path.Combine(workDir, "SteppedPocketTest.SLDPRT");
            doc.SaveAs3(partPath, 0, 1);

            bodiesObj = (object[])part.GetBodies2(0, true);
            body = (IBody2)bodiesObj[0];
            facesObj = (object[])body.GetFaces();
            List<IFace2> selectedFaces = new List<IFace2>();
            foreach (object fo in facesObj)
            {
                IFace2 f = (IFace2)fo;
                ISurface surf = (ISurface)f.GetSurface();
                if (!surf.IsPlane()) continue;
                double[] n = (double[])f.Normal;
                double dot = (n[0] * extrudeNorm[0]) + (n[1] * extrudeNorm[1]) + (n[2] * extrudeNorm[2]);
                if (dot > 0.9999)
                {
                    selectedFaces.Add(f);
                }
            }
            if (selectedFaces.Count < 2)
            {
                throw new InvalidOperationException("Expected at least 2 parallel stepped faces, found " + selectedFaces.Count + ".");
            }

            // Test Case A: Mode 1 with overcut=0.25in
            doc.ClearSelection2(true);
            foreach (IFace2 sf in selectedFaces) ((IEntity)sf).Select4(true, null);
            File.WriteAllLines(cfgPath, new string[] {
                "mode=1",
                "overcut=0.25in",
                "folder=" + mode1Dir,
                "result=" + resPath
            });
            if (File.Exists(resPath)) File.Delete(resPath);
            InvokeMacro(swpPath, "ExportFacesToDXF1", "RunAutomatedFromTempConfig");
            if (!File.Exists(resPath)) throw new InvalidOperationException("Mode 1 headless macro wrote no result file.");
            string res1 = File.ReadAllText(resPath).Trim();
            if (!res1.StartsWith("OK|", StringComparison.Ordinal)) throw new InvalidOperationException("Mode 1 macro failed: " + res1);

            // Test Case B: Mode 3 with overcut=6mm
            doc.ClearSelection2(true);
            foreach (IFace2 sf in selectedFaces) ((IEntity)sf).Select4(true, null);
            File.WriteAllLines(cfgPath, new string[] {
                "mode=3",
                "overcut=6mm",
                "folder=" + mode3Dir,
                "result=" + resPath
            });
            if (File.Exists(resPath)) File.Delete(resPath);
            InvokeMacro(swpPath, "ExportFacesToDXF1", "RunAutomatedFromTempConfig");
            if (!File.Exists(resPath)) throw new InvalidOperationException("Mode 3 headless macro wrote no result file.");
            string res3 = File.ReadAllText(resPath).Trim();
            if (!res3.StartsWith("OK|", StringComparison.Ordinal)) throw new InvalidOperationException("Mode 3 macro failed: " + res3);

            return res1 + "\n" + res3;
        }
        finally
        {
            swApp.CloseDoc(docTitle);
            if (!string.IsNullOrEmpty(prevTitle))
            {
                int actErr = 0;
                try { swApp.ActivateDoc3(prevTitle, false, 0, ref actErr); } catch { }
            }
        }
    }
}
"@

[void][Reflection.Assembly]::LoadFrom($interopDll)
Add-Type -TypeDefinition $csharpRunner -ReferencedAssemblies $interopDll

$rev = $null
try {
    $rev = [SwComTestRunner]::Connect([bool]$LaunchIfNotRunning)
} catch {
    throw "Could not attach to running SldWorks.Application via COM ($($_.Exception.Message)). Ensure SOLIDWORKS is open on the same desktop/user session (not Run As Administrator mismatch), or pass -LaunchIfNotRunning."
}

Write-Host "[OK] Connected to SOLIDWORKS Revision $rev"

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

[SwComTestRunner]::InvokeMacro($SwpPath, "ExportFacesToDXF1", "CompileCheckOnly")

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
$mode1Dir = Join-Path $workDir "out_mode1"
$mode3Dir = Join-Path $workDir "out_mode3"
New-Item -ItemType Directory -Path $mode1Dir -Force | Out-Null
New-Item -ItemType Directory -Path $mode3Dir -Force | Out-Null

$cfgPath = Join-Path ([IO.Path]::GetTempPath()) "ExportFacesToDXF_test.cfg"
$resPath = Join-Path $workDir "macro_result.txt"

try {
    [void][SwComTestRunner]::RunExportTests($SwpPath, $workDir, $mode1Dir, $mode3Dir, $cfgPath, $resPath)

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
    if (-not $KeepArtifacts) {
        if (Test-Path -LiteralPath $cfgPath) { Remove-Item -LiteralPath $cfgPath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue }
    } else {
        Write-Host "[INFO] Kept temporary test artifacts at: $workDir"
    }
}
