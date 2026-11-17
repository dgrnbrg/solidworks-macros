using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    public class ConflictCluster
    {
        public string ClusterId { get; set; }
        public List<JointDefinition> Joints { get; set; } = new List<JointDefinition>();
        public int MinTabCount => Joints.Count > 0 ? Joints.Min(j => j.TabCount) : 0;
        public int Capacity => 2 * MinTabCount;
        public int RequiredTabs => (int)Math.Ceiling(Joints.Count / 2.0);
        public bool ExceedsCapacity => Joints.Count > Capacity;
    }

    public static class KeyingEngine
    {
        private const double GEOMETRIC_TOLERANCE = 1e-4; // 0.1 mm tolerance

        /// <summary>
        /// Analyzes all joints in the group, identifies confusable clusters,
        /// and applies directional poka-yoke tab offsets.
        /// </summary>
        public static void ResolveKeying(JointGroupDefinition group)
        {
            if (group == null || group.Joints == null || group.Joints.Count == 0)
                return;

            // Reset existing keying assignments
            foreach (var j in group.Joints)
            {
                j.IsKeyed = false;
                j.KeyedTabIndex = -1;
                j.KeyDirection = KeyingDirection.None;
                j.HasConflict = false;
                j.ConflictDescription = "";
                j.ConflictGroupId = "";
            }

            if (!group.MasterEnableKeying)
                return;

            // Group into clusters that could be confused by an assembler:
            // Same joint type, identical span length, and identical thickness
            var clusters = new List<ConflictCluster>();

            foreach (var joint in group.Joints)
            {
                if (!joint.EnableKeying)
                    continue;

                ConflictCluster matchingCluster = clusters.FirstOrDefault(c =>
                    c.Joints.Count > 0 &&
                    c.Joints[0].Type == joint.Type &&
                    Math.Abs(c.Joints[0].SpanLength - joint.SpanLength) < GEOMETRIC_TOLERANCE &&
                    Math.Abs(c.Joints[0].Thickness - joint.Thickness) < GEOMETRIC_TOLERANCE);

                if (matchingCluster == null)
                {
                    matchingCluster = new ConflictCluster
                    {
                        ClusterId = "Cluster_" + (clusters.Count + 1)
                    };
                    clusters.Add(matchingCluster);
                }
                matchingCluster.Joints.Add(joint);
            }

            // Process each cluster
            foreach (var cluster in clusters)
            {
                if (cluster.Joints.Count <= 1)
                {
                    // Unique geometry: no risk of cross-assembly confusion with other joints
                    continue;
                }

                int k = cluster.Joints.Count;
                int nMin = cluster.MinTabCount;
                int nReq = cluster.RequiredTabs;

                List<JointDefinition> targetJoints = cluster.Joints;

                if (cluster.ExceedsCapacity)
                {
                    string warning = string.Format(
                        "⚠️ Insufficient tabs to uniquely disambiguate all {0} joints (min tabs={1}). Required tabs: N={2}.",
                        k, nMin, nReq);

                    foreach (var j in cluster.Joints)
                    {
                        j.HasConflict = true;
                        j.ConflictGroupId = cluster.ClusterId;
                        j.ConflictDescription = warning;
                    }

                    if (group.DisambiguationFallback == DisambiguationChoice.ForceDisable)
                    {
                        continue;
                    }
                    else if (group.DisambiguationFallback == DisambiguationChoice.LimitToQualifiedJoints)
                    {
                        // Limit keying to joints that have at least N_req tabs
                        targetJoints = cluster.Joints.Where(j => j.TabCount >= nReq).ToList();
                    }
                }
                else
                {
                    foreach (var j in cluster.Joints)
                    {
                        j.HasConflict = true;
                        j.ConflictGroupId = cluster.ClusterId;
                        j.ConflictDescription = string.Format("Confusable with {0} other joint(s) in {1}", k - 1, cluster.ClusterId);
                    }
                }

                // Apply directional keying:
                // Joint 0: Tab 0, Positive direction
                // Joint 1: Tab 0, Negative direction
                // Joint 2: Tab 1, Positive direction
                // Joint 3: Tab 1, Negative direction ...
                for (int i = 0; i < targetJoints.Count; i++)
                {
                    var j = targetJoints[i];
                    int tabIndex = i / 2;
                    KeyingDirection dir = (i % 2 == 0) ? KeyingDirection.Positive : KeyingDirection.Negative;

                    if (tabIndex < j.TabCount)
                    {
                        j.IsKeyed = true;
                        j.KeyedTabIndex = tabIndex;
                        j.KeyDirection = dir;
                        j.KeyOffset = group.DefaultKeyDelta;
                    }
                }
            }
        }
    }
}
