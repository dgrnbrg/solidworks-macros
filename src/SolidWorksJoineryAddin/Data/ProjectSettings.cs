using System;
using Microsoft.Win32;

namespace SolidWorksJoineryAddin.Data
{
    public static class ProjectSettings
    {
        private const string REG_KEY = @"Software\SolidWorksJoineryAddin\Settings";

        public static int DefaultTabCount { get; set; } = 3;
        public static double DefaultTabWidth { get; set; } = 0.0254; // 1.0 inch (meters)
        public static double DefaultToolBitRadius { get; set; } = 0.00635; // 0.25 inch (meters)
        public static double DefaultSignedClearance { get; set; } = 0.000127; // +0.005 inch (meters)
        public static bool DefaultEnableDogbones { get; set; } = true;
        public static bool DefaultEnableKeying { get; set; } = true;
        public static double DefaultKeyDelta { get; set; } = 0.0127; // 0.5 inch (meters)
        public static JointType DefaultJointType { get; set; } = JointType.TabAndSlot;
        public static CornerReliefType DefaultReliefType { get; set; } = CornerReliefType.DiagonalDogbone;

        public static JointGroupDefinition CreateDefaultGroup()
        {
            return new JointGroupDefinition
            {
                DefaultType = DefaultJointType,
                DefaultTabCount = DefaultTabCount,
                DefaultTabWidth = DefaultTabWidth,
                DefaultToolBitRadius = DefaultToolBitRadius,
                DefaultSignedClearance = DefaultSignedClearance,
                MasterEnableDogbones = DefaultEnableDogbones,
                DefaultReliefType = DefaultReliefType,
                MasterEnableKeying = DefaultEnableKeying,
                DefaultKeyDelta = DefaultKeyDelta
            };
        }

        public static void Load()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY))
                {
                    if (key == null) return;
                    DefaultTabCount = Math.Max(1, Convert.ToInt32(key.GetValue("TabCount", DefaultTabCount)));
                    DefaultTabWidth = Convert.ToDouble(key.GetValue("TabWidth", DefaultTabWidth));
                    DefaultToolBitRadius = Convert.ToDouble(key.GetValue("BitRadius", DefaultToolBitRadius));
                    DefaultSignedClearance = Convert.ToDouble(key.GetValue("Clearance", DefaultSignedClearance));
                    DefaultEnableDogbones = Convert.ToInt32(key.GetValue("EnableDogbones", 1)) == 1;
                    DefaultEnableKeying = Convert.ToInt32(key.GetValue("EnableKeying", 1)) == 1;
                    DefaultKeyDelta = Convert.ToDouble(key.GetValue("KeyDelta", DefaultKeyDelta));
                    DefaultJointType = (JointType)Convert.ToInt32(key.GetValue("JointType", (int)DefaultJointType));
                    DefaultReliefType = (CornerReliefType)Convert.ToInt32(key.GetValue("ReliefType", (int)DefaultReliefType));
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(REG_KEY))
                {
                    if (key == null) return;
                    key.SetValue("TabCount", DefaultTabCount);
                    key.SetValue("TabWidth", DefaultTabWidth);
                    key.SetValue("BitRadius", DefaultToolBitRadius);
                    key.SetValue("Clearance", DefaultSignedClearance);
                    key.SetValue("EnableDogbones", DefaultEnableDogbones ? 1 : 0);
                    key.SetValue("EnableKeying", DefaultEnableKeying ? 1 : 0);
                    key.SetValue("KeyDelta", DefaultKeyDelta);
                    key.SetValue("JointType", (int)DefaultJointType);
                    key.SetValue("ReliefType", (int)DefaultReliefType);
                }
            }
            catch { }
        }
    }
}
