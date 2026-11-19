using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    public class ShopCatalogService
    {
        public Dictionary<string, object> ShopProfile { get; private set; }
        public Dictionary<string, object> SendCutSendCatalog { get; private set; }
        public string DataDirectory { get; private set; }

        public ShopCatalogService()
        {
            DataDirectory = ResolveDataDirectory();
            ShopProfile = LoadJsonFile(Path.Combine(DataDirectory, "shop_profile.json"));
            SendCutSendCatalog = LoadJsonFile(Path.Combine(DataDirectory, "sendcutsend_catalog.json"));
        }

        private static string ResolveDataDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> candidates = new List<string>
            {
                Path.Combine(baseDir, "data"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "data")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "data")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".agents", "plugins", "solidworks", "data")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", ".agents", "plugins", "solidworks", "data")),
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".gemini", "config", "plugins", "solidworks", "data")
            };

            foreach (string c in candidates)
            {
                if (Directory.Exists(c) && File.Exists(Path.Combine(c, "shop_profile.json")))
                    return c;
            }
            return candidates[0];
        }

        private static Dictionary<string, object> LoadJsonFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonHelper.DeserializeDict(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                McpStdioHost.Log("Failed to load catalog " + path + ": " + ex.Message);
            }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public static string GetPreferredUnitSystemForDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
                return "IPS";

            string d = domain.Trim().ToLowerInvariant();
            if (d == "3d_printing" || d == "3d_print" || d == "gridfinity" || d == "electronics" || d == "electronics_kicad" || d == "kicad" || d == "mmgs" || d == "mm")
                return "MMGS";

            return "IPS";
        }

        public static double ToMeters(double value, string units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return value * 0.0254;
            string u = units.Trim().ToLowerInvariant();
            if (u == "mm" || u == "millimeter" || u == "millimeters" || u == "mmgs")
                return value * 0.001;
            if (u == "in" || u == "inch" || u == "inches" || u == "ips")
                return value * 0.0254;
            if (u == "m" || u == "meter" || u == "meters" || u == "mks")
                return value;
            return value * 0.0254;
        }

        public static double FromMeters(double meters, string units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return meters / 0.0254;
            string u = units.Trim().ToLowerInvariant();
            if (u == "mm" || u == "millimeter" || u == "millimeters" || u == "mmgs")
                return meters * 1000.0;
            if (u == "in" || u == "inch" || u == "inches" || u == "ips")
                return meters / 0.0254;
            return meters;
        }

        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        public static double FromRadians(double radians) => radians * 180.0 / Math.PI;

        public Dictionary<string, object> FindMcMasterInsert(string partNumberOrThread)
        {
            Dictionary<string, object> p3d = JsonHelper.GetDict(ShopProfile, "printing_3d");
            List<object> inserts = JsonHelper.GetList(p3d, "heat_set_inserts_mcmaster");
            string q = (partNumberOrThread ?? string.Empty).Trim().ToUpperInvariant();

            foreach (object obj in inserts)
            {
                if (obj is Dictionary<string, object> d)
                {
                    string pn = JsonHelper.GetString(d, "mcmaster_pn", string.Empty).ToUpperInvariant();
                    string thread = JsonHelper.GetString(d, "thread", string.Empty).ToUpperInvariant();
                    if (pn == q || thread.StartsWith(q, StringComparison.OrdinalIgnoreCase) || q.Contains(pn))
                        return d;
                }
            }
            return null;
        }

        public bool TryGetSendCutSendGauge(string materialKey, double thicknessIn, out Dictionary<string, object> materialInfo, out Dictionary<string, object> gaugeInfo)
        {
            materialInfo = null;
            gaugeInfo = null;

            Dictionary<string, object> mats = JsonHelper.GetDict(SendCutSendCatalog, "materials");
            if (mats == null || string.IsNullOrWhiteSpace(materialKey))
                return false;

            Dictionary<string, object> matDict = null;
            if (!mats.TryGetValue(materialKey, out object matObj) || !(matObj is Dictionary<string, object> foundDict))
            {
                // Fuzzy match
                foreach (var kv in mats)
                {
                    if (kv.Key.IndexOf(materialKey, StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value is Dictionary<string, object> md)
                    {
                        matDict = md;
                        break;
                    }
                }
                if (matDict == null)
                    return false;
            }
            else
            {
                matDict = foundDict;
            }

            materialInfo = matDict;
            List<object> gauges = JsonHelper.GetList(matDict, "gauges");
            Dictionary<string, object> best = null;
            double bestDiff = double.MaxValue;
            foreach (object gObj in gauges)
            {
                if (gObj is Dictionary<string, object> g)
                {
                    double t = JsonHelper.GetDouble(g, "thickness_in", 0.0);
                    double diff = Math.Abs(t - thicknessIn);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = g;
                    }
                }
            }

            if (best != null && bestDiff <= 0.015)
            {
                gaugeInfo = best;
                return true;
            }

            return false;
        }

        public static string NormalizeUnitsLabel(string units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return "in";
            string u = units.Trim().ToLowerInvariant();
            if (u == "mm" || u == "millimeter" || u == "millimeters" || u == "mmgs")
                return "mm";
            if (u == "m" || u == "meter" || u == "meters" || u == "mks")
                return "m";
            return "in";
        }
    }
}
