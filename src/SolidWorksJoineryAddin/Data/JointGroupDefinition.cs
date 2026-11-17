using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksJoineryAddin.Data
{
    [Serializable]
    public class JointGroupDefinition
    {
        public const int CURRENT_FEATURE_VERSION = 1;

        // Backward-compatibility schema/algorithm marker (0 = legacy unversioned XML; 1+ = versioned)
        public int FeatureSchemaVersion { get; set; } = 0;

        public string FeatureConfigId { get; set; } = Guid.NewGuid().ToString("D");
        public string ManagedMacroFeatureName { get; set; }
        public string FeatureName { get; set; } = "Multi-Body Joinery";

        // Global defaults applied across all joints in this feature
        public JointType DefaultType { get; set; } = JointType.TabAndSlot;
        public int DefaultTabCount { get; set; } = 3;
        public double DefaultTabWidth { get; set; } = 0.0254; // 1.0 inch in meters
        public double DefaultToolBitRadius { get; set; } = 0.00635; // 0.25 inch in meters
        public double DefaultSignedClearance { get; set; } = 0.000127; // +0.005 inch in meters
        public bool MasterEnableDogbones { get; set; } = true;
        public CornerReliefType DefaultReliefType { get; set; } = CornerReliefType.DiagonalDogbone;
        public bool MasterEnableKeying { get; set; } = true;
        public double DefaultKeyDelta { get; set; } = 0.0127; // 0.5 inch in meters
        public DisambiguationChoice DisambiguationFallback { get; set; } = DisambiguationChoice.DisambiguateAsManyAsPossible;

        // Persistent COM entity references so Edit Feature restores exact selections
        public List<byte[]> BodyPersistIds { get; set; } = new List<byte[]>();
        public List<byte[]> SelectedFacePersistIds { get; set; } = new List<byte[]>();

        // Individual joint definitions
        public List<JointDefinition> Joints { get; set; } = new List<JointDefinition>();

        public static string GetAppDataDir()
        {
            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SolidWorksJoineryAddin");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
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
    }

    /// <summary>
    /// Saves and loads per-feature JointGroupDefinition snapshots both to
    /// %AppData%\SolidWorksJoineryAddin\Features\<id>.xml and to the active Part's
    /// CustomPropertyManager so Edit Feature always restores exact parameters & selections.
    /// </summary>
    public static class JointConfigStore
    {
        private const string PROP_PREFIX = "Joinery_Cfg_";
        private static readonly Dictionary<string, JointGroupDefinition> _memoryCache = new Dictionary<string, JointGroupDefinition>();

        public static string SerializeToXml(JointGroupDefinition group)
        {
            if (group == null) return string.Empty;
            XmlSerializer serializer = new XmlSerializer(typeof(JointGroupDefinition));
            using (StringWriter sw = new StringWriter())
            {
                serializer.Serialize(sw, group);
                return sw.ToString();
            }
        }

        public static JointGroupDefinition DeserializeXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(JointGroupDefinition));
                using (StringReader sr = new StringReader(xml))
                {
                    return (JointGroupDefinition)serializer.Deserialize(sr);
                }
            }
            catch
            {
                return null;
            }
        }

        public static void SaveFeatureConfig(IModelDoc2 model, JointGroupDefinition group)
        {
            if (group == null) return;
            if (string.IsNullOrEmpty(group.FeatureConfigId))
                group.FeatureConfigId = Guid.NewGuid().ToString("D");

            try
            {
                _memoryCache[group.FeatureConfigId] = group;

                string xml = SerializeToXml(group);
                string filePath = Path.Combine(JointGroupDefinition.GetFeatureStorageDir(), group.FeatureConfigId + ".xml");
                File.WriteAllText(filePath, xml, Encoding.UTF8);

                if (model != null)
                {
                    ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                    if (propMgr != null)
                    {
                        string propName = PROP_PREFIX + group.FeatureConfigId;
                        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
                        propMgr.Add3(propName, (int)swCustomInfoType_e.swCustomInfoText, b64,
                            (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("JointConfigStore.SaveFeatureConfig error: " + ex);
            }
        }

        public static JointGroupDefinition LoadFeatureConfig(IModelDoc2 model, string featureConfigId, string macroFeatureName = null)
        {
            if (!string.IsNullOrEmpty(featureConfigId))
            {
                if (_memoryCache.TryGetValue(featureConfigId, out JointGroupDefinition cached))
                    return cached;

                try
                {
                    string filePath = Path.Combine(JointGroupDefinition.GetFeatureStorageDir(), featureConfigId + ".xml");
                    if (File.Exists(filePath))
                    {
                        JointGroupDefinition fromDisk = DeserializeXml(File.ReadAllText(filePath, Encoding.UTF8));
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
                            string propName = PROP_PREFIX + featureConfigId;
                            if (propMgr.Get4(propName, false, out string valOut, out string resolved) &&
                                !string.IsNullOrEmpty(valOut))
                            {
                                string xml = Encoding.UTF8.GetString(Convert.FromBase64String(valOut));
                                JointGroupDefinition fromProp = DeserializeXml(xml);
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

            if (!string.IsNullOrEmpty(macroFeatureName))
            {
                foreach (JointGroupDefinition g in _memoryCache.Values)
                {
                    if (string.Equals(g.ManagedMacroFeatureName, macroFeatureName, StringComparison.OrdinalIgnoreCase))
                        return g;
                }
            }

            return null;
        }

        public static List<JointGroupDefinition> GetAllConfigsForModel(IModelDoc2 model)
        {
            List<JointGroupDefinition> results = new List<JointGroupDefinition>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JointGroupDefinition g in _memoryCache.Values)
            {
                if (!string.IsNullOrEmpty(g.FeatureConfigId) && seenIds.Add(g.FeatureConfigId))
                    results.Add(g);
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
                            if (string.IsNullOrEmpty(propName) || !propName.StartsWith(PROP_PREFIX))
                                continue;

                            string id = propName.Substring(PROP_PREFIX.Length);
                            if (seenIds.Contains(id)) continue;

                            JointGroupDefinition grp = LoadFeatureConfig(model, id);
                            if (grp != null && seenIds.Add(id))
                                results.Add(grp);
                        }
                    }
                }
                catch { }
            }

            return results;
        }

        public static void DeleteFeatureConfig(IModelDoc2 model, JointGroupDefinition group)
        {
            if (group == null || string.IsNullOrEmpty(group.FeatureConfigId)) return;
            try
            {
                _memoryCache.Remove(group.FeatureConfigId);

                string filePath = Path.Combine(JointGroupDefinition.GetFeatureStorageDir(), group.FeatureConfigId + ".xml");
                if (File.Exists(filePath))
                    File.Delete(filePath);

                if (model != null)
                {
                    ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                    propMgr?.Delete2(PROP_PREFIX + group.FeatureConfigId);
                }
            }
            catch { }
        }
    }
}
