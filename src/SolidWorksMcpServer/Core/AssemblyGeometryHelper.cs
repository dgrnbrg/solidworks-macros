using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    public class FastenerPartProfile
    {
        public string PartPath { get; set; }
        public string ActiveConfig { get; set; }
        public string PartKind { get; set; }
        public string AxisFeatureName { get; set; }
        public bool AxisCreated { get; set; }
        public double[] LocalAxis { get; set; }
        public double LocalBearingOffsetMm { get; set; }
        public double LocalOppositeOffsetMm { get; set; }
        public int LocalBearingNormalSign { get; set; }
        public double MeasuredShankLengthMm { get; set; }
        public double MeasuredHeadHeightMm { get; set; }
        public double MeasuredThicknessMm { get; set; }
        public double MeasuredOdMm { get; set; }
        public double[] BoundingBoxMinMm { get; set; }
        public double[] BoundingBoxMaxMm { get; set; }
        public string BearingFaceToken { get; set; }
        public string OppositeFaceToken { get; set; }
        public double HexFlatLocalAngleDeg { get; set; }
        public bool HasHexFlats { get; set; }
    }

    public class McMasterCatalogItem
    {
        public string PartNumber { get; set; }
        public string Material { get; set; } = "18-8 Stainless Steel";
        public string ThreadSpec { get; set; }
        public string Kind { get; set; }
        public double NominalLengthMm { get; set; }
        public double WasherOrHeadOdMm { get; set; }
        public double HexAcrossFlatsMm { get; set; }
        public double ThicknessOrHeightMm { get; set; }
        public string Description { get; set; }
    }

    public static class AssemblyGeometryHelper
    {
        public const string DfmAddinGuid = "{3E9A4B71-6D2C-4F18-8E53-9A7C1D4B206F}";
        public static readonly Regex McMasterRegex = new Regex(@"\d{4,5}[a|k|n|t]\d{1,3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly McMasterCatalogItem[] Standard18_8Catalog = new[]
        {
            // M2.5 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A014", ThreadSpec = "M2.5x0.45", Kind = "shcs", NominalLengthMm = 10.0, WasherOrHeadOdMm = 4.5, ThicknessOrHeightMm = 2.5, Description = "18-8 Stainless Steel M2.5x10mm SHCS" },

            // M3 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A111", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 6.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x6mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A112", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 8.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x8mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A113", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 10.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x10mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A114", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 12.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x12mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A027", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 14.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x14mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A115", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 16.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x16mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A116", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 20.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x20mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A118", ThreadSpec = "M3x0.5", Kind = "shcs", NominalLengthMm = 25.0, WasherOrHeadOdMm = 5.5, ThicknessOrHeightMm = 3.0, Description = "18-8 Stainless Steel M3x25mm SHCS" },
            new McMasterCatalogItem { PartNumber = "93625A100", ThreadSpec = "M3x0.5", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 5.5, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel Nylon-Insert M3 Locknut" },
            new McMasterCatalogItem { PartNumber = "91828A211", ThreadSpec = "M3x0.5", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 5.5, ThicknessOrHeightMm = 2.4, Description = "18-8 Stainless Steel M3x0.5 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "90710A030", ThreadSpec = "M3x0.5", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 5.5, ThicknessOrHeightMm = 1.8, Description = "18-8 Stainless Steel M3x0.5 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "93475A210", ThreadSpec = "M3", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 7.0, ThicknessOrHeightMm = 0.5, Description = "18-8 Stainless Steel M3 Flat Washer" },

            // M4 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A119", ThreadSpec = "M4x0.7", Kind = "shcs", NominalLengthMm = 10.0, WasherOrHeadOdMm = 7.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M4x10mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A120", ThreadSpec = "M4x0.7", Kind = "shcs", NominalLengthMm = 12.0, WasherOrHeadOdMm = 7.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M4x12mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A122", ThreadSpec = "M4x0.7", Kind = "shcs", NominalLengthMm = 16.0, WasherOrHeadOdMm = 7.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M4x16mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A124", ThreadSpec = "M4x0.7", Kind = "shcs", NominalLengthMm = 20.0, WasherOrHeadOdMm = 7.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M4x20mm SHCS" },
            new McMasterCatalogItem { PartNumber = "93625A150", ThreadSpec = "M4x0.7", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 7.0, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel Nylon-Insert M4 Locknut" },
            new McMasterCatalogItem { PartNumber = "91828A231", ThreadSpec = "M4x0.7", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 7.0, ThicknessOrHeightMm = 3.2, Description = "18-8 Stainless Steel M4x0.7 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "90710A035", ThreadSpec = "M4x0.7", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 7.0, ThicknessOrHeightMm = 2.2, Description = "18-8 Stainless Steel M4x0.7 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "93475A230", ThreadSpec = "M4", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 9.0, ThicknessOrHeightMm = 0.8, Description = "18-8 Stainless Steel M4 Flat Washer" },

            // M5 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A125", ThreadSpec = "M5x0.8", Kind = "shcs", NominalLengthMm = 12.0, WasherOrHeadOdMm = 8.5, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel M5x12mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A126", ThreadSpec = "M5x0.8", Kind = "shcs", NominalLengthMm = 16.0, WasherOrHeadOdMm = 8.5, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel M5x16mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A127", ThreadSpec = "M5x0.8", Kind = "shcs", NominalLengthMm = 20.0, WasherOrHeadOdMm = 8.5, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel M5x20mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A128", ThreadSpec = "M5x0.8", Kind = "shcs", NominalLengthMm = 25.0, WasherOrHeadOdMm = 8.5, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel M5x25mm SHCS" },
            new McMasterCatalogItem { PartNumber = "93625A200", ThreadSpec = "M5x0.8", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 8.0, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel Nylon-Insert M5 Locknut" },
            new McMasterCatalogItem { PartNumber = "91828A241", ThreadSpec = "M5x0.8", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 8.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M5x0.8 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "90710A037", ThreadSpec = "M5x0.8", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 8.0, ThicknessOrHeightMm = 2.7, Description = "18-8 Stainless Steel M5x0.8 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "93475A240", ThreadSpec = "M5", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 1.0, Description = "18-8 Stainless Steel M5 Flat Washer" },

            // M6 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A134", ThreadSpec = "M6x1.0", Kind = "shcs", NominalLengthMm = 12.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel M6x12mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A136", ThreadSpec = "M6x1.0", Kind = "shcs", NominalLengthMm = 16.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel M6x16mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A137", ThreadSpec = "M6x1.0", Kind = "shcs", NominalLengthMm = 20.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel M6x20mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A139", ThreadSpec = "M6x1.0", Kind = "shcs", NominalLengthMm = 25.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel M6x25mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A141", ThreadSpec = "M6x1.0", Kind = "shcs", NominalLengthMm = 30.0, WasherOrHeadOdMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel M6x30mm SHCS" },
            new McMasterCatalogItem { PartNumber = "92095A242", ThreadSpec = "M6x1.0", Kind = "bhcs", NominalLengthMm = 25.0, WasherOrHeadOdMm = 10.5, ThicknessOrHeightMm = 3.3, Description = "18-8 Stainless Steel M6x25mm BHCS" },
            new McMasterCatalogItem { PartNumber = "93625A250", ThreadSpec = "M6x1.0", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 10.0, ThicknessOrHeightMm = 6.0, Description = "18-8 Stainless Steel Nylon-Insert M6 Locknut" },
            new McMasterCatalogItem { PartNumber = "91828A251", ThreadSpec = "M6x1.0", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 10.0, ThicknessOrHeightMm = 5.0, Description = "18-8 Stainless Steel M6x1.0 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "90710A038", ThreadSpec = "M6x1.0", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 10.0, ThicknessOrHeightMm = 3.2, Description = "18-8 Stainless Steel M6x1.0 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "93475A250", ThreadSpec = "M6", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 12.0, ThicknessOrHeightMm = 1.6, Description = "18-8 Stainless Steel M6 Flat Washer" },

            // M8 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "91292A145", ThreadSpec = "M8x1.25", Kind = "shcs", NominalLengthMm = 20.0, WasherOrHeadOdMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel M8x20mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A147", ThreadSpec = "M8x1.25", Kind = "shcs", NominalLengthMm = 25.0, WasherOrHeadOdMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel M8x25mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A149", ThreadSpec = "M8x1.25", Kind = "shcs", NominalLengthMm = 30.0, WasherOrHeadOdMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel M8x30mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A150", ThreadSpec = "M8x1.25", Kind = "shcs", NominalLengthMm = 35.0, WasherOrHeadOdMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel M8x35mm SHCS" },
            new McMasterCatalogItem { PartNumber = "91292A152", ThreadSpec = "M8x1.25", Kind = "shcs", NominalLengthMm = 40.0, WasherOrHeadOdMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel M8x40mm SHCS" },
            new McMasterCatalogItem { PartNumber = "92125A290", ThreadSpec = "M8x1.25", Kind = "csk", NominalLengthMm = 30.0, WasherOrHeadOdMm = 16.0, ThicknessOrHeightMm = 0.0, Description = "18-8 Stainless Steel M8x30mm Flat Head CSK" },
            new McMasterCatalogItem { PartNumber = "93625A300", ThreadSpec = "M8x1.25", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 13.0, ThicknessOrHeightMm = 8.0, Description = "18-8 Stainless Steel Nylon-Insert M8 Locknut" },
            new McMasterCatalogItem { PartNumber = "91828A271", ThreadSpec = "M8x1.25", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 13.0, ThicknessOrHeightMm = 6.5, Description = "18-8 Stainless Steel M8x1.25 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "90710A039", ThreadSpec = "M8x1.25", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 13.0, ThicknessOrHeightMm = 4.0, Description = "18-8 Stainless Steel M8x1.25 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "93475A270", ThreadSpec = "M8", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 16.0, ThicknessOrHeightMm = 1.6, Description = "18-8 Stainless Steel M8 Flat Washer" },

            // 1/4"-20 18-8 Stainless Steel
            new McMasterCatalogItem { PartNumber = "92198A537", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 19.05, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 3-4 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A540", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 25.40, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 1 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A542", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 31.75, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 1-1-4 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A544", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 38.10, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 1-1-2 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A548", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 44.45, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 1-3-4 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A550", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 50.80, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 2 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92198A551", ThreadSpec = "1/4-20", Kind = "hex_bolt", NominalLengthMm = 57.15, WasherOrHeadOdMm = 11.11, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 4.14, Description = "18-8 Stainless Steel 1-4-20 x 2-1-4 Hex Head Screw" },
            new McMasterCatalogItem { PartNumber = "92196A540", ThreadSpec = "1/4-20", Kind = "shcs", NominalLengthMm = 25.40, WasherOrHeadOdMm = 9.53, ThicknessOrHeightMm = 6.35, Description = "18-8 Stainless Steel 1-4-20 x 1 SHCS" },
            new McMasterCatalogItem { PartNumber = "91831A029", ThreadSpec = "1/4-20", Kind = "nylock_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 7.92, Description = "18-8 Stainless Steel 1-4-20 Nylock Nut" },
            new McMasterCatalogItem { PartNumber = "91841A029", ThreadSpec = "1/4-20", Kind = "hex_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 5.56, Description = "18-8 Stainless Steel 1-4-20 Hex Nut" },
            new McMasterCatalogItem { PartNumber = "91847A029", ThreadSpec = "1/4-20", Kind = "jam_nut", NominalLengthMm = 0.0, HexAcrossFlatsMm = 11.11, ThicknessOrHeightMm = 3.97, Description = "18-8 Stainless Steel 1-4-20 Thin Hex Jam Nut" },
            new McMasterCatalogItem { PartNumber = "92141A029", ThreadSpec = "1/4", Kind = "flat_washer", NominalLengthMm = 0.0, WasherOrHeadOdMm = 15.88, ThicknessOrHeightMm = 1.65, Description = "18-8 Stainless Steel 1-4 Flat Washer" }
        };

        public static McMasterCatalogItem FindCatalogItemByPartNumber(string pn)
        {
            if (string.IsNullOrWhiteSpace(pn)) return null;
            string clean = pn.Trim();
            foreach (McMasterCatalogItem item in Standard18_8Catalog)
            {
                if (string.Equals(item.PartNumber, clean, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        public static bool IsMaterial18_8(string material)
        {
            if (string.IsNullOrWhiteSpace(material)) return true;
            string m = material.Trim().ToUpperInvariant();
            return m == "18-8" || m == "18-8 SS" || m == "18-8 STAINLESS" || m == "18-8 STAINLESS STEEL";
        }

        public static void GetStandardFastenerDimensions(
            string threadSpec,
            out double washerOdMm,
            out double washerThkMm,
            out double hexAfMm,
            out double hexNutHeightMm,
            out double jamNutHeightMm,
            out double nylockHeightMm)
        {
            string ts = (threadSpec ?? "M6x1.0").ToUpperInvariant();
            if (ts.StartsWith("M2.5")) { washerOdMm = 6.0; washerThkMm = 0.5; hexAfMm = 5.0; hexNutHeightMm = 2.0; jamNutHeightMm = 1.6; nylockHeightMm = 3.8; }
            else if (ts.StartsWith("M3")) { washerOdMm = 7.0; washerThkMm = 0.5; hexAfMm = 5.5; hexNutHeightMm = 2.4; jamNutHeightMm = 1.8; nylockHeightMm = 4.0; }
            else if (ts.StartsWith("M4")) { washerOdMm = 9.0; washerThkMm = 0.8; hexAfMm = 7.0; hexNutHeightMm = 3.2; jamNutHeightMm = 2.2; nylockHeightMm = 5.0; }
            else if (ts.StartsWith("M5")) { washerOdMm = 10.0; washerThkMm = 1.0; hexAfMm = 8.0; hexNutHeightMm = 4.0; jamNutHeightMm = 2.7; nylockHeightMm = 5.0; }
            else if (ts.StartsWith("M6")) { washerOdMm = 12.0; washerThkMm = 1.6; hexAfMm = 10.0; hexNutHeightMm = 5.0; jamNutHeightMm = 3.2; nylockHeightMm = 6.0; }
            else if (ts.StartsWith("M8")) { washerOdMm = 16.0; washerThkMm = 1.6; hexAfMm = 13.0; hexNutHeightMm = 6.5; jamNutHeightMm = 4.0; nylockHeightMm = 8.0; }
            else if (ts.StartsWith("1/4")) { washerOdMm = 15.88; washerThkMm = 1.65; hexAfMm = 11.11; hexNutHeightMm = 5.56; jamNutHeightMm = 3.97; nylockHeightMm = 7.92; }
            else { washerOdMm = 20.64; washerThkMm = 1.65; hexAfMm = 14.29; hexNutHeightMm = 8.33; jamNutHeightMm = 5.56; nylockHeightMm = 11.51; }
        }

        public static double ChooseStandardBoltLengthMm(string threadSpec, double minLengthMm, double maxSafeLengthMm)
        {
            bool imperial = (threadSpec ?? "").Contains("/");
            double[] steps = imperial
                ? new[] { 12.70, 15.88, 19.05, 22.23, 25.40, 31.75, 38.10, 44.45, 50.80, 57.15, 63.50, 76.20 }
                : new[] { 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 25.0, 30.0, 35.0, 40.0, 45.0, 50.0, 60.0 };

            double chosen = 0.0;
            foreach (double len in steps)
            {
                if (len >= minLengthMm - 0.5 && (maxSafeLengthMm <= 0 || len <= maxSafeLengthMm + 0.05))
                {
                    chosen = len;
                    break;
                }
            }
            if (chosen <= 0.0)
            {
                foreach (double len in steps)
                {
                    if (maxSafeLengthMm <= 0 || len <= maxSafeLengthMm + 0.05)
                        chosen = len;
                }
            }
            return chosen > 0.0 ? chosen : Math.Ceiling(minLengthMm);
        }

        public static List<McMasterCatalogItem> Search18_8Catalog(string threadPrefix, string kind, double targetLengthMm)
        {
            List<McMasterCatalogItem> hits = new List<McMasterCatalogItem>();
            foreach (McMasterCatalogItem item in Standard18_8Catalog)
            {
                if (!string.IsNullOrWhiteSpace(threadPrefix) &&
                    item.ThreadSpec.IndexOf(threadPrefix, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !item.Description.ToUpperInvariant().Contains(threadPrefix.ToUpperInvariant()))
                    continue;
                if (!string.IsNullOrWhiteSpace(kind) &&
                    !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits.Add(item);
            }
            if (targetLengthMm > 0 && hits.Count > 1)
            {
                hits.Sort((a, b) => Math.Abs(a.NominalLengthMm - targetLengthMm).CompareTo(Math.Abs(b.NominalLengthMm - targetLengthMm)));
            }
            return hits;
        }

        public static McMasterCatalogItem FindBestBolt18_8(string threadPrefix, string preferredKind, double minLengthMm, double maxSafeLengthMm)
        {
            List<McMasterCatalogItem> candidates = Search18_8Catalog(threadPrefix, preferredKind, 0);
            if (candidates.Count == 0 && string.Equals(preferredKind, "hex_bolt", StringComparison.OrdinalIgnoreCase))
                candidates = Search18_8Catalog(threadPrefix, "shcs", 0);
            if (candidates.Count == 0 && string.Equals(preferredKind, "shcs", StringComparison.OrdinalIgnoreCase))
                candidates = Search18_8Catalog(threadPrefix, "hex_bolt", 0);

            McMasterCatalogItem best = null;
            foreach (McMasterCatalogItem c in candidates)
            {
                if (c.NominalLengthMm >= minLengthMm - 0.5 &&
                    (maxSafeLengthMm <= 0 || c.NominalLengthMm <= maxSafeLengthMm + 0.05))
                {
                    if (best == null || c.NominalLengthMm < best.NominalLengthMm)
                        best = c;
                }
            }
            if (best == null && candidates.Count > 0)
            {
                // Fallback to shortest safe candidate below maxSafeLengthMm if minLengthMm was tight
                foreach (McMasterCatalogItem c in candidates)
                {
                    if (maxSafeLengthMm <= 0 || c.NominalLengthMm <= maxSafeLengthMm + 0.05)
                    {
                        if (best == null || c.NominalLengthMm > best.NominalLengthMm)
                            best = c;
                    }
                }
            }
            return best;
        }

        public static string GetMcMasterPartNumber(IComponent2 comp)
        {
            if (comp == null) return null;
            string name = comp.Name2 ?? "";
            string path = "";
            try { path = comp.GetPathName() ?? ""; } catch { }
            Match m = McMasterRegex.Match(Path.GetFileNameWithoutExtension(path));
            if (m.Success) return m.Value.ToUpperInvariant();
            m = McMasterRegex.Match(name);
            return m.Success ? m.Value.ToUpperInvariant() : null;
        }

        private static readonly Regex McMasterFastenerSkuRegex = new Regex(@"^9\d{4}[aA]\d{1,3}$", RegexOptions.Compiled);

        private static readonly string[] NonFastenerComponentKeywords = new[]
        {
            "MOTOR", "BEARING", "COUPLING", "LEAD SCREW", "BALL SCREW", "ACME",
            "SWITCH", "RAIL", "TRILHO", "ROLAMENTO", "EXTRUSION", "ROUTER",
            "SPINDLE", "PULLEY", "BELT", "GEAR", "SHAFT", "SPRING", "BRACKET"
        };

        public static bool IsMcMasterOrFastenerComponent(IComponent2 comp)
        {
            if (comp == null) return false;

            string text = ((comp.Name2 ?? "") + " " + Path.GetFileNameWithoutExtension(comp.GetPathName() ?? "")).ToUpperInvariant();
            for (int i = 0; i < NonFastenerComponentKeywords.Length; i++)
            {
                if (text.Contains(NonFastenerComponentKeywords[i]))
                    return false;
            }

            string sku = GetMcMasterPartNumber(comp);
            if (!string.IsNullOrEmpty(sku) && McMasterFastenerSkuRegex.IsMatch(sku))
                return true;

            if (text.Contains("SHCS") || text.Contains("BHCS") || text.Contains("FHCS") || text.Contains("SFHCS") ||
                text.Contains("SOCKET HEAD") || text.Contains("BUTTON HEAD") || text.Contains("FLAT HEAD") ||
                text.Contains("LOCKNUT") || text.Contains("NYLOCK") || text.Contains("HEX NUT") || text.Contains("JAM NUT") ||
                text.Contains("FLAT WASHER") || text.Contains("WASHER") || text.Contains("HEX HEAD SCREW") ||
                text.Contains("FASTENER") || text.Contains("THREADED INSERT"))
                return true;

            return false;
        }

        public static double[] Normalize(double[] v)
        {
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (len < 1e-12) return new[] { 0.0, 1.0, 0.0 };
            return new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        public static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        public static double[] Cross(double[] a, double[] b)
        {
            return new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }

        public static double[] TransformPointFast(double[] m, double x, double y, double z)
        {
            if (m == null || m.Length < 12) return new[] { x, y, z };
            double s = (m.Length > 12 && Math.Abs(m[12]) > 1e-12) ? m[12] : 1.0;
            return new[]
            {
                (x * m[0] + y * m[3] + z * m[6]) * s + m[9],
                (x * m[1] + y * m[4] + z * m[7]) * s + m[10],
                (x * m[2] + y * m[5] + z * m[8]) * s + m[11]
            };
        }

        public static double[] TransformVectorFast(double[] m, double vx, double vy, double vz)
        {
            if (m == null || m.Length < 9) return Normalize(new[] { vx, vy, vz });
            return Normalize(new[]
            {
                vx * m[0] + vy * m[3] + vz * m[6],
                vx * m[1] + vy * m[4] + vz * m[7],
                vx * m[2] + vy * m[5] + vz * m[8]
            });
        }

        public static void TransformAabbFast(double[] m, double[] boxM, out double[] outMin, out double[] outMax)
        {
            outMin = new[] { 0.0, 0.0, 0.0 };
            outMax = new[] { 0.0, 0.0, 0.0 };
            if (boxM == null || boxM.Length < 6) return;

            if (m == null || m.Length < 12)
            {
                outMin = new[] { Math.Min(boxM[0], boxM[3]), Math.Min(boxM[1], boxM[4]), Math.Min(boxM[2], boxM[5]) };
                outMax = new[] { Math.Max(boxM[0], boxM[3]), Math.Max(boxM[1], boxM[4]), Math.Max(boxM[2], boxM[5]) };
                return;
            }

            outMin = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            outMax = new[] { double.MinValue, double.MinValue, double.MinValue };
            for (int ix = 0; ix <= 1; ix++)
            {
                double x = ix == 0 ? boxM[0] : boxM[3];
                for (int iy = 0; iy <= 1; iy++)
                {
                    double y = iy == 0 ? boxM[1] : boxM[4];
                    for (int iz = 0; iz <= 1; iz++)
                    {
                        double z = iz == 0 ? boxM[2] : boxM[5];
                        double[] p = TransformPointFast(m, x, y, z);
                        if (p[0] < outMin[0]) outMin[0] = p[0];
                        if (p[1] < outMin[1]) outMin[1] = p[1];
                        if (p[2] < outMin[2]) outMin[2] = p[2];
                        if (p[0] > outMax[0]) outMax[0] = p[0];
                        if (p[1] > outMax[1]) outMax[1] = p[1];
                        if (p[2] > outMax[2]) outMax[2] = p[2];
                    }
                }
            }
        }

        public static double[] TransformPoint(IMathUtility mathUtil, IMathTransform xf, double[] ptM)
        {
            if (xf == null) return new[] { ptM[0], ptM[1], ptM[2] };
            double[] m = xf.ArrayData as double[];
            if (m != null && m.Length >= 12)
                return TransformPointFast(m, ptM[0], ptM[1], ptM[2]);
            if (mathUtil == null) return new[] { ptM[0], ptM[1], ptM[2] };
            IMathPoint p = mathUtil.CreatePoint(ptM) as IMathPoint;
            IMathPoint tp = p.MultiplyTransform(xf) as IMathPoint;
            double[] arr = tp.ArrayData as double[];
            return arr != null && arr.Length >= 3 ? new[] { arr[0], arr[1], arr[2] } : ptM;
        }

        public static double[] TransformVector(IMathUtility mathUtil, IMathTransform xf, double[] vec)
        {
            if (xf == null) return Normalize(vec);
            double[] m = xf.ArrayData as double[];
            if (m != null && m.Length >= 9)
                return TransformVectorFast(m, vec[0], vec[1], vec[2]);
            if (mathUtil == null) return Normalize(vec);
            IMathVector v = mathUtil.CreateVector(vec) as IMathVector;
            IMathVector tv = v.MultiplyTransform(xf) as IMathVector;
            double[] arr = tv.ArrayData as double[];
            return arr != null && arr.Length >= 3 ? Normalize(arr) : Normalize(vec);
        }

        public static MathTransform BuildTransformFromPose(
            IMathUtility mathUtil,
            double[] targetPosM,
            double[] targetAxis,
            double[] localAxis,
            double localBearingOffsetM,
            double clockAngleDeg,
            double hexFlatLocalAngleDeg)
        {
            double[] dstY = Normalize(targetAxis);
            double[] srcY = Normalize(localAxis ?? new[] { 0.0, 1.0, 0.0 });

            // Build orthonormal basis (srcX, srcY, srcZ) in part local frame
            double[] worldUp = Math.Abs(srcY[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
            double[] srcZ = Normalize(Cross(worldUp, srcY));
            double[] srcX = Normalize(Cross(srcY, srcZ));

            // Rotate target frame around dstY so local hex flat angle matches clockAngleDeg
            double[] refRadial = Math.Abs(dstY[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
            double dotR = Dot(refRadial, dstY);
            double[] dstBasisX = Normalize(new[]
            {
                refRadial[0] - dotR * dstY[0],
                refRadial[1] - dotR * dstY[1],
                refRadial[2] - dotR * dstY[2]
            });
            double[] dstBasisZ = Normalize(Cross(dstBasisX, dstY));

            double rad = (clockAngleDeg - hexFlatLocalAngleDeg) * Math.PI / 180.0;
            double c = Math.Cos(rad);
            double s = Math.Sin(rad);

            // Columns/rows mapped so localAxis -> dstY and local plane rotates by rad around dstY
            double[] rotX = new[]
            {
                dstBasisX[0] * c + dstBasisZ[0] * s,
                dstBasisX[1] * c + dstBasisZ[1] * s,
                dstBasisX[2] * c + dstBasisZ[2] * s
            };
            double[] rotZ = Normalize(Cross(rotX, dstY));

            // Express global axes of local X(1,0,0), Y(0,1,0), Z(0,0,1) via (srcX, srcY, srcZ) weights
            double[] colX = new[]
            {
                srcX[0] * rotX[0] + srcY[0] * dstY[0] + srcZ[0] * rotZ[0],
                srcX[0] * rotX[1] + srcY[0] * dstY[1] + srcZ[0] * rotZ[1],
                srcX[0] * rotX[2] + srcY[0] * dstY[2] + srcZ[0] * rotZ[2]
            };
            double[] colY = new[]
            {
                srcX[1] * rotX[0] + srcY[1] * dstY[0] + srcZ[1] * rotZ[0],
                srcX[1] * rotX[1] + srcY[1] * dstY[1] + srcZ[1] * rotZ[1],
                srcX[1] * rotX[2] + srcY[1] * dstY[2] + srcZ[1] * rotZ[2]
            };
            double[] colZ = new[]
            {
                srcX[2] * rotX[0] + srcY[2] * dstY[0] + srcZ[2] * rotZ[0],
                srcX[2] * rotX[1] + srcY[2] * dstY[1] + srcZ[2] * rotZ[1],
                srcX[2] * rotX[2] + srcY[2] * dstY[2] + srcZ[2] * rotZ[2]
            };

            double tx = targetPosM[0] - localBearingOffsetM * dstY[0];
            double ty = targetPosM[1] - localBearingOffsetM * dstY[1];
            double tz = targetPosM[2] - localBearingOffsetM * dstY[2];

            return mathUtil.CreateTransform(new double[]
            {
                colX[0], colX[1], colX[2],
                colY[0], colY[1], colY[2],
                colZ[0], colZ[1], colZ[2],
                tx, ty, tz,
                1.0, 0.0, 0.0, 0.0
            }) as MathTransform;
        }

        public static string FindLocalMcMasterPart(string mcmasterPn, string libraryDir)
        {
            if (string.IsNullOrWhiteSpace(mcmasterPn)) return null;
            string pn = mcmasterPn.Trim().ToUpperInvariant();
            string downloads = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");

            if (!string.IsNullOrWhiteSpace(libraryDir) && Directory.Exists(libraryDir))
            {
                string[] hits = Directory.GetFiles(libraryDir, pn + "*.SLDPRT", SearchOption.TopDirectoryOnly);
                if (hits.Length > 0) return hits[0];
            }
            if (Directory.Exists(downloads))
            {
                string[] hits = Directory.GetFiles(downloads, pn + "*.SLDPRT", SearchOption.TopDirectoryOnly);
                if (hits.Length > 0)
                {
                    string src = hits[0];
                    if (!string.IsNullOrWhiteSpace(libraryDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(libraryDir);
                            string dst = Path.Combine(libraryDir, Path.GetFileName(src));
                            if (!File.Exists(dst)) File.Copy(src, dst, false);
                            return dst;
                        }
                        catch { }
                    }
                    return src;
                }
            }
            return null;
        }

        public static Dictionary<string, object> InvokeMcMasterBridge(
            ISldWorks app,
            string action,
            string mcmasterPn,
            string query,
            string libraryDir,
            int timeoutMs = 20000)
        {
            object addin = null;
            try { addin = app.GetAddInObject(DfmAddinGuid); } catch { }
            if (addin == null)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ok", false },
                    { "error", "SolidWorks3DPrintDFMAddin bridge is not loaded in SOLIDWORKS yet. Restart SOLIDWORKS so it loads the rebuilt add-in." }
                };
            }

            string reqJson = JsonHelper.Serialize(new Dictionary<string, object>
            {
                { "action", action },
                { "mcmaster_pn", mcmasterPn ?? "" },
                { "query", query ?? "" },
                { "library_dir", libraryDir ?? "" },
                { "timeout_ms", timeoutMs }
            });

            object raw = addin.GetType().InvokeMember(
                "InvokeMcMasterBridge",
                BindingFlags.InvokeMethod,
                null,
                addin,
                new object[] { reqJson });

            return JsonHelper.DeserializeDict(Convert.ToString(raw, CultureInfo.InvariantCulture));
        }

        public static FastenerPartProfile EnsureFastenerAxisAndInspect(ISldWorks app, string partPath)
        {
            if (app == null || string.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath))
                return null;

            bool alreadyOpen = false;
            IModelDoc2 partDoc = null;
            IModelDoc2 iter = app.GetFirstDocument() as IModelDoc2;
            while (iter != null)
            {
                if (string.Equals(iter.GetPathName(), partPath, StringComparison.OrdinalIgnoreCase))
                {
                    partDoc = iter;
                    alreadyOpen = true;
                    break;
                }
                iter = iter.GetNext() as IModelDoc2;
            }

            if (partDoc == null)
            {
                int oErr = 0, oWarn = 0;
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
                try
                {
                    partDoc = app.OpenDoc6(partPath, (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oErr, ref oWarn);
                }
                finally
                {
                    try { app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { }
                }
            }

            if (partDoc == null)
                return null;

            try
            {
                IConfiguration cfg = partDoc.ConfigurationManager?.ActiveConfiguration;
                string cfgName = cfg != null ? cfg.Name : "";

                FastenerPartProfile profile = InspectPartGeometry(partDoc, partPath, cfgName);

                // Check for existing reference axis named "Fastener_Axis"
                IFeature axisFeat = FindFeatureByTypeAndName(partDoc, "RefAxis", "Fastener_Axis")
                    ?? FindAnyRefAxis(partDoc);

                if (axisFeat == null)
                {
                    axisFeat = CreateFastenerAxisFeature(partDoc, profile.LocalAxis);
                    if (axisFeat != null)
                    {
                        try { axisFeat.Name = "Fastener_Axis"; } catch { }
                        profile.AxisCreated = true;
                        int sErr = 0, sWarn = 0;
                        partDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref sErr, ref sWarn);
                    }
                }
                else if (!string.Equals(axisFeat.Name, "Fastener_Axis", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        axisFeat.Name = "Fastener_Axis";
                        int sErr = 0, sWarn = 0;
                        partDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref sErr, ref sWarn);
                    }
                    catch { }
                }

                profile.AxisFeatureName = axisFeat != null ? axisFeat.Name : null;
                return profile;
            }
            finally
            {
                if (!alreadyOpen)
                {
                    try { app.CloseDoc(partDoc.GetTitle()); } catch { }
                }
            }
        }

        private class AxialPlaneCandidate
        {
            public IFace2 Face;
            public string Token;
            public double[] PointM;
            public double[] Normal;
            public double AreaM2;
            public double RadialMaxM;
            public double S;
            public int NormalSign;
        }

        private class RadialConeCandidate
        {
            public double[] OriginM;
            public double[] Axis;
            public double HalfAngleDeg;
            public double[] BoxM;
            public double RadialMaxM;
            public double SMin;
            public double SMax;
        }

        private static void ComputeAxisProjections(double[] axis, double[] boxM, out double sMin, out double sMax, out double radialMax)
        {
            sMin = double.MaxValue;
            sMax = double.MinValue;
            radialMax = 0.0;
            if (boxM == null || boxM.Length < 6) return;

            for (int ix = 0; ix <= 1; ix++)
            {
                double x = ix == 0 ? boxM[0] : boxM[3];
                for (int iy = 0; iy <= 1; iy++)
                {
                    double y = iy == 0 ? boxM[1] : boxM[4];
                    for (int iz = 0; iz <= 1; iz++)
                    {
                        double z = iz == 0 ? boxM[2] : boxM[5];
                        double s = x * axis[0] + y * axis[1] + z * axis[2];
                        if (s < sMin) sMin = s;
                        if (s > sMax) sMax = s;
                        double rx = x - s * axis[0], ry = y - s * axis[1], rz = z - s * axis[2];
                        double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        if (r > radialMax) radialMax = r;
                    }
                }
            }
        }

        private static double MaxRadialExtentFromBox(double[] axis, double[] boxM)
        {
            if (boxM == null || boxM.Length < 6) return 0.0;
            double maxR = 0.0;
            // Sample extreme corners perpendicular to axis (use face/edge extents, note AABB corners overshoot circle by sqrt(2))
            double[] u = Math.Abs(axis[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
            double[] zAx = Normalize(Cross(u, axis));
            double[] xAx = Normalize(Cross(axis, zAx));
            double rxMax = 0.0, rzMax = 0.0;
            for (int ix = 0; ix <= 1; ix++)
            {
                double x = ix == 0 ? boxM[0] : boxM[3];
                for (int iy = 0; iy <= 1; iy++)
                {
                    double y = iy == 0 ? boxM[1] : boxM[4];
                    for (int iz = 0; iz <= 1; iz++)
                    {
                        double z = iz == 0 ? boxM[2] : boxM[5];
                        double ax = Math.Abs(x * xAx[0] + y * xAx[1] + z * xAx[2]);
                        double az = Math.Abs(x * zAx[0] + y * zAx[1] + z * zAx[2]);
                        if (ax > rxMax) rxMax = ax;
                        if (az > rzMax) rzMax = az;
                    }
                }
            }
            maxR = Math.Max(rxMax, rzMax);
            return maxR;
        }

        private static double[] CanonicalizePositiveAxis(double[] v)
        {
            double[] n = Normalize(v);
            if (Math.Abs(n[2]) > 0.1 ? n[2] < 0 : (Math.Abs(n[1]) > 0.1 ? n[1] < 0 : n[0] < 0))
                return new[] { -n[0], -n[1], -n[2] };
            return n;
        }

        private static FastenerPartProfile InspectPartGeometry(IModelDoc2 partDoc, string partPath, string cfgName)
        {
            IPartDoc part = partDoc as IPartDoc;
            double[] bb = part != null ? part.GetPartBox(true) as double[] : null;

            double[] uAxis = new[] { 0.0, 1.0, 0.0 };
            double bestCylArea = -1.0;
            List<AxialPlaneCandidate> axialPlanes = new List<AxialPlaneCandidate>();
            List<RadialConeCandidate> cones = new List<RadialConeCandidate>();
            List<double[]> sideFaceNormals = new List<double[]>();
            List<double[]> cylExtents = new List<double[]>(); // [sMin, sMax, radiusM]

            double maxPartRadialM = 0.0;

            object[] bodies = part != null ? part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] : null;
            if (bodies != null)
            {
                // First pass: find principal unsigned axis uAxis from largest cylinder
                foreach (object bObj in bodies)
                {
                    if (!(bObj is IBody2 body)) continue;
                    object[] faces = body.GetFaces() as object[];
                    if (faces == null) continue;
                    foreach (object fObj in faces)
                    {
                        if (!(fObj is IFace2 face)) continue;
                        ISurface surf = face.GetSurface() as ISurface;
                        if (surf == null) continue;
                        if (surf.IsCylinder())
                        {
                            double[] cp = surf.CylinderParams as double[];
                            double area = face.GetArea();
                            if (cp != null && cp.Length >= 7 && area > bestCylArea)
                            {
                                bestCylArea = area;
                                uAxis = CanonicalizePositiveAxis(new[] { cp[3], cp[4], cp[5] });
                            }
                        }
                    }
                }
            }

            if (bestCylArea <= 0 && bb != null && bb.Length >= 6)
            {
                double sx = bb[3] - bb[0], sy = bb[4] - bb[1], sz = bb[5] - bb[2];
                // Thin washers have shortest span along axis; long bolts have longest span along axis
                uAxis = sy <= sx && sy <= sz ? new[] { 0.0, 1.0, 0.0 } : (sz >= sx && sz >= sy ? new[] { 0.0, 0.0, 1.0 } : new[] { 1.0, 0.0, 0.0 });
            }

            maxPartRadialM = MaxRadialExtentFromBox(uAxis, bb);
            ComputeAxisProjections(uAxis, bb, out double bbSMin, out double bbSMax, out _);
            double totalSpanM = Math.Max(0.0001, bbSMax - bbSMin);
            double totalSpanMm = totalSpanM * 1000.0;
            double outerDiaMm = maxPartRadialM * 2000.0;

            // Second pass: collect axial planar faces, coaxial cones, side planes, and cylinder spans
            if (bodies != null)
            {
                for (int bi = 0; bi < bodies.Length; bi++)
                {
                    if (!(bodies[bi] is IBody2 body)) continue;
                    object[] faces = body.GetFaces() as object[];
                    if (faces == null) continue;
                    foreach (object fObj in faces)
                    {
                        if (!(fObj is IFace2 face)) continue;
                        ISurface surf = face.GetSurface() as ISurface;
                        if (surf == null) continue;

                        if (surf.IsPlane())
                        {
                            double[] pp = surf.PlaneParams as double[];
                            if (pp == null || pp.Length < 6) continue;
                            double[] n = new[] { pp[0], pp[1], pp[2] };
                            if (face.FaceInSurfaceSense()) { n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]; }
                            n = Normalize(n);
                            double align = Dot(n, uAxis);
                            if (Math.Abs(align) > 0.99)
                            {
                                double[] fBox = face.GetBox() as double[];
                                double rMax = MaxRadialExtentFromBox(uAxis, fBox);
                                if (rMax > maxPartRadialM) maxPartRadialM = rMax;
                                double[] pt = new[] { pp[3], pp[4], pp[5] };
                                double s = Dot(pt, uAxis);
                                axialPlanes.Add(new AxialPlaneCandidate
                                {
                                    Face = face,
                                    Token = SwConnectionManager.ComputeFaceToken(face, bi),
                                    PointM = pt,
                                    Normal = n,
                                    AreaM2 = face.GetArea(),
                                    RadialMaxM = rMax,
                                    S = s,
                                    NormalSign = align >= 0 ? 1 : -1
                                });
                            }
                            else if (Math.Abs(align) < 0.05)
                            {
                                sideFaceNormals.Add(n);
                            }
                        }
                        else if (surf.IsCylinder())
                        {
                            double[] cp = surf.CylinderParams as double[];
                            if (cp != null && cp.Length >= 7)
                            {
                                double[] cAx = Normalize(new[] { cp[3], cp[4], cp[5] });
                                if (Math.Abs(Dot(cAx, uAxis)) > 0.98)
                                {
                                    double[] fBox = face.GetBox() as double[];
                                    ComputeAxisProjections(uAxis, fBox, out double csMin, out double csMax, out _);
                                    cylExtents.Add(new[] { csMin, csMax, cp[6] });
                                    if (!face.FaceInSurfaceSense() && cp[6] > maxPartRadialM)
                                        maxPartRadialM = cp[6];
                                }
                            }
                        }
                        else if (surf.IsCone())
                        {
                            double[] cnp = surf.ConeParams as double[];
                            if (cnp != null && cnp.Length >= 8)
                            {
                                double[] kAx = Normalize(new[] { cnp[3], cnp[4], cnp[5] });
                                if (Math.Abs(Dot(kAx, uAxis)) > 0.98)
                                {
                                    double halfDeg = Math.Abs(cnp[7]) * 180.0 / Math.PI;
                                    double[] fBox = face.GetBox() as double[];
                                    double rMax = MaxRadialExtentFromBox(uAxis, fBox);
                                    ComputeAxisProjections(uAxis, fBox, out double ksMin, out double ksMax, out _);
                                    cones.Add(new RadialConeCandidate
                                    {
                                        OriginM = new[] { cnp[0], cnp[1], cnp[2] },
                                        Axis = kAx,
                                        HalfAngleDeg = halfDeg,
                                        BoxM = fBox,
                                        RadialMaxM = rMax,
                                        SMin = ksMin,
                                        SMax = ksMax
                                    });
                                }
                            }
                        }
                    }
                }
            }

            outerDiaMm = maxPartRadialM * 2000.0;
            bool hasHexFlats = sideFaceNormals.Count >= 6;
            string upperName = Path.GetFileNameWithoutExtension(partPath ?? "").ToUpperInvariant();

            // Classify PartKind
            bool nameWasher = upperName.Contains("WASHER");
            bool nameNut = (upperName.Contains("LOCKNUT") || upperName.Contains("NYLOCK") || upperName.Contains("NUT")) &&
                           !upperName.Contains("SCREW") && !upperName.Contains("BOLT");
            bool nameCsk = upperName.Contains("FLAT HEAD") || upperName.Contains("COUNTERSUNK") || upperName.Contains("CSK");

            string partKind = "bolt_or_screw";
            if (nameWasher || (totalSpanMm <= 4.5 && !hasHexFlats))
            {
                partKind = "washer";
            }
            else if (nameNut || (hasHexFlats && totalSpanMm <= Math.Max(14.0, 1.6 * outerDiaMm)))
            {
                partKind = "nut";
            }

            // Compute radial extent near negative uAxis end vs positive uAxis end
            double radAtMin = 0.0, radAtMax = 0.0;
            double minEndThresh = bbSMin + 0.25 * totalSpanM;
            double maxEndThresh = bbSMax - 0.25 * totalSpanM;
            foreach (double[] ce in cylExtents)
            {
                if (ce[0] <= minEndThresh && ce[2] > radAtMin) radAtMin = ce[2];
                if (ce[1] >= maxEndThresh && ce[2] > radAtMax) radAtMax = ce[2];
            }
            foreach (AxialPlaneCandidate p in axialPlanes)
            {
                if (p.S <= minEndThresh && p.RadialMaxM > radAtMin) radAtMin = p.RadialMaxM;
                if (p.S >= maxEndThresh && p.RadialMaxM > radAtMax) radAtMax = p.RadialMaxM;
            }
            foreach (RadialConeCandidate kc in cones)
            {
                if (kc.SMin <= minEndThresh && kc.RadialMaxM > radAtMin) radAtMin = kc.RadialMaxM;
                if (kc.SMax >= maxEndThresh && kc.RadialMaxM > radAtMax) radAtMax = kc.RadialMaxM;
            }

            // Determine canonical localAxis direction
            double[] localAxis = new[] { uAxis[0], uAxis[1], uAxis[2] };
            AxialPlaneCandidate minOutFace = null, maxOutFace = null;
            foreach (AxialPlaneCandidate p in axialPlanes)
            {
                if (p.NormalSign < 0 && p.S <= bbSMin + 0.001)
                {
                    if (minOutFace == null || p.AreaM2 > minOutFace.AreaM2) minOutFace = p;
                }
                else if (p.NormalSign > 0 && p.S >= bbSMax - 0.001)
                {
                    if (maxOutFace == null || p.AreaM2 > maxOutFace.AreaM2) maxOutFace = p;
                }
            }

            if (partKind == "nut")
            {
                double areaMin = minOutFace != null ? minOutFace.AreaM2 : 0.0;
                double areaMax = maxOutFace != null ? maxOutFace.AreaM2 : 0.0;
                if (areaMax > areaMin * 1.05)
                    localAxis = new[] { -uAxis[0], -uAxis[1], -uAxis[2] };
            }
            else if (partKind == "bolt_or_screw")
            {
                // Canonical bolt axis points from head end -> tip end
                if (radAtMax > radAtMin + 0.0004)
                {
                    localAxis = new[] { -uAxis[0], -uAxis[1], -uAxis[2] };
                }
                else if (Math.Abs(radAtMax - radAtMin) <= 0.0004)
                {
                    double areaMin = minOutFace != null ? minOutFace.AreaM2 : 0.0;
                    double areaMax = maxOutFace != null ? maxOutFace.AreaM2 : 0.0;
                    if (areaMax > areaMin * 1.15)
                        localAxis = new[] { -uAxis[0], -uAxis[1], -uAxis[2] };
                }
            }

            // Re-project planes & cones onto canonical localAxis
            double axisFlip = Dot(localAxis, uAxis) >= 0 ? 1.0 : -1.0;
            foreach (AxialPlaneCandidate p in axialPlanes)
            {
                p.S = Dot(p.PointM, localAxis);
                p.NormalSign = Dot(p.Normal, localAxis) >= 0 ? 1 : -1;
            }
            foreach (RadialConeCandidate kc in cones)
            {
                double nsMin = kc.SMin * axisFlip;
                double nsMax = kc.SMax * axisFlip;
                kc.SMin = Math.Min(nsMin, nsMax);
                kc.SMax = Math.Max(nsMin, nsMax);
            }
            ComputeAxisProjections(localAxis, bb, out double cSMin, out double cSMax, out _);

            // Identify head-top (-localAxis) and tip (+localAxis) end planes
            AxialPlaneCandidate startPlane = null;
            AxialPlaneCandidate endPlane = null;
            foreach (AxialPlaneCandidate p in axialPlanes)
            {
                if (p.NormalSign < 0 && p.S <= cSMin + 0.0012)
                {
                    if (startPlane == null || p.S < startPlane.S - 0.00005 || (Math.Abs(p.S - startPlane.S) <= 0.00005 && p.AreaM2 > startPlane.AreaM2))
                        startPlane = p;
                }
                if (p.NormalSign > 0 && p.S >= cSMax - 0.0012)
                {
                    if (endPlane == null || p.S > endPlane.S + 0.00005 || (Math.Abs(p.S - endPlane.S) <= 0.00005 && p.AreaM2 > endPlane.AreaM2))
                        endPlane = p;
                }
            }

            double sStart = startPlane != null ? startPlane.S : cSMin;
            double sEnd = endPlane != null ? endPlane.S : cSMax;

            double bearingOffsetM = sStart;
            double oppositeOffsetM = sEnd;
            int bearingNormalSign = -1;
            string bearingToken = startPlane != null ? startPlane.Token : null;
            string oppositeToken = endPlane != null ? endPlane.Token : null;
            double measuredShankMm = 0.0;
            double measuredHeadMm = 0.0;
            double measuredThkMm = Math.Round((sEnd - sStart) * 1000.0, 2);

            if (partKind == "washer" || partKind == "nut")
            {
                bearingNormalSign = -1;
                measuredShankMm = 0.0;
                measuredHeadMm = 0.0;
                measuredThkMm = Math.Round((sEnd - sStart) * 1000.0, 2);
            }
            else
            {
                // Detect large CSK head cone near sStart
                bool hasCskCone = false;
                foreach (RadialConeCandidate kc in cones)
                {
                    if (kc.HalfAngleDeg >= 30.0 && kc.HalfAngleDeg <= 60.0 &&
                        0.5 * (kc.SMin + kc.SMax) < sStart + 0.45 * totalSpanM &&
                        kc.RadialMaxM >= 0.80 * maxPartRadialM)
                    {
                        hasCskCone = true;
                        break;
                    }
                }

                // Look for under-head bearing face strictly inside (sStart + 0.4mm, sStart + 0.50*totalSpan) with NormalSign == +1
                AxialPlaneCandidate underHead = null;
                if (!hasCskCone && !nameCsk)
                {
                    foreach (AxialPlaneCandidate p in axialPlanes)
                    {
                        if (p.NormalSign > 0 && p.S > sStart + 0.0004 && p.S < sStart + 0.50 * totalSpanM)
                        {
                            if (underHead == null || p.AreaM2 > underHead.AreaM2)
                                underHead = p;
                        }
                    }
                }

                if (underHead != null)
                {
                    partKind = "bolt_or_screw";
                    bearingOffsetM = underHead.S;
                    bearingNormalSign = 1;
                    bearingToken = underHead.Token;
                    oppositeOffsetM = sStart;
                    oppositeToken = startPlane != null ? startPlane.Token : null;
                    measuredShankMm = Math.Round((sEnd - underHead.S) * 1000.0, 2);
                    measuredHeadMm = Math.Round((underHead.S - sStart) * 1000.0, 2);
                    measuredThkMm = Math.Round((sEnd - sStart) * 1000.0, 2);
                }
                else if (hasCskCone || nameCsk)
                {
                    partKind = "csk_screw";
                    bearingOffsetM = sStart;
                    bearingNormalSign = -1;
                    bearingToken = startPlane != null ? startPlane.Token : null;
                    oppositeOffsetM = sEnd;
                    oppositeToken = endPlane != null ? endPlane.Token : null;
                    measuredShankMm = Math.Round((sEnd - sStart) * 1000.0, 2);
                    measuredHeadMm = 0.0;
                    measuredThkMm = measuredShankMm;
                }
                else
                {
                    partKind = "bolt_or_screw";
                    bearingOffsetM = sStart;
                    bearingNormalSign = -1;
                    measuredShankMm = Math.Round((sEnd - sStart) * 1000.0, 2);
                    measuredThkMm = measuredShankMm;
                }
            }

            double hexFlatAngle = 0.0;
            if (hasHexFlats)
            {
                double[] n0 = sideFaceNormals[0];
                double[] wUp = Math.Abs(localAxis[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
                double[] sZ = Normalize(Cross(wUp, localAxis));
                double[] sX = Normalize(Cross(localAxis, sZ));
                hexFlatAngle = Math.Atan2(Dot(n0, sZ), Dot(n0, sX)) * 180.0 / Math.PI;
            }

            return new FastenerPartProfile
            {
                PartPath = partPath,
                ActiveConfig = cfgName,
                PartKind = partKind,
                LocalAxis = localAxis,
                LocalBearingOffsetMm = Math.Round(bearingOffsetM * 1000.0, 4),
                LocalOppositeOffsetMm = Math.Round(oppositeOffsetM * 1000.0, 4),
                LocalBearingNormalSign = bearingNormalSign,
                MeasuredShankLengthMm = measuredShankMm,
                MeasuredHeadHeightMm = measuredHeadMm,
                MeasuredThicknessMm = measuredThkMm,
                MeasuredOdMm = Math.Round(outerDiaMm, 2),
                BoundingBoxMinMm = bb != null && bb.Length >= 6 ? new[] { Math.Round(bb[0] * 1000.0, 3), Math.Round(bb[1] * 1000.0, 3), Math.Round(bb[2] * 1000.0, 3) } : null,
                BoundingBoxMaxMm = bb != null && bb.Length >= 6 ? new[] { Math.Round(bb[3] * 1000.0, 3), Math.Round(bb[4] * 1000.0, 3), Math.Round(bb[5] * 1000.0, 3) } : null,
                BearingFaceToken = bearingToken,
                OppositeFaceToken = oppositeToken,
                HasHexFlats = hasHexFlats,
                HexFlatLocalAngleDeg = Math.Round(hexFlatAngle, 2)
            };
        }

        private static IFeature FindFeatureByTypeAndName(IModelDoc2 doc, string typeName, string name)
        {
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), typeName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static IFeature FindAnyRefAxis(IModelDoc2 doc)
        {
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "RefAxis", StringComparison.OrdinalIgnoreCase))
                    return f;
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static IFeature CreateFastenerAxisFeature(IModelDoc2 partDoc, double[] localAxis)
        {
            IPartDoc part = partDoc as IPartDoc;
            if (part == null) return null;

            // Strategy 1: Select largest coaxial outer cylindrical face centered on origin and insert axis
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies != null)
            {
                IFace2 bestCylFace = null;
                double bestArea = 0;
                foreach (object bObj in bodies)
                {
                    if (!(bObj is IBody2 b)) continue;
                    object[] faces = b.GetFaces() as object[];
                    if (faces == null) continue;
                    foreach (object fObj in faces)
                    {
                        if (!(fObj is IFace2 face)) continue;
                        ISurface surf = face.GetSurface() as ISurface;
                        if (surf == null || !surf.IsCylinder()) continue;
                        double[] cp = surf.CylinderParams as double[];
                        if (cp == null || cp.Length < 7) continue;
                        double[] fAxis = Normalize(new[] { cp[3], cp[4], cp[5] });
                        if (Math.Abs(Dot(fAxis, localAxis)) < 0.99) continue;
                        double area = face.GetArea();
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestCylFace = face;
                        }
                    }
                }
                if (bestCylFace is IEntity ent)
                {
                    partDoc.ClearSelection2(true);
                    if (ent.Select4(false, null) && partDoc.InsertAxis2(true))
                        return FindLastRefAxis(partDoc);
                }
            }

            // Strategy 2: Intersect two orthogonal standard reference planes whose normals are perpendicular to localAxis
            List<IFeature> refPlanes = new List<IFeature>();
            IFeature feat = partDoc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase))
                    refPlanes.Add(feat);
                feat = feat.GetNextFeature() as IFeature;
            }

            // Pair planes orthogonal to localAxis (e.g. Front+Right when axis is Y)
            if (refPlanes.Count >= 3)
            {
                partDoc.ClearSelection2(true);
                int idxA = 0, idxB = 2; // Standard order: Front(0), Top(1), Right(2)
                if (Math.Abs(localAxis[2]) > 0.9) { idxA = 1; idxB = 2; } // Axis Z -> Top & Right
                else if (Math.Abs(localAxis[0]) > 0.9) { idxA = 0; idxB = 1; } // Axis X -> Front & Top

                refPlanes[idxA].Select2(false, 0);
                refPlanes[idxB].Select2(true, 0);
                if (partDoc.InsertAxis2(true))
                    return FindLastRefAxis(partDoc);
            }
            return null;
        }

        private static IFeature FindLastRefAxis(IModelDoc2 doc)
        {
            IFeature last = null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "RefAxis", StringComparison.OrdinalIgnoreCase))
                    last = f;
                f = f.GetNextFeature() as IFeature;
            }
            return last;
        }

        public static int MoveComponentsToFolder(IModelDoc2 doc, IAssemblyDoc assy, IList<IComponent2> comps, string folderName)
        {
            if (doc == null || assy == null || comps == null || comps.Count == 0 || string.IsNullOrWhiteSpace(folderName))
                return 0;

            IFeature existingFolder = null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "FtrFolder", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    existingFolder = f;
                    break;
                }
                f = f.GetNextFeature() as IFeature;
            }

            if (existingFolder != null && existingFolder.GetSpecificFeature2() is IFeatureFolder folder)
            {
                object[] subFeats = folder.GetFeatures() as object[];
                IComponent2 anchorComp = null;
                if (subFeats != null)
                {
                    for (int i = subFeats.Length - 1; i >= 0; i--)
                    {
                        if (subFeats[i] is IFeature sf)
                        {
                            IComponent2 cand = assy.GetComponentByName(sf.Name) as IComponent2;
                            if (cand != null)
                            {
                                anchorComp = cand;
                                break;
                            }
                        }
                    }
                }

                if (anchorComp != null)
                {
                    List<object> toMove = new List<object>();
                    foreach (IComponent2 c in comps)
                    {
                        if (c != null && !string.Equals(c.Name2, anchorComp.Name2, StringComparison.OrdinalIgnoreCase))
                            toMove.Add(c);
                    }
                    if (toMove.Count > 0)
                    {
                        bool ok = assy.ReorderComponents(toMove.ToArray(), anchorComp, (int)swReorderComponentsWhere_e.swReorderComponents_After);
                        return ok ? toMove.Count : 0;
                    }
                    return 0;
                }
            }

            // Create new folder containing these components
            doc.ClearSelection2(true);
            int selCount = 0;
            foreach (IComponent2 c in comps)
            {
                if (c != null && c.Select4(selCount > 0, null, false))
                    selCount++;
            }
            if (selCount > 0)
            {
                IFeature created = doc.FeatureManager.InsertFeatureTreeFolder2((int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing);
                if (created != null)
                {
                    try { created.Name = folderName; } catch { }
                    return selCount;
                }
            }
            return 0;
        }

        public static bool TrySelectAssemblyTarget(IModelDoc2 assyDoc, IAssemblyDoc assy, string selector, bool append, int mark)
        {
            if (assyDoc == null || string.IsNullOrWhiteSpace(selector))
                return false;

            ISelectionMgr selMgr = assyDoc.SelectionManager as ISelectionMgr;
            SelectData selData = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
            if (selData != null) selData.Mark = mark;

            // Format 1: comp:<ComponentName>::<FeatureNameOrFaceToken>
            if (selector.StartsWith("comp:", StringComparison.OrdinalIgnoreCase))
            {
                int sep = selector.IndexOf("::", 5, StringComparison.Ordinal);
                if (sep > 5)
                {
                    string compName = selector.Substring(5, sep - 5);
                    string inner = selector.Substring(sep + 2);
                    IComponent2 comp = FindComponentByName(assy, compName);
                    if (comp == null) return false;

                    IModelDoc2 compDoc = comp.GetModelDoc2() as IModelDoc2;
                    if (inner.StartsWith("face:", StringComparison.OrdinalIgnoreCase) && compDoc != null)
                    {
                        IFace2 f = SwConnectionManager.FindFaceByToken(compDoc, inner);
                        if (f != null && comp.GetCorrespondingEntity(f) is IEntity ent)
                            return ent.Select4(append, selData);
                    }
                    if (inner.StartsWith("edge:", StringComparison.OrdinalIgnoreCase) && compDoc != null)
                    {
                        IEdge e = SwConnectionManager.FindEdgeByToken(compDoc, inner);
                        if (e != null && comp.GetCorrespondingEntity(e) is IEntity ent)
                            return ent.Select4(append, selData);
                    }
                    IFeature compFeat = comp.FeatureByName(inner);
                    if (compFeat != null)
                        return compFeat.Select2(append, mark);
                }
            }

            // Format 2: standard SelectByID2 string (e.g. Fastener_Axis@Part-1@Assy or Front Plane)
            string[] types = new[] { "AXIS", "PLANE", "FACE", "EDGE", "DATUMPOINT", "COORDSYS", "" };
            foreach (string t in types)
            {
                if (assyDoc.Extension.SelectByID2(selector, t, 0, 0, 0, append, mark, null, 0))
                    return true;
            }
            return SwConnectionManager.TrySelectByToken(assyDoc, selector, append, mark);
        }

        public static IComponent2 FindComponentByName(IAssemblyDoc assy, string name)
        {
            if (assy == null || string.IsNullOrWhiteSpace(name)) return null;
            IComponent2 direct = assy.GetComponentByName(name) as IComponent2;
            if (direct != null) return direct;

            object[] comps = assy.GetComponents(false) as object[];
            if (comps == null) return null;
            foreach (object cObj in comps)
            {
                if (cObj is IComponent2 c && string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        public static IMate2 CreateMateBySelectors(
            IModelDoc2 assyDoc,
            IAssemblyDoc assy,
            string selectorA,
            string selectorB,
            int mateType,
            int align,
            bool flip,
            double distanceM,
            double angleRad,
            bool lockRotation,
            out int errCode)
        {
            errCode = -1;
            if (assyDoc == null || assy == null) return null;

            assyDoc.ClearSelection2(true);
            if (!TrySelectAssemblyTarget(assyDoc, assy, selectorA, false, 1))
                return null;
            if (!TrySelectAssemblyTarget(assyDoc, assy, selectorB, true, 1))
            {
                assyDoc.ClearSelection2(true);
                return null;
            }

            IMate2 mate = assy.AddMate5(
                mateType,
                align,
                flip,
                distanceM, distanceM, distanceM,
                1.0, 1.0,
                angleRad, angleRad, angleRad,
                false,
                lockRotation,
                0,
                out errCode);

            assyDoc.ClearSelection2(true);
            return mate;
        }

        public static void AutoMateFastenerAtPose(
            IModelDoc2 assyDoc,
            IAssemblyDoc assy,
            IMathUtility mathUtil,
            IComponent2 hwComp,
            double[] bearingPosM,
            double[] targetAxis,
            double localBearingOffsetM,
            bool lockRotation,
            out int concentricErr,
            out int coincidentErr,
            bool needConcentric = true,
            bool needCoincident = true)
        {
            concentricErr = -1;
            coincidentErr = -1;
            if (assyDoc == null || assy == null || mathUtil == null || hwComp == null || (!needConcentric && !needCoincident))
                return;

            IMathTransform hXf = hwComp.Transform2;
            double[] xfData = hXf != null ? hXf.ArrayData as double[] : null;
            if (xfData == null || xfData.Length < 12)
                return;

            double[] gAxis = targetAxis != null && targetAxis.Length >= 3
                ? Normalize(targetAxis)
                : Normalize(new[] { xfData[3], xfData[4], xfData[5] });

            double[] gBear = bearingPosM != null && bearingPosM.Length >= 3
                ? bearingPosM
                : new[]
                {
                    xfData[9] + localBearingOffsetM * gAxis[0],
                    xfData[10] + localBearingOffsetM * gAxis[1],
                    xfData[11] + localBearingOffsetM * gAxis[2]
                };

            if (hwComp.IsFixed())
            {
                assyDoc.ClearSelection2(true);
                hwComp.Select4(false, null, false);
                assy.UnfixComponent();
                assyDoc.ClearSelection2(true);
            }

            // Locate Fastener_Axis / fallback cylinder AND bottom bearing face across ALL bodies on hwComp
            IFeature hwAxisFeat = hwComp.FeatureByName("Fastener_Axis");
            IFace2 hwCylFace = null;
            double hwCylArea = -1.0;
            IFace2 hwBearFace = null;
            double[] hwBearNorm = null;
            double hwBearMinProj = double.MaxValue;
            double hwBearMaxArea = -1.0;

            if (hwComp.GetModelDoc2() is IPartDoc hwPart)
            {
                object[] bodies = hwPart.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies != null)
                {
                    foreach (object bObj in bodies)
                    {
                        if (!(bObj is IBody2 body)) continue;
                        object[] faces = body.GetFaces() as object[];
                        if (faces == null) continue;
                        foreach (object fObj in faces)
                        {
                            if (!(fObj is IFace2 f)) continue;
                            ISurface surf = f.GetSurface() as ISurface;
                            if (surf == null) continue;

                            if (surf.IsCylinder())
                            {
                                double[] cp = surf.CylinderParams as double[];
                                if (cp != null && cp.Length >= 7)
                                {
                                    double[] ga = TransformVectorFast(xfData, cp[3], cp[4], cp[5]);
                                    if (Math.Abs(Dot(ga, gAxis)) > 0.98)
                                    {
                                        double area = f.GetArea();
                                        if (area > hwCylArea)
                                        {
                                            hwCylArea = area;
                                            hwCylFace = f;
                                        }
                                    }
                                }
                            }
                            else if (surf.IsPlane())
                            {
                                double[] pp = surf.PlaneParams as double[];
                                if (pp != null && pp.Length >= 6)
                                {
                                    double[] ln = new[] { pp[0], pp[1], pp[2] };
                                    if (f.FaceInSurfaceSense()) { ln[0] = -ln[0]; ln[1] = -ln[1]; ln[2] = -ln[2]; }
                                    double[] fgNorm = TransformVectorFast(xfData, ln[0], ln[1], ln[2]);
                                    // Accept bearing faces aligned either opposing (-gAxis: CSK/washer/nut) or along (+gAxis: bolt under-head)
                                    if (Math.Abs(Dot(fgNorm, gAxis)) > 0.98)
                                    {
                                        double[] gp = TransformPointFast(xfData, pp[3], pp[4], pp[5]);
                                        double proj = Dot(new[] { gp[0] - xfData[9], gp[1] - xfData[10], gp[2] - xfData[11] }, gAxis);
                                        double diffProj = Math.Abs(proj - localBearingOffsetM);
                                        double area = f.GetArea();
                                        if (diffProj < hwBearMinProj - 0.00002 ||
                                            (Math.Abs(diffProj - hwBearMinProj) <= 0.00002 && area > hwBearMaxArea))
                                        {
                                            hwBearMinProj = diffProj;
                                            hwBearMaxArea = area;
                                            hwBearFace = f;
                                            hwBearNorm = fgNorm;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Scan assembly components for best coaxial structural hole and best coplanar bearing face
            bool hwIsWasher = (hwComp.Name2 ?? "").IndexOf("Washer", StringComparison.OrdinalIgnoreCase) >= 0;
            IComponent2 holeOwner = null;
            IFace2 bestHoleFace = null;
            double bestHoleScore = double.MaxValue;

            IEntity bestWasherPlaneEnt = null;
            double bestWasherDist = 0.0006;

            List<object[]> structPlaneCandidates = new List<object[]>();

            object[] allComps = assy.GetComponents(true) as object[];
            if (allComps != null)
            {
                foreach (object cObj in allComps)
                {
                    if (!(cObj is IComponent2 cand) || cand.IsSuppressed()) continue;
                    if (string.Equals(cand.Name2, hwComp.Name2, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(cand.GetModelDoc2() is IPartDoc cPart)) continue;

                    bool candIsFastener = IsMcMasterOrFastenerComponent(cand);
                    bool candIsWasher = candIsFastener && ((cand.Name2 ?? "").IndexOf("Washer", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (candIsFastener && !candIsWasher) continue;

                    IMathTransform cXf = cand.Transform2;
                    double[] cMat = cXf != null ? cXf.ArrayData as double[] : null;

                    // Quick AABB rejection before scanning any faces
                    double[] cPartBox = cPart.GetPartBox(true) as double[];
                    if (cPartBox != null && cPartBox.Length >= 6)
                    {
                        TransformAabbFast(cMat, cPartBox, out double[] cMin, out double[] cMax);
                        double padC = 0.050;
                        if (gBear[0] < cMin[0] - padC || gBear[0] > cMax[0] + padC ||
                            gBear[1] < cMin[1] - padC || gBear[1] > cMax[1] + padC ||
                            gBear[2] < cMin[2] - padC || gBear[2] > cMax[2] + padC)
                            continue;
                    }

                    object[] bodies = cPart.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    if (bodies == null) continue;

                    foreach (object bObj in bodies)
                    {
                        if (!(bObj is IBody2 body)) continue;
                        object[] faces = body.GetFaces() as object[];
                        if (faces == null) continue;

                        foreach (object fObj in faces)
                        {
                            if (!(fObj is IFace2 f)) continue;
                            ISurface surf = f.GetSurface() as ISurface;
                            if (surf == null) continue;

                            if (!candIsFastener && surf.IsCylinder() && f.FaceInSurfaceSense())
                            {
                                double[] cp = surf.CylinderParams as double[];
                                if (cp == null || cp.Length < 7) continue;
                                double[] hOrig = TransformPointFast(cMat, cp[0], cp[1], cp[2]);
                                double[] hAxis = TransformVectorFast(cMat, cp[3], cp[4], cp[5]);
                                if (Math.Abs(Dot(hAxis, gAxis)) < 0.99) continue;

                                double dx = gBear[0] - hOrig[0], dy = gBear[1] - hOrig[1], dz = gBear[2] - hOrig[2];
                                double proj = dx * hAxis[0] + dy * hAxis[1] + dz * hAxis[2];
                                double px = dx - proj * hAxis[0], py = dy - proj * hAxis[1], pz = dz - proj * hAxis[2];
                                double perp = Math.Sqrt(px * px + py * py + pz * pz);
                                if (perp > 0.0005) continue;

                                double[] fBox = f.GetBox() as double[];
                                double minS = double.MaxValue, maxS = double.MinValue;
                                if (fBox != null && fBox.Length >= 6)
                                {
                                    for (int ix = 0; ix <= 1; ix++)
                                    for (int iy = 0; iy <= 1; iy++)
                                    for (int iz = 0; iz <= 1; iz++)
                                    {
                                        double[] cp3 = TransformPointFast(cMat,
                                            ix == 0 ? fBox[0] : fBox[3],
                                            iy == 0 ? fBox[1] : fBox[4],
                                            iz == 0 ? fBox[2] : fBox[5]);
                                        double sVal = Dot(cp3, hAxis);
                                        if (sVal < minS) minS = sVal;
                                        if (sVal > maxS) maxS = sVal;
                                    }
                                }
                                double bearS = Dot(gBear, hAxis);
                                double axDist = (bearS < minS) ? (minS - bearS) : ((bearS > maxS) ? (bearS - maxS) : 0.0);
                                double score = axDist * 100.0 + perp;
                                if (score < bestHoleScore)
                                {
                                    bestHoleScore = score;
                                    holeOwner = cand;
                                    bestHoleFace = f;
                                }
                            }
                            else if (surf.IsPlane())
                            {
                                double[] pp = surf.PlaneParams as double[];
                                if (pp == null || pp.Length < 6) continue;
                                double[] ln = new[] { pp[0], pp[1], pp[2] };
                                if (f.FaceInSurfaceSense()) { ln[0] = -ln[0]; ln[1] = -ln[1]; ln[2] = -ln[2]; }
                                double[] gn = TransformVectorFast(cMat, ln[0], ln[1], ln[2]);
                                if (hwBearNorm != null ? Dot(gn, hwBearNorm) > -0.98 : Dot(gn, gAxis) < 0.99) continue;

                                double[] gp = TransformPointFast(cMat, pp[3], pp[4], pp[5]);
                                if (candIsWasher)
                                {
                                    if (!hwIsWasher)
                                    {
                                        double[] diff = new[] { gBear[0] - gp[0], gBear[1] - gp[1], gBear[2] - gp[2] };
                                        double dist = Math.Sqrt(Dot(diff, diff));
                                        if (dist < bestWasherDist)
                                        {
                                            bestWasherDist = dist;
                                            bestWasherPlaneEnt = cand.GetCorrespondingEntity(f) as IEntity;
                                        }
                                    }
                                }
                                else if (!candIsFastener)
                                {
                                    double pDist = Math.Abs((gBear[0] - gp[0]) * gn[0] + (gBear[1] - gp[1]) * gn[1] + (gBear[2] - gp[2]) * gn[2]);
                                    if (pDist > 0.0002) continue;

                                    double[] fBox = f.GetBox() as double[];
                                    if (fBox != null && fBox.Length >= 6)
                                    {
                                        TransformAabbFast(cMat, fBox, out double[] bMin, out double[] bMax);
                                        double pad = 0.008;
                                        if (gBear[0] >= bMin[0] - pad && gBear[0] <= bMax[0] + pad &&
                                            gBear[1] >= bMin[1] - pad && gBear[1] <= bMax[1] + pad &&
                                            gBear[2] >= bMin[2] - pad && gBear[2] <= bMax[2] + pad)
                                        {
                                            structPlaneCandidates.Add(new object[] { cand, f, pDist });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            ISelectionMgr selMgr = assyDoc.SelectionManager as ISelectionMgr;
            SelectData sd = selMgr != null ? selMgr.CreateSelectData() as SelectData : null;
            if (sd != null) sd.Mark = 1;

            // Create Concentric Mate
            if (needConcentric && holeOwner != null && bestHoleFace != null && (hwAxisFeat != null || hwCylFace != null))
            {
                IEntity holeEnt = holeOwner.GetCorrespondingEntity(bestHoleFace) as IEntity;
                if (holeEnt != null)
                {
                    assyDoc.ClearSelection2(true);
                    bool s1 = hwAxisFeat != null
                        ? hwAxisFeat.Select2(false, 1)
                        : ((hwComp.GetCorrespondingEntity(hwCylFace) as IEntity)?.Select4(false, sd) ?? false);
                    bool s2 = holeEnt.Select4(true, sd);
                    if (s1 && s2)
                    {
                        assy.AddMate5(
                            (int)swMateType_e.swMateCONCENTRIC,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            false, 0, 0, 0, 1, 1, 0, 0, 0, false, lockRotation, 0, out concentricErr);
                    }
                    assyDoc.ClearSelection2(true);
                }
            }

            // Create Coincident Mate
            IEntity targetBearEnt = bestWasherPlaneEnt;
            if (needCoincident && targetBearEnt == null && structPlaneCandidates.Count > 0)
            {
                double bestPlScore = double.MaxValue;
                foreach (object[] sp in structPlaneCandidates)
                {
                    double pDist = (double)sp[2];
                    double sc = pDist + (holeOwner != null && sp[0] == holeOwner ? 0.0 : 0.001);
                    if (sc < bestPlScore)
                    {
                        bestPlScore = sc;
                        targetBearEnt = ((IComponent2)sp[0]).GetCorrespondingEntity((IFace2)sp[1]) as IEntity;
                    }
                }
            }

            if (needCoincident && hwBearFace != null && targetBearEnt != null)
            {
                IEntity hwBearEnt = hwComp.GetCorrespondingEntity(hwBearFace) as IEntity;
                if (hwBearEnt != null)
                {
                    assyDoc.ClearSelection2(true);
                    if (hwBearEnt.Select4(false, sd) && targetBearEnt.Select4(true, sd))
                    {
                        assy.AddMate5(
                            (int)swMateType_e.swMateCOINCIDENT,
                            (int)swMateAlign_e.swMateAlignCLOSEST,
                            false, 0, 0, 0, 1, 1, 0, 0, 0, false, false, 0, out coincidentErr);
                    }
                    assyDoc.ClearSelection2(true);
                }
            }
        }
    }
}
