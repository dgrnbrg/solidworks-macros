using System;
using SolidWorksJoineryAddin.MacroFeatures;

namespace SolidWorksJoineryAddin.Data
{
    [Serializable]
    public class JointDefinition
    {
        public string JointId { get; set; } = "J1";
        public JointType Type { get; set; } = JointType.TabAndSlot;

        // Entity references (runtime IDs & tags)
        public int SourceBodyId { get; set; }
        public int TargetBodyId { get; set; }
        public string SourceEdgeTag { get; set; }
        public string TargetFaceTag { get; set; }

        // Persistent COM entity references for reliable Edit Feature & Rebuild
        public byte[] SourceFacePersistId { get; set; }
        public byte[] TargetFacePersistId { get; set; }
        public byte[] SourceBodyPersistId { get; set; }
        public byte[] TargetBodyPersistId { get; set; }

        // 3D contact coordinate frame (origin at joint start center, span along edge, thickness across mate, cut along normal into target)
        public Vector3D Origin { get; set; }
        public Vector3D SpanUnit { get; set; } = new Vector3D(1, 0, 0);
        public Vector3D ThicknessUnit { get; set; } = new Vector3D(0, 1, 0);
        public Vector3D CutDirectionUnit { get; set; } = new Vector3D(0, 0, 1);

        // Geometric parameters (meters)
        public double SpanLength { get; set; }
        public double Thickness { get; set; }
        public double TargetBodyThickness { get; set; } = 0.0127; // meters (0.5")
        public int TabCount { get; set; } = 3;
        public double NominalTabWidth { get; set; } = 0.0254; // meters (1.0")
        public double Spacing { get; set; }
        public double DadoDepth { get; set; } = 0.00635; // meters (1/4")

        // Fit & Relief
        public double SignedClearance { get; set; } = 0.000127; // meters (+0.005")
        public bool EnableDogbones { get; set; } = true;
        public CornerReliefType ReliefType { get; set; } = CornerReliefType.DiagonalDogbone;
        public double ToolBitRadius { get; set; } = 0.00635; // meters (0.25")

        // Keying (Poka-Yoke)
        public bool EnableKeying { get; set; } = true;
        public bool IsKeyed { get; set; } = false;
        public int KeyedTabIndex { get; set; } = -1; // 0-based index of tab that has offset
        public KeyingDirection KeyDirection { get; set; } = KeyingDirection.None;
        public double KeyOffset { get; set; } = 0.0127; // meters (0.5")

        // Conflict metadata
        public string ConflictGroupId { get; set; } = "";
        public bool HasConflict { get; set; } = false;
        public string ConflictDescription { get; set; } = "";

        // Managed feature names for clean regeneration & delete cleanup
        public string ManagedRefPlaneName { get; set; }
        public string ManagedSlotSketchName { get; set; }
        public string ManagedSlotCutName { get; set; }
        public string ManagedTabSketchName { get; set; }
        public string ManagedTabBossName { get; set; }
    }
}
