using System;
using System.Collections.Generic;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures.Strategies
{
    public class TabAndSlotStrategy : IJointStrategy
    {
        public JointType Type => JointType.TabAndSlot;
        public string DisplayName => "Tab & Slot";

        public void ComputeIntervals(JointDefinition joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals)
        {
            tabIntervals = new List<Interval>();
            slotIntervals = new List<Interval>();

            double L = joint.SpanLength;
            int n = Math.Max(1, joint.TabCount);
            double baseW = joint.NominalTabWidth;

            // Ensure tabs fit inside span
            if (n * baseW >= L * 0.95)
            {
                baseW = (L * 0.90) / n;
            }

            // Distribute tabs evenly along span
            double totalTabWidth = n * baseW;
            double remaining = L - totalTabWidth;
            double pitch = remaining / (n + 1);

            double currentPos = pitch;
            for (int i = 0; i < n; i++)
            {
                double start = currentPos;
                double end = currentPos + baseW;
                bool isKeyed = false;

                // Apply directional keying if enabled and assigned
                if (joint.EnableKeying && joint.IsKeyed && joint.KeyedTabIndex == i)
                {
                    isKeyed = true;
                    if (joint.KeyDirection == KeyingDirection.Positive)
                    {
                        // Extend towards the positive end
                        end += joint.KeyOffset;
                    }
                    else if (joint.KeyDirection == KeyingDirection.Negative)
                    {
                        // Extend towards the start
                        start -= joint.KeyOffset;
                    }
                }

                // Clamp to joint span
                start = Math.Max(0.0, start);
                end = Math.Min(L, end);

                Interval tab = new Interval(start, end, isKeyed);
                tabIntervals.Add(tab);

                // Slot corresponds directly to the tab location (clearances applied during profile geometry generation)
                slotIntervals.Add(new Interval(start, end, isKeyed));

                currentPos += baseW + pitch;
            }
        }
    }
}
