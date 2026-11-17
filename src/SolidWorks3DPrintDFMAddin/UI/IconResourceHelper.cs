using System;
using System.IO;
using System.Reflection;

namespace SolidWorks3DPrintDFMAddin.UI
{
    public static class IconResourceHelper
    {
        private static readonly int[] StripSizes = new[] { 20, 32, 40, 64, 96, 128 };
        private static string _iconsDir;

        public static string EnsureIconsExtracted()
        {
            if (!string.IsNullOrEmpty(_iconsDir) && Directory.Exists(_iconsDir))
                return _iconsDir;

            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SolidWorks3DPrintDFMAddin",
                "Icons");

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Assembly asm = Assembly.GetExecutingAssembly();
            DateTime asmTime = File.GetLastWriteTimeUtc(asm.Location);
            string[] resNames = asm.GetManifestResourceNames();

            foreach (string resName in resNames)
            {
                if (!resName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Resource name format: SolidWorks3DPrintDFMAddin.Resources.Icons.<filename>.png
                int iconsIdx = resName.IndexOf(".Resources.Icons.", StringComparison.OrdinalIgnoreCase);
                string fileName = iconsIdx >= 0
                    ? resName.Substring(iconsIdx + ".Resources.Icons.".Length)
                    : resName;

                string outPath = Path.Combine(dir, fileName);
                bool needWrite = !File.Exists(outPath) || File.GetLastWriteTimeUtc(outPath) < asmTime;
                if (needWrite)
                {
                    using (Stream s = asm.GetManifestResourceStream(resName))
                    {
                        if (s != null)
                        {
                            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                            {
                                s.CopyTo(fs);
                            }
                        }
                    }
                }
            }

            _iconsDir = dir;
            return dir;
        }

        public static string[] GetCommandIconList()
        {
            string dir = EnsureIconsExtracted();
            string[] list = new string[StripSizes.Length];
            for (int i = 0; i < StripSizes.Length; i++)
            {
                list[i] = Path.Combine(dir, string.Format("dfm_cmd_strip_{0}.png", StripSizes[i]));
            }
            return list;
        }

        public static string[] GetMainIconList()
        {
            string dir = EnsureIconsExtracted();
            string[] list = new string[StripSizes.Length];
            for (int i = 0; i < StripSizes.Length; i++)
            {
                list[i] = Path.Combine(dir, string.Format("dfm_main_{0}.png", StripSizes[i]));
            }
            return list;
        }

        public static string[] GetTeardropTreeIconFiles()
        {
            string dir = EnsureIconsExtracted();
            return new[]
            {
                Path.Combine(dir, "teardrop_tree_regular.png"),
                Path.Combine(dir, "teardrop_tree_suppressed.png"),
                Path.Combine(dir, "teardrop_tree_highlight.png")
            };
        }

        public static string[] GetBridgeTreeIconFiles()
        {
            string dir = EnsureIconsExtracted();
            return new[]
            {
                Path.Combine(dir, "bridge_tree_regular.png"),
                Path.Combine(dir, "bridge_tree_suppressed.png"),
                Path.Combine(dir, "bridge_tree_highlight.png")
            };
        }
    }
}
