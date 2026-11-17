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
    public class SavedBedInfo
    {
        public string BodyKey { get; set; }
        public byte[] BedRefPersistId { get; set; }
        public Vector3D BedUpVector { get; set; }
        public Vector3D BedReferencePoint { get; set; }
        public bool FlipBedDirection { get; set; }
    }

    /// <summary>
    /// Stores and retrieves Print Bed identification per-body (and per-part fallback) so the user
    /// only sets the Print Bed once per body and can immediately create multiple Teardrop Fillet
    /// and Crisscross Bridge features without re-picking the bed each time.
    /// </summary>
    public static class BedStateStore
    {
        private static readonly Dictionary<string, SavedBedInfo> _memoryStore =
            new Dictionary<string, SavedBedInfo>(StringComparer.OrdinalIgnoreCase);

        public static byte[] GetPersistReferenceBytes(IModelDoc2 model, object entity)
        {
            if (model == null || entity == null) return null;
            try
            {
                object raw = model.Extension.GetPersistReference3(entity);
                return raw as byte[];
            }
            catch
            {
                return null;
            }
        }

        public static object ResolvePersistReference(IModelDoc2 model, byte[] persistId)
        {
            if (model == null || persistId == null || persistId.Length == 0)
                return null;

            try
            {
                int err = 0;
                object obj = model.Extension.GetObjectByPersistReference3(persistId, out err);
                return obj;
            }
            catch
            {
                return null;
            }
        }

        public static void SaveBedForBodies(
            IModelDoc2 model,
            IList<IBody2> bodies,
            object bedEntity,
            DfmConfig config)
        {
            if (model == null || config == null) return;

            byte[] persistId = config.BedRefPersistId ?? GetPersistReferenceBytes(model, bedEntity);
            if (persistId == null || persistId.Length == 0) return;
            config.BedRefPersistId = persistId;

            List<string> bodyKeys = GetBodyKeys(bodies);
            foreach (string key in bodyKeys)
            {
                SavedBedInfo info = new SavedBedInfo
                {
                    BodyKey = key,
                    BedRefPersistId = persistId,
                    BedUpVector = config.BedUpVector,
                    BedReferencePoint = config.BedReferencePoint,
                    FlipBedDirection = config.FlipBedDirection
                };
                SaveEntry(model, key, info);
            }
        }

        public static SavedBedInfo TryLoadBedForBodies(IModelDoc2 model, IList<IBody2> bodies)
        {
            if (model == null) return null;

            List<string> keys = GetBodyKeys(bodies);
            foreach (string key in keys)
            {
                SavedBedInfo info = LoadEntry(model, key);
                if (info != null && info.BedRefPersistId != null && info.BedRefPersistId.Length > 0)
                    return info;
            }

            return LoadEntry(model, "__default__");
        }

        private static List<string> GetBodyKeys(IList<IBody2> bodies)
        {
            List<string> keys = new List<string>();
            if (bodies != null)
            {
                foreach (IBody2 b in bodies)
                {
                    if (b == null) continue;
                    string name = string.Empty;
                    try { name = b.Name; } catch { }
                    if (!string.IsNullOrEmpty(name))
                    {
                        string sanitized = SanitizeKey(name);
                        if (!keys.Contains(sanitized))
                            keys.Add(sanitized);
                    }
                }
            }

            if (!keys.Contains("__default__"))
                keys.Add("__default__");

            return keys;
        }

        private static string GetModelKey(IModelDoc2 model)
        {
            string path = string.Empty;
            try { path = model.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(path))
            {
                try { path = model.GetTitle(); } catch { }
            }
            return string.IsNullOrEmpty(path) ? "UnsavedPart" : path;
        }

        private static string SanitizeKey(string raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        private static void SaveEntry(IModelDoc2 model, string bodyKey, SavedBedInfo info)
        {
            try
            {
                string cacheKey = GetModelKey(model) + "::" + bodyKey;
                _memoryStore[cacheKey] = info;

                XmlSerializer serializer = new XmlSerializer(typeof(SavedBedInfo));
                string xml;
                using (StringWriter sw = new StringWriter())
                {
                    serializer.Serialize(sw, info);
                    xml = sw.ToString();
                }

                ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                if (propMgr != null)
                {
                    string propName = "DFM_Bed_" + bodyKey;
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
                    propMgr.Add3(propName, (int)swCustomInfoType_e.swCustomInfoText, b64,
                        (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                }
            }
            catch { }
        }

        private static SavedBedInfo LoadEntry(IModelDoc2 model, string bodyKey)
        {
            try
            {
                string cacheKey = GetModelKey(model) + "::" + bodyKey;
                if (_memoryStore.TryGetValue(cacheKey, out SavedBedInfo cached))
                    return cached;

                ICustomPropertyManager propMgr = model.Extension.CustomPropertyManager[""];
                if (propMgr != null)
                {
                    string propName = "DFM_Bed_" + bodyKey;
                    if (propMgr.Get4(propName, false, out string valOut, out string resolved) &&
                        !string.IsNullOrEmpty(valOut))
                    {
                        string xml = Encoding.UTF8.GetString(Convert.FromBase64String(valOut));
                        XmlSerializer serializer = new XmlSerializer(typeof(SavedBedInfo));
                        using (StringReader sr = new StringReader(xml))
                        {
                            SavedBedInfo info = (SavedBedInfo)serializer.Deserialize(sr);
                            if (info != null)
                            {
                                _memoryStore[cacheKey] = info;
                                return info;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
