using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Core;

namespace SolidWorks3DPrintDFMAddin.Data
{
    [Serializable]
    public class TeardropHoleTarget
    {
        public string HoleId { get; set; }
        public Vector3D AxisOrigin { get; set; }
        public Vector3D AxisUnit { get; set; }
        public double RadiusMeters { get; set; }
        public double AxialMinMeters { get; set; }
        public double AxialMaxMeters { get; set; }
        public double CircleAngleToBedDeg { get; set; }
        public string ManagedCutFeatureName { get; set; }
        public string ManagedSketchName { get; set; }
        public string ManagedRefPlaneName { get; set; }

        public double DiameterMm => RadiusMeters * 2000.0;
        public double LengthMm => (AxialMaxMeters - AxialMinMeters) * 1000.0;
    }

    [Serializable]
    public class InnerHole2D
    {
        public Point2D Center { get; set; }
        public double RadiusMeters { get; set; }
        public bool IsCircular { get; set; } = true;
        public List<Point2D> Polygon2D { get; set; } = new List<Point2D>();
    }

    [Serializable]
    public class CrisscrossBridgeTarget
    {
        public string RecessId { get; set; }
        public Vector3D PlaneOrigin { get; set; }
        public Vector3D OutwardNormal { get; set; }
        public bool IsCircularOuter { get; set; }
        public Point2D OuterCircleCenter2D { get; set; }
        public double OuterCircleRadiusMeters { get; set; }
        public List<Point2D> OuterLoopPolygon2D { get; set; } = new List<Point2D>();
        public List<BoundingBox2D> InnerHoleBoxes2D { get; set; } = new List<BoundingBox2D>();
        public List<InnerHole2D> InnerHoles2D { get; set; } = new List<InnerHole2D>();
        public string ManagedRefPlaneName { get; set; }
        public string ManagedTier1CutName { get; set; }
        public string ManagedTier1SketchName { get; set; }
        public string ManagedTier2CutName { get; set; }
        public string ManagedTier2SketchName { get; set; }
        public List<string> ManagedCutNames { get; set; } = new List<string>();
        public List<string> ManagedSketchNames { get; set; } = new List<string>();
    }

    [Serializable]
    public class DfmConfig
    {
        // Unique ID for reliable per-feature XML & CustomProperty persistence
        public string FeatureConfigId { get; set; } = Guid.NewGuid().ToString("D");
        public string ManagedMacroFeatureName { get; set; }

        // Bed orientation
        public Vector3D BedUpVector { get; set; } = new Vector3D(0, 0, 1);
        public Vector3D BedReferencePoint { get; set; } = new Vector3D(0, 0, 0);
        public bool FlipBedDirection { get; set; } = false;

        // Persistent COM entity references so Edit Feature restores exact selections
        public byte[] BedRefPersistId { get; set; }
        public List<byte[]> BodyPersistIds { get; set; } = new List<byte[]>();
        public List<byte[]> TeardropFacePersistIds { get; set; } = new List<byte[]>();
        public List<byte[]> BridgeFacePersistIds { get; set; } = new List<byte[]>();

        // Teardrop settings
        public bool EnableTeardrops { get; set; } = true;
        public double OverhangAngleFromBedDeg { get; set; } = 40.0;
        public bool EnableMaxHeightCap { get; set; } = true;
        public double MaxCapHeightMeters { get; set; } = 0.0006; // 0.6 mm along BedUpVector
        public double MinCircleAngleToBedDeg { get; set; } = 40.0; // 40° to 90°
        public double MinHoleDiameterMeters { get; set; } = 0.0015; // 1.5 mm

        // Integral Crisscross Bridge settings
        public bool EnableCrisscrossBridges { get; set; } = true;
        public double LayerHeightMeters { get; set; } = 0.0002; // 0.20 mm
        public double BridgeAngleOffsetDeg { get; set; } = 0.0;

        // Stored targets for Hole-Wizard style MacroFeature re-editing & delete cleanup
        public List<TeardropHoleTarget> TeardropTargets { get; set; } = new List<TeardropHoleTarget>();
        public List<CrisscrossBridgeTarget> BridgeTargets { get; set; } = new List<CrisscrossBridgeTarget>();
        public string ManagedFolderName { get; set; } = "3D Print DFM";

        [XmlIgnore]
        public double MaxCapHeightMm
        {
            get => MaxCapHeightMeters * 1000.0;
            set => MaxCapHeightMeters = value / 1000.0;
        }

        [XmlIgnore]
        public double LayerHeightMm
        {
            get => LayerHeightMeters * 1000.0;
            set => LayerHeightMeters = value / 1000.0;
        }

        private static string GetAppDataDir()
        {
            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SolidWorks3DPrintDFMAddin");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(GetAppDataDir(), "dfm_settings.xml");
        }

        public static string GetFeatureStorageDir()
        {
            string dir = Path.Combine(GetAppDataDir(), "Features");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }

        public static DfmConfig LoadDefaults()
        {
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(DfmConfig));
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        DfmConfig loaded = (DfmConfig)serializer.Deserialize(fs);
                        if (loaded != null)
                        {
                            loaded.FeatureConfigId = Guid.NewGuid().ToString("D");
                            loaded.ManagedMacroFeatureName = null;
                            loaded.BedRefPersistId = null;
                            loaded.BodyPersistIds = new List<byte[]>();
                            loaded.TeardropFacePersistIds = new List<byte[]>();
                            loaded.BridgeFacePersistIds = new List<byte[]>();
                            loaded.TeardropTargets = new List<TeardropHoleTarget>();
                            loaded.BridgeTargets = new List<CrisscrossBridgeTarget>();
                            return loaded;
                        }
                    }
                }
            }
            catch { }

            return new DfmConfig();
        }

        public void SaveAsDefaults()
        {
            try
            {
                DfmConfig copy = new DfmConfig
                {
                    EnableTeardrops = this.EnableTeardrops,
                    OverhangAngleFromBedDeg = this.OverhangAngleFromBedDeg,
                    EnableMaxHeightCap = this.EnableMaxHeightCap,
                    MaxCapHeightMeters = this.MaxCapHeightMeters,
                    MinCircleAngleToBedDeg = this.MinCircleAngleToBedDeg,
                    MinHoleDiameterMeters = this.MinHoleDiameterMeters,
                    EnableCrisscrossBridges = this.EnableCrisscrossBridges,
                    LayerHeightMeters = this.LayerHeightMeters,
                    BridgeAngleOffsetDeg = this.BridgeAngleOffsetDeg
                };

                string path = GetSettingsFilePath();
                XmlSerializer serializer = new XmlSerializer(typeof(DfmConfig));
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    serializer.Serialize(fs, copy);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Saves and loads per-feature DfmConfig snapshots both to %AppData%\SolidWorks3DPrintDFMAddin\Features\<id>.xml
    /// and to the active Part's CustomPropertyManager so Edit Feature always restores exact parameters & selections.
    /// </summary>
    public static class DfmConfigStore
    {
        private static readonly Dictionary<string, DfmConfig> _memoryCache = new Dictionary<string, DfmConfig>();

        public static void SaveFeatureConfig(IModelDoc2 model, DfmConfig config)
        {
            if (config == null) return;
            if (string.IsNullOrEmpty(config.FeatureConfigId))
                config.FeatureConfigId = Guid.NewGuid().ToString("D");

            try
            {
                _memoryCache[config.FeatureConfigId] = config;

                XmlSerializer serializer = new XmlSerializer(typeof(DfmConfig));
                string xml;
                using (StringWriter sw = new StringWriter())
                {
                    serializer.Serialize(sw, config);
                    xml = sw.ToString();
                }

                string filePath = Path.Combine(DfmConfig.GetFeatureStorageDir(), config.FeatureConfigId + ".xml");
                File.WriteAllText(filePath, xml, Encoding.UTF8);

                if (model != null)
                {
                    ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                    if (propMgr != null)
                    {
                        string propName = "DFM_Cfg_" + config.FeatureConfigId;
                        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
                        propMgr.Add3(propName, (int)swCustomInfoType_e.swCustomInfoText, b64,
                            (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("DfmConfigStore.SaveFeatureConfig error: " + ex);
            }
        }

        public static DfmConfig LoadFeatureConfig(IModelDoc2 model, string featureConfigId, string macroFeatureName = null)
        {
            if (!string.IsNullOrEmpty(featureConfigId))
            {
                if (_memoryCache.TryGetValue(featureConfigId, out DfmConfig cached))
                    return cached;

                try
                {
                    string filePath = Path.Combine(DfmConfig.GetFeatureStorageDir(), featureConfigId + ".xml");
                    if (File.Exists(filePath))
                    {
                        DfmConfig fromDisk = DeserializeXml(File.ReadAllText(filePath, Encoding.UTF8));
                        if (fromDisk != null)
                        {
                            _memoryCache[featureConfigId] = fromDisk;
                            return fromDisk;
                        }
                    }
                }
                catch { }

                if (model != null)
                {
                    try
                    {
                        ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                        if (propMgr != null)
                        {
                            string propName = "DFM_Cfg_" + featureConfigId;
                            if (propMgr.Get4(propName, false, out string valOut, out string resolved) &&
                                !string.IsNullOrEmpty(valOut))
                            {
                                string xml = Encoding.UTF8.GetString(Convert.FromBase64String(valOut));
                                DfmConfig fromProp = DeserializeXml(xml);
                                if (fromProp != null)
                                {
                                    _memoryCache[featureConfigId] = fromProp;
                                    return fromProp;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // Fallback: search by ManagedMacroFeatureName in memory cache
            if (!string.IsNullOrEmpty(macroFeatureName))
            {
                foreach (DfmConfig c in _memoryCache.Values)
                {
                    if (string.Equals(c.ManagedMacroFeatureName, macroFeatureName, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }

            return null;
        }

        public static List<DfmConfig> GetAllConfigsForModel(IModelDoc2 model)
        {
            List<DfmConfig> results = new List<DfmConfig>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DfmConfig c in _memoryCache.Values)
            {
                if (!string.IsNullOrEmpty(c.FeatureConfigId) && seenIds.Add(c.FeatureConfigId))
                    results.Add(c);
            }

            if (model != null)
            {
                try
                {
                    ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                    object rawNames = propMgr?.GetNames();
                    if (rawNames is object[] names)
                    {
                        foreach (object nObj in names)
                        {
                            string propName = nObj as string;
                            if (string.IsNullOrEmpty(propName) || !propName.StartsWith("DFM_Cfg_"))
                                continue;

                            string id = propName.Substring("DFM_Cfg_".Length);
                            if (seenIds.Contains(id)) continue;

                            DfmConfig cfg = LoadFeatureConfig(model, id);
                            if (cfg != null && seenIds.Add(id))
                                results.Add(cfg);
                        }
                    }
                }
                catch { }
            }

            return results;
        }

        public static void DeleteFeatureConfig(IModelDoc2 model, DfmConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.FeatureConfigId)) return;
            try
            {
                _memoryCache.Remove(config.FeatureConfigId);

                string filePath = Path.Combine(DfmConfig.GetFeatureStorageDir(), config.FeatureConfigId + ".xml");
                if (File.Exists(filePath))
                    File.Delete(filePath);

                if (model != null)
                {
                    ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                    propMgr?.Delete2("DFM_Cfg_" + config.FeatureConfigId);
                }
            }
            catch { }
        }

        private static DfmConfig DeserializeXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(DfmConfig));
                using (StringReader sr = new StringReader(xml))
                {
                    return (DfmConfig)serializer.Deserialize(sr);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
