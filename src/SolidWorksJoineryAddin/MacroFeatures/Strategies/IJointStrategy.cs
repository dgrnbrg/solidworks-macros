using System;
using System.Collections.Generic;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures.Strategies
{
    public struct Interval
    {
        public double Start;
        public double End;
        public double Length => Math.Abs(End - Start);
        public bool IsKeyed;

        public Interval(double start, double end, bool isKeyed = false)
        {
            Start = start;
            End = end;
            IsKeyed = isKeyed;
        }
    }

    public interface IJointStrategy
    {
        JointType Type { get; }
        string DisplayName { get; }

        /// <summary>
        /// Computes tab and slot intervals along the joint span (from 0 to span length).
        /// Takes into account nominal width, count, spacing, and directional keying offsets.
        /// </summary>
        void ComputeIntervals(JointDefinition joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals);
    }
}
