using System;
using System.Collections.Generic;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures.Strategies
{
    public class RabbetStrategy : IJointStrategy
    {
        public JointType Type => JointType.Rabbet;
        public string DisplayName => "Rabbet";

        public void ComputeIntervals(JointDefinition joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals)
        {
            tabIntervals = new List<Interval>();
            slotIntervals = new List<Interval>();

            // Continuous step along the panel edge
            Interval step = new Interval(0.0, joint.SpanLength, false);
            tabIntervals.Add(step);
            slotIntervals.Add(step);
        }
    }
}
