using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace SolidWorksMcpServer.Protocol
{
    public static class JsonHelper
    {
        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 128
            };
        }

        public static string Serialize(object obj)
        {
            return CreateSerializer().Serialize(obj);
        }

        public static Dictionary<string, object> DeserializeDict(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            object parsed = CreateSerializer().DeserializeObject(json);
            if (parsed is Dictionary<string, object> dict)
                return new Dictionary<string, object>(dict, StringComparer.OrdinalIgnoreCase);

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public static string GetString(IDictionary<string, object> dict, string key, string defaultValue = null)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return defaultValue;
            return Convert.ToString(val, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(IDictionary<string, object> dict, string key, bool defaultValue = false)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return defaultValue;
            if (val is bool b)
                return b;
            string s = Convert.ToString(val, CultureInfo.InvariantCulture);
            if (bool.TryParse(s, out bool parsed))
                return parsed;
            return defaultValue;
        }

        public static int GetInt(IDictionary<string, object> dict, string key, int defaultValue = 0)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return defaultValue;
            if (val is int i)
                return i;
            if (val is long l)
                return (int)l;
            if (val is double d)
                return (int)Math.Round(d);
            if (int.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            return defaultValue;
        }

        public static double GetDouble(IDictionary<string, object> dict, string key, double defaultValue = 0.0)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return defaultValue;
            if (val is double d)
                return d;
            if (val is float f)
                return f;
            if (val is int i)
                return i;
            if (val is long l)
                return l;
            if (val is decimal dec)
                return (double)dec;
            if (double.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                return parsed;
            return defaultValue;
        }

        public static double? GetNullableDouble(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return null;
            return GetDouble(dict, key, 0.0);
        }

        public static Dictionary<string, object> GetDict(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return null;
            if (val is Dictionary<string, object> d)
                return new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase);
            if (val is IDictionary<string, object> id)
                return new Dictionary<string, object>(id, StringComparer.OrdinalIgnoreCase);
            return null;
        }

        public static List<object> AsObjectList(object val)
        {
            if (val == null)
                return new List<object>();
            if (val is object[] arr)
                return new List<object>(arr);
            if (val is IEnumerable enumerable && !(val is string))
            {
                List<object> list = new List<object>();
                foreach (object item in enumerable)
                    list.Add(item);
                return list;
            }
            return new List<object>();
        }

        public static List<object> GetList(IDictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out object val) || val == null)
                return new List<object>();
            return AsObjectList(val);
        }

        public static List<string> GetStringList(IDictionary<string, object> dict, string key)
        {
            List<string> result = new List<string>();
            foreach (object item in GetList(dict, key))
            {
                if (item != null)
                    result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            return result;
        }

        public static double[] GetDoubleArray(IDictionary<string, object> dict, string key)
        {
            List<object> raw = GetList(dict, key);
            double[] arr = new double[raw.Count];
            for (int i = 0; i < raw.Count; i++)
            {
                arr[i] = raw[i] != null ? Convert.ToDouble(raw[i], CultureInfo.InvariantCulture) : 0.0;
            }
            return arr;
        }

        public static Dictionary<string, object> Schema(
            Dictionary<string, object> properties,
            params string[] required)
        {
            Dictionary<string, object> s = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties ?? new Dictionary<string, object>() }
            };
            if (required != null && required.Length > 0)
            {
                s["required"] = required;
            }
            return s;
        }

        public static Dictionary<string, object> Prop(string type, string description, object defaultValue = null, string[] enumValues = null)
        {
            Dictionary<string, object> p = new Dictionary<string, object>
            {
                { "type", type },
                { "description", description }
            };
            if (defaultValue != null)
                p["default"] = defaultValue;
            if (enumValues != null && enumValues.Length > 0)
                p["enum"] = enumValues;
            return p;
        }

        public static Dictionary<string, object> ArrayProp(string itemType, string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", description },
                { "items", new Dictionary<string, object> { { "type", itemType } } }
            };
        }
    }
}
