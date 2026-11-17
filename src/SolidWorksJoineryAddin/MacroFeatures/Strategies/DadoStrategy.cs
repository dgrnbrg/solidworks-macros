using System;
using System.Collections.Generic;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures.Strategies
{
    public class DadoStrategy : IJointStrategy
    {
        public JointType Type => JointType.ThroughDado;
        public string DisplayName => "Dado";

        public void ComputeIntervals(JointDefinition joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals)
        {
            tabIntervals = new List<Interval>();
            slotIntervals = new List<Interval>();

            double L = joint.SpanLength;
            // Full continuous channel across the joint span
            // For a blind dado, end margins are applied
            double start = (joint.Type == JointType.BlindDado) ? joint.Thickness : 0.0;
            double end = (joint.Type == JointType.BlindDado) ? (L - joint.Thickness) : L;

            if (start >= end)
            {
                start = 0.0;
                end = L;
            }

            Interval channel = new Interval(start, end, false);
            tabIntervals.Add(channel);
            slotIntervals.Add(channel);
        }
    }
}
