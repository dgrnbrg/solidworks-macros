<#
.SYNOPSIS
Creates or updates ExportFacesToDXF.swp directly from ExportFacesToDXF.bas using [MS-OVBA] + Windows OLE2 Structured Storage.

.DESCRIPTION
Updates the VBA project streams inside a SOLIDWORKS .swp OLE2 compound file without requiring Microsoft Excel,
VBE UI automation, or Office Trust Center (AccessVBOM) settings.
Preserves all SOLIDWORKS APC host metadata (ThisLibrary, type library references to sldworks.tlb / swconst.tlb,
module name ExportFacesToDXF1, and project name ExportFacesToDXF), replaces ExportFacesToDXF1 source code
from the .bas file, strips stale p-code PerformanceCache (sets MODULEOFFSET=0 and _VBA_PROJECT Version=0xFFFF),
and verifies round-trip decompression of the resulting .swp streams.

.PARAMETER SourceBas
Path to the .bas source file. Defaults to <repo>\ExportFacesToDXF.bas.

.PARAMETER OutputSwp
Path to the .swp file to create or update. Defaults to <repo>\ExportFacesToDXF.swp.

.PARAMETER TemplateSwp
Optional template .swp used when OutputSwp does not exist. Defaults to <repo>\scripts\template_ExportFacesToDXF.swp.

.PARAMETER ModuleName
VBA module name inside the .swp. Defaults to ExportFacesToDXF1.
#>
[CmdletBinding()]
param(
    [string]$SourceBas,
    [string]$OutputSwp,
    [string]$TemplateSwp,
    [string]$ModuleName = "ExportFacesToDXF1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceBas)) {
    $SourceBas = Join-Path $repoRoot "ExportFacesToDXF.bas"
}
if ([string]::IsNullOrWhiteSpace($OutputSwp)) {
    $OutputSwp = Join-Path $repoRoot "ExportFacesToDXF.swp"
}
if ([string]::IsNullOrWhiteSpace($TemplateSwp)) {
    $TemplateSwp = Join-Path $PSScriptRoot "template_ExportFacesToDXF.swp"
}

if (-not (Test-Path -LiteralPath $SourceBas)) {
    throw "Source .bas file not found: $SourceBas"
}

$seedSwp = $null
if (Test-Path -LiteralPath $OutputSwp) {
    $seedSwp = $OutputSwp
} elseif (Test-Path -LiteralPath $TemplateSwp) {
    $seedSwp = $TemplateSwp
} else {
    throw "Neither target .swp ($OutputSwp) nor template .swp ($TemplateSwp) exists."
}

$csharpSource = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

public static class SwpVbaBuilder
{
    private const uint STGM_READ = 0x00000000;
    private const uint STGM_WRITE = 0x00000001;
    private const uint STGM_READWRITE = 0x00000002;
    private const uint STGM_SHARE_EXCLUSIVE = 0x00000010;
    private const uint STGM_CREATE = 0x00001000;

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorage(
        [MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
        IStorage pstgPriority,
        uint grfMode,
        IntPtr snbReserved,
        uint reserved,
        out IStorage ppstgOpen);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgCreateDocfile(
        [MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
        uint grfMode,
        uint reserved,
        out IStorage ppstgOpen);

    [ComImport]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        [PreserveSig] int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, uint grfMode, uint reserved1, uint reserved2, out IStream ppstm);
        [PreserveSig] int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr reserved1, uint grfMode, uint reserved2, out IStream ppstm);
        [PreserveSig] int CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, uint grfMode, uint reserved1, uint reserved2, out IStorage ppstg);
        [PreserveSig] int OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IStorage pstgPriority, uint grfMode, IntPtr snbReserved, uint reserved, out IStorage ppstg);
        [PreserveSig] int CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude, IStorage pstgDest);
        [PreserveSig] int MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IStorage pstgDest, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName, uint grfFlags);
        [PreserveSig] int Commit(uint grfCommitFlags);
        [PreserveSig] int Revert();
        [PreserveSig] int EnumElements(uint reserved1, IntPtr reserved2, uint reserved3, out IEnumSTATSTG ppenum);
        [PreserveSig] int DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsName);
        [PreserveSig] int RenameElement([MarshalAs(UnmanagedType.LPWStr)] string pwcsOldName, [MarshalAs(UnmanagedType.LPWStr)] string pwcsNewName);
        [PreserveSig] int SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr pctime, IntPtr patime, IntPtr pmtime);
        [PreserveSig] int SetClass(ref Guid clsid);
        [PreserveSig] int SetStateBits(uint grfStateBits, uint grfMask);
        [PreserveSig] int Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, uint grfStatFlag);
    }

    [ComImport]
    [Guid("0000000D-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSTATSTG
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] System.Runtime.InteropServices.ComTypes.STATSTG[] rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumSTATSTG ppenum);
    }

    private static void CheckHr(int hr, string context)
    {
        if (hr < 0)
        {
            throw new ExternalException(context + " failed (0x" + hr.ToString("X8") + ")", hr);
        }
    }

    private static byte[] ReadAllStream(IStorage stg, string name)
    {
        IStream stm;
        CheckHr(stg.OpenStream(name, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out stm), "OpenStream " + name);
        try
        {
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stm.Stat(out stat, 1);
            int size = checked((int)stat.cbSize);
            byte[] buf = new byte[size];
            if (size > 0)
            {
                IntPtr readPtr = Marshal.AllocCoTaskMem(4);
                try
                {
                    int offset = 0;
                    while (offset < size)
                    {
                        int chunk = Math.Min(65536, size - offset);
                        byte[] tmp = new byte[chunk];
                        stm.Read(tmp, chunk, readPtr);
                        int got = Marshal.ReadInt32(readPtr);
                        if (got <= 0) break;
                        Buffer.BlockCopy(tmp, 0, buf, offset, got);
                        offset += got;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(readPtr);
                }
            }
            return buf;
        }
        finally
        {
            Marshal.ReleaseComObject(stm);
        }
    }

    private static void WriteAllStream(IStorage stg, string name, byte[] data)
    {
        IStream stm;
        CheckHr(stg.CreateStream(name, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, 0, out stm), "CreateStream " + name);
        try
        {
            if (data.Length > 0)
            {
                IntPtr writtenPtr = Marshal.AllocCoTaskMem(4);
                try
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int chunk = Math.Min(65536, data.Length - offset);
                        byte[] tmp = new byte[chunk];
                        Buffer.BlockCopy(data, offset, tmp, 0, chunk);
                        stm.Write(tmp, chunk, writtenPtr);
                        offset += chunk;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(writtenPtr);
                }
            }
            stm.SetSize(data.Length);
            stm.Commit(0);
        }
        finally
        {
            Marshal.ReleaseComObject(stm);
        }
    }

    // [MS-OVBA] 2.4.1.3 Decompression
    public static byte[] OvbaDecompress(byte[] compressed)
    {
        if (compressed == null || compressed.Length < 1 || compressed[0] != 0x01)
            throw new InvalidDataException("Invalid MS-OVBA compressed container signature.");

        MemoryStream outMs = new MemoryStream();
        int idx = 1;
        while (idx < compressed.Length)
        {
            if (idx + 2 > compressed.Length) break;
            ushort header = (ushort)(compressed[idx] | (compressed[idx + 1] << 8));
            idx += 2;
            int chunkSize = (header & 0x0FFF) + 3;
            bool isCompressed = (header & 0x8000) != 0;
            int chunkEnd = Math.Min(compressed.Length, idx + chunkSize - 2);

            if (!isCompressed)
            {
                int rawLen = Math.Min(4096, compressed.Length - idx);
                outMs.Write(compressed, idx, rawLen);
                idx += rawLen;
            }
            else
            {
                long chunkStartOut = outMs.Length;
                while (idx < chunkEnd)
                {
                    byte flagByte = compressed[idx++];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if (idx >= chunkEnd) break;
                        if ((flagByte & (1 << bit)) == 0)
                        {
                            outMs.WriteByte(compressed[idx++]);
                        }
                        else
                        {
                            if (idx + 2 > chunkEnd) break;
                            ushort token = (ushort)(compressed[idx] | (compressed[idx + 1] << 8));
                            idx += 2;
                            long decompLen = outMs.Length - chunkStartOut;
                            int bitCount = 4;
                            while ((1L << bitCount) < decompLen && bitCount < 12) bitCount++;
                            int lenMask = 0xFFFF >> bitCount;
                            int offShift = 16 - bitCount;
                            int copyLen = (token & lenMask) + 3;
                            int copyOff = (token >> offShift) + 1;

                            byte[] current = outMs.GetBuffer();
                            for (int k = 0; k < copyLen; k++)
                            {
                                byte b = current[outMs.Length - copyOff];
                                outMs.WriteByte(b);
                                current = outMs.GetBuffer();
                            }
                        }
                    }
                }
            }
        }
        return outMs.ToArray();
    }

    // [MS-OVBA] 2.4.1.3 Compression
    public static byte[] OvbaCompress(byte[] raw)
    {
        MemoryStream outMs = new MemoryStream();
        outMs.WriteByte(0x01);

        int pos = 0;
        while (pos < raw.Length)
        {
            int chunkLen = Math.Min(4096, raw.Length - pos);
            byte[] chunkOut = CompressChunk(raw, pos, chunkLen);

            if (chunkOut.Length <= 4096 && !(chunkLen == 4096 && chunkOut.Length == 4096))
            {
                ushort hdr = (ushort)(0xB000 | ((chunkOut.Length - 1) & 0x0FFF));
                outMs.WriteByte((byte)(hdr & 0xFF));
                outMs.WriteByte((byte)((hdr >> 8) & 0xFF));
                outMs.Write(chunkOut, 0, chunkOut.Length);
                pos += chunkLen;
            }
            else if (chunkLen == 4096)
            {
                ushort hdr = (ushort)(0x3000 | 4095);
                outMs.WriteByte((byte)(hdr & 0xFF));
                outMs.WriteByte((byte)((hdr >> 8) & 0xFF));
                outMs.Write(raw, pos, 4096);
                pos += 4096;
            }
            else
            {
                // Shrink chunk so compressed payload stays <= 4096 bytes per [MS-OVBA] chunk limit
                int subLen = Math.Min(chunkLen, 3584);
                byte[] subOut = CompressChunk(raw, pos, subLen);
                ushort hdr = (ushort)(0xB000 | ((subOut.Length - 1) & 0x0FFF));
                outMs.WriteByte((byte)(hdr & 0xFF));
                outMs.WriteByte((byte)((hdr >> 8) & 0xFF));
                outMs.Write(subOut, 0, subOut.Length);
                pos += subLen;
            }
        }
        return outMs.ToArray();
    }

    private static byte[] CompressChunk(byte[] src, int chunkStart, int chunkLen)
    {
        MemoryStream chunkMs = new MemoryStream();
        int i = 0;
        byte[] tokBuf = new byte[16];

        while (i < chunkLen)
        {
            byte flags = 0;
            int tokLen = 0;
            for (int bit = 0; bit < 8 && i < chunkLen; bit++)
            {
                int bitCount = 4;
                while ((1 << bitCount) < i && bitCount < 12) bitCount++;
                int maxCopyLen = Math.Min(chunkLen - i, (0xFFFF >> bitCount) + 3);
                int maxWindow = Math.Min(i, 1 << bitCount);

                int bestLen = 0;
                int bestOff = 0;
                if (maxCopyLen >= 3 && maxWindow >= 1)
                {
                    for (int off = 1; off <= maxWindow; off++)
                    {
                        int cur = 0;
                        while (cur < maxCopyLen && src[chunkStart + i - off + cur] == src[chunkStart + i + cur])
                        {
                            cur++;
                        }
                        if (cur > bestLen)
                        {
                            bestLen = cur;
                            bestOff = off;
                            if (bestLen == maxCopyLen) break;
                        }
                    }
                }

                if (bestLen >= 3)
                {
                    flags |= (byte)(1 << bit);
                    int offShift = 16 - bitCount;
                    ushort tok = (ushort)(((bestOff - 1) << offShift) | (bestLen - 3));
                    tokBuf[tokLen++] = (byte)(tok & 0xFF);
                    tokBuf[tokLen++] = (byte)((tok >> 8) & 0xFF);
                    i += bestLen;
                }
                else
                {
                    tokBuf[tokLen++] = src[chunkStart + i];
                    i++;
                }
            }
            chunkMs.WriteByte(flags);
            chunkMs.Write(tokBuf, 0, tokLen);
        }
        return chunkMs.ToArray();
    }

    private static uint ReadUInt32LE(byte[] b, int o)
    {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    private static ushort ReadUInt16LE(byte[] b, int o)
    {
        return (ushort)(b[o] | (b[o + 1] << 8));
    }

    private static void WriteUInt32LE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static Dictionary<string, uint> ZeroTargetModuleOffset(byte[] dirRaw, string targetModuleName)
    {
        Dictionary<string, uint> oldOffsets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        string currentModName = null;

        while (i + 6 <= dirRaw.Length)
        {
            ushort tag = ReadUInt16LE(dirRaw, i);
            uint sz = ReadUInt32LE(dirRaw, i + 2);

            if (tag == 0x0009)
            {
                i += 12; // PROJECTVERSION: fixed 12 bytes
                continue;
            }
            if (tag == 0x0016)
            {
                // REFERENCENAME: 0x0016 + size + name + 0x003e + size2 + unicodeName
                i += 6 + checked((int)sz);
                if (i + 6 <= dirRaw.Length)
                {
                    uint sz2 = ReadUInt32LE(dirRaw, i + 2);
                    i += 6 + checked((int)sz2);
                }
                continue;
            }

            if (tag == 0x0019) // MODULENAME (MBCS)
            {
                currentModName = Encoding.GetEncoding(1252).GetString(dirRaw, i + 6, checked((int)sz));
            }
            else if (tag == 0x0031 && sz == 4) // MODULEOFFSET
            {
                if (!string.IsNullOrEmpty(currentModName))
                {
                    oldOffsets[currentModName] = ReadUInt32LE(dirRaw, i + 6);
                }
                if (string.Equals(currentModName, targetModuleName, StringComparison.OrdinalIgnoreCase))
                {
                    WriteUInt32LE(dirRaw, i + 6, 0u);
                }
            }
            else if (tag == 0x002B) // MODULE TERMINATOR
            {
                currentModName = null;
            }

            i += 6 + checked((int)sz);
        }

        return oldOffsets;
    }

    public static void UpdateSwpFromBas(string workingSwpPath, string basSourcePath, string targetModuleName)
    {
        Encoding cp1252 = Encoding.GetEncoding(1252);
        string rawBas = File.ReadAllText(basSourcePath, Encoding.UTF8);
        string normalized = rawBas.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        string attrPrefix = "Attribute VB_Name = \"" + targetModuleName + "\"\r\n";
        string trimmedStart = normalized.TrimStart();
        if (!trimmedStart.StartsWith("Attribute VB_Name =", StringComparison.OrdinalIgnoreCase))
        {
            normalized = attrPrefix + normalized;
        }
        byte[] moduleSourceBytes = cp1252.GetBytes(normalized);
        byte[] moduleCompressed = OvbaCompress(moduleSourceBytes);

        // Verify round-trip compression fidelity in memory before touching OLE storage
        byte[] verifyMod = OvbaDecompress(moduleCompressed);
        if (verifyMod.Length != moduleSourceBytes.Length)
            throw new InvalidOperationException("Module compression round-trip length mismatch.");
        for (int k = 0; k < verifyMod.Length; k++)
        {
            if (verifyMod[k] != moduleSourceBytes[k])
                throw new InvalidOperationException("Module compression round-trip byte mismatch at " + k);
        }

        IStorage rootStg = null;
        IStorage apcStg = null;
        IStorage projStg = null;
        IStorage vbaRootStg = null;
        IStorage vbaStg = null;

        CheckHr(StgOpenStorage(workingSwpPath, null, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out rootStg),
            "StgOpenStorage " + workingSwpPath);
        try
        {
            CheckHr(rootStg.OpenStorage("apc", null, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out apcStg),
                "OpenStorage apc");
            CheckHr(apcStg.OpenStorage("The VBA Project", null, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out projStg),
                "OpenStorage The VBA Project");
            CheckHr(projStg.OpenStorage("_VBA_Project", null, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out vbaRootStg),
                "OpenStorage _VBA_Project");
            CheckHr(vbaRootStg.OpenStorage("VBA", null, STGM_READWRITE | STGM_SHARE_EXCLUSIVE, IntPtr.Zero, 0, out vbaStg),
                "OpenStorage VBA");

            // 1. Update dir stream: strip only targetModuleName's MODULEOFFSET to 0
            //    (Preserve _VBA_PROJECT, ThisLibrary, and OLE2 container intact for SOLIDWORKS APC)
            byte[] oldVbaProj = ReadAllStream(vbaStg, "_VBA_PROJECT");
            if (oldVbaProj.Length <= 16)
            {
                throw new InvalidOperationException("TRUNCATED_VBA_PROJECT");
            }

            byte[] oldDirCompressed = ReadAllStream(vbaStg, "dir");
            byte[] dirRaw = OvbaDecompress(oldDirCompressed);
            Dictionary<string, uint> oldOffsets = ZeroTargetModuleOffset(dirRaw, targetModuleName);
            if (!oldOffsets.ContainsKey(targetModuleName))
            {
                throw new InvalidOperationException("Target module '" + targetModuleName + "' was not found in .swp VBA dir stream.");
            }
            byte[] newDirCompressed = OvbaCompress(dirRaw);
            byte[] verifyDir = OvbaDecompress(newDirCompressed);
            if (verifyDir.Length != dirRaw.Length)
                throw new InvalidOperationException("dir stream compression round-trip length mismatch.");

            WriteAllStream(vbaStg, "dir", newDirCompressed);

            // 2. Write updated target module source (no stale PerformanceCache)
            WriteAllStream(vbaStg, targetModuleName, moduleCompressed);

            CheckHr(vbaStg.Commit(0), "Commit VBA");
            CheckHr(vbaRootStg.Commit(0), "Commit _VBA_Project");
            CheckHr(projStg.Commit(0), "Commit The VBA Project");
            CheckHr(apcStg.Commit(0), "Commit apc");
            CheckHr(rootStg.Commit(0), "Commit root");

            Marshal.ReleaseComObject(vbaStg); vbaStg = null;
            Marshal.ReleaseComObject(vbaRootStg); vbaRootStg = null;
            Marshal.ReleaseComObject(projStg); projStg = null;
            Marshal.ReleaseComObject(apcStg); apcStg = null;
            Marshal.ReleaseComObject(rootStg); rootStg = null;
        }
        finally
        {
            if (vbaStg != null) Marshal.ReleaseComObject(vbaStg);
            if (vbaRootStg != null) Marshal.ReleaseComObject(vbaRootStg);
            if (projStg != null) Marshal.ReleaseComObject(projStg);
            if (apcStg != null) Marshal.ReleaseComObject(apcStg);
            if (rootStg != null) Marshal.ReleaseComObject(rootStg);
        }
    }
}
"@

Add-Type -TypeDefinition $csharpSource

$resolvedSource = (Resolve-Path -LiteralPath $SourceBas).Path
$resolvedSeed = (Resolve-Path -LiteralPath $seedSwp).Path
$outDir = Split-Path -Parent $OutputSwp
if (-not [string]::IsNullOrWhiteSpace($outDir) -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$tmpSwp = Join-Path ([IO.Path]::GetTempPath()) ("swp_build_" + [Guid]::NewGuid().ToString("N") + ".swp")
try {
    Copy-Item -LiteralPath $resolvedSeed -Destination $tmpSwp -Force
    try {
        [SwpVbaBuilder]::UpdateSwpFromBas($tmpSwp, $resolvedSource, $ModuleName)
    } catch {
        if ($_.Exception.InnerException -and $_.Exception.InnerException.Message -eq "TRUNCATED_VBA_PROJECT" -and (Test-Path -LiteralPath $TemplateSwp)) {
            Write-Warning "Existing '$OutputSwp' had a truncated _VBA_PROJECT stream; re-seeding from '$TemplateSwp'."
            Copy-Item -LiteralPath (Resolve-Path -LiteralPath $TemplateSwp).Path -Destination $tmpSwp -Force
            [SwpVbaBuilder]::UpdateSwpFromBas($tmpSwp, $resolvedSource, $ModuleName)
        } else {
            throw
        }
    }

    if (Test-Path -LiteralPath $OutputSwp) {
        try {
            Copy-Item -LiteralPath $tmpSwp -Destination $OutputSwp -Force
        } catch {
            throw "Failed to overwrite '$OutputSwp' (if SOLIDWORKS has it open in VBE or locked, close the VBE/macro and retry). Error: $($_.Exception.Message)"
        }
    } else {
        Move-Item -LiteralPath $tmpSwp -Destination $OutputSwp -Force
    }
    Write-Host "[OK] Built/updated '$OutputSwp' from '$resolvedSource' (module: $ModuleName)."
} finally {
    if (Test-Path -LiteralPath $tmpSwp) {
        Remove-Item -LiteralPath $tmpSwp -Force -ErrorAction SilentlyContinue
    }
}
