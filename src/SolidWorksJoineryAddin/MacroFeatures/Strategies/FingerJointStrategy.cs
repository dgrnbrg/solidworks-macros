using System;
using System.Collections.Generic;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures.Strategies
{
    public class FingerJointStrategy : IJointStrategy
    {
        public JointType Type => JointType.FingerJoint;
        public string DisplayName => "Finger / Box Joint";

        public void ComputeIntervals(JointDefinition joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals)
        {
            tabIntervals = new List<Interval>();
            slotIntervals = new List<Interval>();

            double L = joint.SpanLength;
            int n = Math.Max(2, joint.TabCount);
            
            // Total segments: n fingers on Body A, with interleaving fingers on Body B
            int totalSegments = 2 * n - 1;
            double baseWidth = L / totalSegments;

            for (int i = 0; i < totalSegments; i++)
            {
                double start = i * baseWidth;
                double end = (i + 1) * baseWidth;
                bool isBodyAFinger = (i % 2 == 0);
                int fingerIdx = i / 2;
                bool isKeyed = false;

                // Directional keying applied to finger index
                if (joint.EnableKeying && joint.IsKeyed && isBodyAFinger && joint.KeyedTabIndex == fingerIdx)
                {
                    isKeyed = true;
                    if (joint.KeyDirection == KeyingDirection.Positive)
                    {
                        end += joint.KeyOffset;
                    }
                    else if (joint.KeyDirection == KeyingDirection.Negative)
                    {
                        start -= joint.KeyOffset;
                    }
                    start = Math.Max(0.0, start);
                    end = Math.Min(L, end);
                }

                Interval segment = new Interval(start, end, isKeyed);
                if (isBodyAFinger)
                {
                    tabIntervals.Add(segment);
                }
                else
                {
                    slotIntervals.Add(segment);
                }
            }
        }
    }
}
