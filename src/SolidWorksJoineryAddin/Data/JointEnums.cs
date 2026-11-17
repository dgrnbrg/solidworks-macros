using System;

namespace SolidWorksJoineryAddin.Data
{
    public enum JointType
    {
        TabAndSlot = 0,
        FingerJoint = 1,
        ThroughDado = 2,
        BlindDado = 3,
        Rabbet = 4,
        DadoTabCombo = 5
    }

    public enum CornerReliefType
    {
        DiagonalDogbone = 0,
        TBone = 1,
        None = 2
    }

    public enum KeyingDirection
    {
        None = 0,
        Positive = 1,
        Negative = 2
    }

    public enum DisambiguationChoice
    {
        DisambiguateAsManyAsPossible = 0,
        LimitToQualifiedJoints = 1,
        ForceDisable = 2
    }
}
