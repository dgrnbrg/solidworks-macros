using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    internal static class AssemblyJointAnalyzer
    {
        public class InterferenceHoleCache
        {
            public readonly Dictionary<string, double[][]> CompBoxCache = new Dictionary<string, double[][]>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<double[][]>> CompHoleBoxesCache = new Dictionary<string, List<double[][]>>(StringComparer.OrdinalIgnoreCase);
        }

        private class HoleSegment
        {
            public string ComponentName;
            public string PartPath;
            public string FaceToken;
            public string DocUnits;
            public double DiameterM;
            public double LengthM;
            public double[] MidpointM;
            public double[] Axis;
            public double SMin;
            public double SMax;
            public double MinDiaMergedM;
            public double MaxDiaMergedM;
            public double CboreDiaM;
            public double CboreSMin;
            public double CboreSMax;
            public double CboreDepthM;
            public bool IsSubassemblyChild;
        }

        private class PlanarFaceInfo
        {
            public string ComponentName;
            public string Token;
            public double[] PointM;
            public double[] Normal;
            public double[] BoxMinM;
            public double[] BoxMaxM;
            public double AreaM2;
        }

        private class BossCylInfo
        {
            public string ComponentName;
            public double[] OriginM;
            public double[] Axis;
            public double RadiusM;
            public double[] BoxMinM;
            public double[] BoxMaxM;
        }

        private class ConeHoleInfo
        {
            public string ComponentName;
            public double[] OriginM;
            public double[] Axis;
            public double HalfAngleDeg;
            public double[] BoxMinM;
            public double[] BoxMaxM;
        }

        private class CompBoundsInfo
        {
            public IComponent2 Component;
            public string Name;
            public bool IsSubassemblyChild;
            public double[] BoxMinM;
            public double[] BoxMaxM;
            public List<PlanarFaceInfo> Planes = new List<PlanarFaceInfo>();
            public List<BossCylInfo> Bosses = new List<BossCylInfo>();
            public List<ConeHoleInfo> Cones = new List<ConeHoleInfo>();
            public List<HoleSegment> Holes = new List<HoleSegment>();
        }

        private class FastenerInstanceInfo
        {
            public string Name;
            public string PartNumber;
            public double[] BoxMinM;
            public double[] BoxMaxM;
            public double[] CenterM;
        }

        public static McpToolResult InspectAssemblyJoints(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IAssemblyDoc assy))
                return McpToolResult.Error("sw_inspect_assembly_joints requires an active Assembly (.SLDASM) document.");

            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string nutStyle = NormalizeNutStyle(JsonHelper.GetString(args, "nut_style", "nylock"));
            string material = (JsonHelper.GetString(args, "material", "18-8 Stainless Steel") ?? "18-8 Stainless Steel").Trim();
            if (string.IsNullOrWhiteSpace(material)) material = "18-8 Stainless Steel";
            double maxHoleDiaM = JsonHelper.GetDouble(args, "max_hole_diameter_mm", 16.0) * 0.001;
            bool includeSingle = JsonHelper.GetBool(args, "include_single_component_holes", false);
            bool topLevelOnly = JsonHelper.GetBool(args, "top_level_only", true);
            bool includeSubBoundary = JsonHelper.GetBool(args, "include_subassembly_boundary_joints", true);

            object[] rawComps = assy.GetComponents(topLevelOnly) as object[];
            if (rawComps == null || rawComps.Length == 0)
                return McpToolResult.Error("No components found in active assembly.");

            List<CompBoundsInfo> structComps = new List<CompBoundsInfo>();
            List<FastenerInstanceInfo> fasteners = new List<FastenerInstanceInfo>();
            List<HoleSegment> allHoles = new List<HoleSegment>();
            List<double[][]> topLevelStructBoxes = new List<double[][]>();
            List<IComponent2> subAssemblies = new List<IComponent2>();

            foreach (object cObj in rawComps)
            {
                if (!(cObj is IComponent2 comp) || comp.IsSuppressed()) continue;
                IModelDoc2 cDoc = comp.GetModelDoc2() as IModelDoc2;
                if (cDoc is IAssemblyDoc)
                {
                    subAssemblies.Add(comp);
                    continue;
                }
                if (!(cDoc is IPartDoc partDoc)) continue;

                IMathTransform xf = comp.Transform2;
                double[] xfMat = xf != null ? xf.ArrayData as double[] : null;
                double[] partBox = partDoc.GetPartBox(true) as double[];
                AssemblyGeometryHelper.TransformAabbFast(xfMat, partBox, out double[] cMinM, out double[] cMaxM);

                if (AssemblyGeometryHelper.IsMcMasterOrFastenerComponent(comp))
                {
                    fasteners.Add(MakeFastenerInfo(comp, cMinM, cMaxM));
                    continue;
                }

                topLevelStructBoxes.Add(new[] { cMinM, cMaxM });
                structComps.Add(ScanComponentGeometry(
                    comp, cDoc, partDoc, xfMat, cMinM, cMaxM, maxHoleDiaM, false, allHoles));
            }

            // Scan subassembly boundary leaf components touching top-level structural parts
            if (topLevelOnly && includeSubBoundary && subAssemblies.Count > 0 && topLevelStructBoxes.Count > 0)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<IComponent2> leaves = new List<IComponent2>();
                foreach (IComponent2 sub in subAssemblies)
                    CollectLeafSubcomponents(sub, visited, leaves);

                foreach (IComponent2 leaf in leaves)
                {
                    if (leaf.IsSuppressed()) continue;
                    IModelDoc2 lDoc = leaf.GetModelDoc2() as IModelDoc2;
                    if (!(lDoc is IPartDoc lPart)) continue;

                    double[] lMat = leaf.Transform2 != null ? leaf.Transform2.ArrayData as double[] : null;
                    double[] lBox = lPart.GetPartBox(true) as double[];
                    AssemblyGeometryHelper.TransformAabbFast(lMat, lBox, out double[] lMinM, out double[] lMaxM);
                    if (!AabbOverlapsAny(lMinM, lMaxM, topLevelStructBoxes, 0.015))
                        continue;

                    if (AssemblyGeometryHelper.IsMcMasterOrFastenerComponent(leaf))
                    {
                        fasteners.Add(MakeFastenerInfo(leaf, lMinM, lMaxM));
                        continue;
                    }

                    structComps.Add(ScanComponentGeometry(
                        leaf, lDoc, lPart, lMat, lMinM, lMaxM, maxHoleDiaM, true, allHoles));
                }
            }

            // Cluster coaxial hole segments across components
            List<List<HoleSegment>> axisClusters = ClusterCoaxialSegments(allHoles);

            List<Dictionary<string, object>> jointSites = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> nonJointBores = new List<Dictionary<string, object>>();

            foreach (List<HoleSegment> rawCluster in axisClusters)
            {
                // Merge concentric counterbore/split faces within the same component on this axis
                double[] axisDir = rawCluster[0].Axis;
                double[] anchor = rawCluster[0].MidpointM;
                List<HoleSegment> layers = MergeComponentLayers(rawCluster, anchor, axisDir);

                HashSet<string> distinctComps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool hasTopLevelLayer = false;
                foreach (HoleSegment h in layers)
                {
                    distinctComps.Add(h.ComponentName);
                    if (!h.IsSubassemblyChild) hasTopLevelLayer = true;
                }

                if (distinctComps.Count < 2 && !includeSingle)
                {
                    foreach (HoleSegment h in layers)
                    {
                        nonJointBores.Add(new Dictionary<string, object>
                        {
                            { "component", h.ComponentName },
                            { "diameter", Math.Round(ShopCatalogService.FromMeters(h.MinDiaMergedM, units), 3) },
                            { "length", Math.Round(ShopCatalogService.FromMeters(h.LengthM, units), 3) },
                            { "midpoint", RoundVec(h.MidpointM, units) },
                            { "axis", new[] { Math.Round(axisDir[0], 4), Math.Round(axisDir[1], 4), Math.Round(axisDir[2], 4) } }
                        });
                    }
                    continue;
                }

                if (topLevelOnly && !hasTopLevelLayer)
                    continue;

                // Split any disjoint groups separated by more than 25mm air gap along the axis
                List<List<HoleSegment>> contiguousStacks = SplitByAxialGap(layers, 0.025);
                foreach (List<HoleSegment> stack in contiguousStacks)
                {
                    HashSet<string> stackComps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool stackHasTop = false;
                    foreach (HoleSegment h in stack)
                    {
                        stackComps.Add(h.ComponentName);
                        if (!h.IsSubassemblyChild) stackHasTop = true;
                    }
                    if (stackComps.Count < 2 && !includeSingle) continue;
                    if (topLevelOnly && !stackHasTop) continue;

                    Dictionary<string, object> analyzed = AnalyzeJointStack(
                        stack, axisDir, anchor, structComps, fasteners, units, nutStyle, material);
                    jointSites.Add(analyzed);
                }
            }

            // Group identical stacks into Pattern Groups
            List<Dictionary<string, object>> patternGroups = BuildPatternGroups(jointSites, units, nutStyle, material);

            return McpToolResult.JsonText(new
            {
                document = doc.GetTitle(),
                units = units,
                nut_style = nutStyle,
                material = material,
                structural_components = structComps.Count,
                existing_fastener_components = fasteners.Count,
                joint_site_count = jointSites.Count,
                pattern_group_count = patternGroups.Count,
                pattern_groups = patternGroups,
                non_joint_single_part_bores = nonJointBores.Count > 20 ? nonJointBores.GetRange(0, 20) : nonJointBores
            });
        }

        private static FastenerInstanceInfo MakeFastenerInfo(IComponent2 comp, double[] cMinM, double[] cMaxM)
        {
            return new FastenerInstanceInfo
            {
                Name = comp.Name2,
                PartNumber = AssemblyGeometryHelper.GetMcMasterPartNumber(comp),
                BoxMinM = cMinM,
                BoxMaxM = cMaxM,
                CenterM = new[]
                {
                    0.5 * (cMinM[0] + cMaxM[0]),
                    0.5 * (cMinM[1] + cMaxM[1]),
                    0.5 * (cMinM[2] + cMaxM[2])
                }
            };
        }

        private static void CollectLeafSubcomponents(IComponent2 parent, HashSet<string> visited, List<IComponent2> leaves)
        {
            if (parent == null || parent.IsSuppressed()) return;
            object[] children = null;
            try { children = parent.GetChildren() as object[]; } catch { }
            if (children == null || children.Length == 0) return;

            foreach (object chObj in children)
            {
                if (!(chObj is IComponent2 child) || child.IsSuppressed()) continue;
                string key = child.Name2 ?? "";
                if (!string.IsNullOrEmpty(key) && !visited.Add(key)) continue;

                IModelDoc2 chDoc = child.GetModelDoc2() as IModelDoc2;
                if (chDoc is IAssemblyDoc)
                {
                    CollectLeafSubcomponents(child, visited, leaves);
                }
                else if (chDoc is IPartDoc)
                {
                    leaves.Add(child);
                }
            }
        }

        private static bool AabbOverlapsAny(double[] minM, double[] maxM, List<double[][]> boxes, double padM)
        {
            foreach (double[][] b in boxes)
            {
                double[] bMin = b[0];
                double[] bMax = b[1];
                if (maxM[0] >= bMin[0] - padM && minM[0] <= bMax[0] + padM &&
                    maxM[1] >= bMin[1] - padM && minM[1] <= bMax[1] + padM &&
                    maxM[2] >= bMin[2] - padM && minM[2] <= bMax[2] + padM)
                    return true;
            }
            return false;
        }

        private static CompBoundsInfo ScanComponentGeometry(
            IComponent2 comp,
            IModelDoc2 cDoc,
            IPartDoc partDoc,
            double[] xfMat,
            double[] cMinM,
            double[] cMaxM,
            double maxHoleDiaM,
            bool isSubChild,
            List<HoleSegment> allHoles)
        {
            string compUpper = (comp.Name2 ?? "").ToUpperInvariant();
            if (compUpper.Contains("LEAD SCREW") || compUpper.Contains("BALL SCREW"))
            {
                return new CompBoundsInfo
                {
                    Component = comp,
                    Name = comp.Name2,
                    IsSubassemblyChild = isSubChild,
                    BoxMinM = cMinM,
                    BoxMaxM = cMaxM
                };
            }

            string compUnits = SwConnectionManager.GetDocumentUnits(cDoc);
            CompBoundsInfo cInfo = new CompBoundsInfo
            {
                Component = comp,
                Name = comp.Name2,
                IsSubassemblyChild = isSubChild,
                BoxMinM = cMinM,
                BoxMaxM = cMaxM
            };

            object[] bodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return cInfo;

            for (int bIdx = 0; bIdx < bodies.Length; bIdx++)
            {
                if (!(bodies[bIdx] is IBody2 body)) continue;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    if (!(fObj is IFace2 face)) continue;
                    ISurface surf = face.GetSurface() as ISurface;
                    if (surf == null) continue;

                    bool isPlane = surf.IsPlane();
                    bool isCyl = !isPlane && surf.IsCylinder();
                    bool isCone = !isPlane && !isCyl && surf.IsCone();
                    if (!isPlane && !isCyl && !isCone) continue;

                    if (isPlane)
                    {
                        double[] pp = surf.PlaneParams as double[];
                        if (pp == null || pp.Length < 6) continue;
                        double[] fBox = face.GetBox() as double[];
                        AssemblyGeometryHelper.TransformAabbFast(xfMat, fBox, out double[] fMin, out double[] fMax);
                        double[] nLocal = new[] { pp[0], pp[1], pp[2] };
                        if (face.FaceInSurfaceSense()) { nLocal[0] = -nLocal[0]; nLocal[1] = -nLocal[1]; nLocal[2] = -nLocal[2]; }
                        cInfo.Planes.Add(new PlanarFaceInfo
                        {
                            ComponentName = comp.Name2,
                            Token = "comp:" + comp.Name2 + "::" + SwConnectionManager.ComputeFaceToken(face, bIdx),
                            PointM = AssemblyGeometryHelper.TransformPointFast(xfMat, pp[3], pp[4], pp[5]),
                            Normal = AssemblyGeometryHelper.TransformVectorFast(xfMat, nLocal[0], nLocal[1], nLocal[2]),
                            BoxMinM = fMin,
                            BoxMaxM = fMax,
                            AreaM2 = face.GetArea()
                        });
                    }
                    else if (isCyl)
                    {
                        double[] cp = surf.CylinderParams as double[];
                        if (cp == null || cp.Length < 7) continue;

                        double radiusM = cp[6];
                        bool isHole = face.FaceInSurfaceSense();
                        if (isHole && 2.0 * radiusM > maxHoleDiaM) continue;

                        double[] fBox = face.GetBox() as double[];
                        AssemblyGeometryHelper.TransformAabbFast(xfMat, fBox, out double[] fMin, out double[] fMax);
                        double[] gOrig = AssemblyGeometryHelper.TransformPointFast(xfMat, cp[0], cp[1], cp[2]);
                        double[] gAxis = AssemblyGeometryHelper.TransformVectorFast(xfMat, cp[3], cp[4], cp[5]);

                        if (isHole)
                        {
                            ProjectBoxAlongAxis(fMin, fMax, gAxis, out double sMin, out double sMax);
                            double[] mid = ProjectMidpointToAxis(gOrig, gAxis, 0.5 * (sMin + sMax));
                            HoleSegment seg = new HoleSegment
                            {
                                ComponentName = comp.Name2,
                                PartPath = cDoc.GetPathName() ?? "",
                                FaceToken = "comp:" + comp.Name2 + "::" + SwConnectionManager.ComputeFaceToken(face, bIdx),
                                DocUnits = compUnits,
                                DiameterM = 2.0 * radiusM,
                                MinDiaMergedM = 2.0 * radiusM,
                                MaxDiaMergedM = 2.0 * radiusM,
                                CboreDiaM = 2.0 * radiusM,
                                LengthM = Math.Max(0.0, sMax - sMin),
                                MidpointM = mid,
                                Axis = CanonicalizeAxis(gAxis),
                                IsSubassemblyChild = isSubChild
                            };
                            cInfo.Holes.Add(seg);
                            allHoles.Add(seg);
                        }
                        else
                        {
                            cInfo.Bosses.Add(new BossCylInfo
                            {
                                ComponentName = comp.Name2,
                                OriginM = gOrig,
                                Axis = gAxis,
                                RadiusM = radiusM,
                                BoxMinM = fMin,
                                BoxMaxM = fMax
                            });
                        }
                    }
                    else if (isCone && face.FaceInSurfaceSense())
                    {
                        double[] cnp = surf.ConeParams as double[];
                        if (cnp == null || cnp.Length < 8) continue;
                        double halfAngleDeg = Math.Abs(cnp[7]) * 180.0 / Math.PI;
                        if (halfAngleDeg < 30.0 || halfAngleDeg > 60.0) continue;

                        double[] fBox = face.GetBox() as double[];
                        AssemblyGeometryHelper.TransformAabbFast(xfMat, fBox, out double[] fMin, out double[] fMax);
                        cInfo.Cones.Add(new ConeHoleInfo
                        {
                            ComponentName = comp.Name2,
                            OriginM = AssemblyGeometryHelper.TransformPointFast(xfMat, cnp[0], cnp[1], cnp[2]),
                            Axis = CanonicalizeAxis(AssemblyGeometryHelper.TransformVectorFast(xfMat, cnp[3], cnp[4], cnp[5])),
                            HalfAngleDeg = halfAngleDeg,
                            BoxMinM = fMin,
                            BoxMaxM = fMax
                        });
                    }
                }
            }
            return cInfo;
        }

        private static string NormalizeNutStyle(string raw)
        {
            string s = (raw ?? "nylock").Trim().ToLowerInvariant();
            if (s == "hex" || s == "hex_nut" || s == "single" || s == "single_nut") return "hex_nut";
            if (s == "jam" || s == "jam_nut" || s == "jam_nut_stack" || s == "double_nut") return "jam_nut_stack";
            return "nylock";
        }

        public static bool IsExpectedThreadEngagement(IAssemblyDoc assy, IInterference inf, IMathUtility mu, out string compA, out string compB, out double volMm3, out string reason)
        {
            return IsExpectedThreadEngagement(assy, inf, mu, null, out compA, out compB, out volMm3, out reason);
        }

        public static bool IsExpectedThreadEngagement(IAssemblyDoc assy, IInterference inf, IMathUtility mu, InterferenceHoleCache cache, out string compA, out string compB, out double volMm3, out string reason)
        {
            compA = "Unknown";
            compB = "Unknown";
            volMm3 = Math.Round(inf.Volume * 1e9, 4);
            reason = "solid_body_overlap";

            object[] comps = inf.Components as object[];
            IComponent2 c0 = comps != null && comps.Length > 0 ? comps[0] as IComponent2 : null;
            IComponent2 c1 = comps != null && comps.Length > 1 ? comps[1] as IComponent2 : null;
            if (c0 != null) compA = c0.Name2;
            if (c1 != null) compB = c1.Name2;

            if (inf.IsFastener)
            {
                reason = "solidworks_fastener_flag";
                return true;
            }

            bool f0 = AssemblyGeometryHelper.IsMcMasterOrFastenerComponent(c0);
            bool f1 = AssemblyGeometryHelper.IsMcMasterOrFastenerComponent(c1);

            if (f0 && f1)
            {
                reason = "fastener_to_fastener_thread_engagement";
                return true;
            }

            if (f0 ^ f1)
            {
                IComponent2 fastener = f0 ? c0 : c1;
                IComponent2 structural = f0 ? c1 : c0;

                if (volMm3 <= 0.15)
                {
                    reason = "micro_helical_thread_edge_grazing";
                    return true;
                }

                if (HasCoaxialTapDrillHole(fastener, structural, mu, cache))
                {
                    reason = "bolt_to_tapped_receiver_engagement";
                    return true;
                }

                reason = "fastener_colliding_with_structural_part";
                return false;
            }

            reason = "structural_part_overlap";
            return false;
        }

        private static bool HasCoaxialTapDrillHole(IComponent2 fastener, IComponent2 structural, IMathUtility mu, InterferenceHoleCache cache)
        {
            try
            {
                string fName = fastener?.Name2 ?? "";
                string sName = structural?.Name2 ?? "";

                double[] fMin = null, fMax = null;
                if (cache != null && cache.CompBoxCache.TryGetValue(fName, out double[][] fCached))
                {
                    fMin = fCached[0];
                    fMax = fCached[1];
                }
                else
                {
                    IModelDoc2 fDoc = fastener?.GetModelDoc2() as IModelDoc2;
                    if (!(fDoc is IPartDoc fPart)) return false;
                    double[] fMat = fastener.Transform2 != null ? fastener.Transform2.ArrayData as double[] : null;
                    AssemblyGeometryHelper.TransformAabbFast(fMat, fPart.GetPartBox(true) as double[], out fMin, out fMax);
                    if (cache != null && !string.IsNullOrEmpty(fName))
                        cache.CompBoxCache[fName] = new[] { fMin, fMax };
                }

                List<double[][]> holeBoxes = null;
                if (cache != null && cache.CompHoleBoxesCache.TryGetValue(sName, out List<double[][]> hCached))
                {
                    holeBoxes = hCached;
                }
                else
                {
                    IModelDoc2 sDoc = structural?.GetModelDoc2() as IModelDoc2;
                    if (!(sDoc is IPartDoc sPart)) return false;
                    holeBoxes = new List<double[][]>();
                    object[] bodies = sPart.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    if (bodies != null)
                    {
                        double[] sMat = structural.Transform2 != null ? structural.Transform2.ArrayData as double[] : null;
                        foreach (object bObj in bodies)
                        {
                            if (!(bObj is IBody2 body)) continue;
                            object[] faces = body.GetFaces() as object[];
                            if (faces == null) continue;
                            foreach (object faceObj in faces)
                            {
                                if (!(faceObj is IFace2 face)) continue;
                                ISurface surf = face.GetSurface() as ISurface;
                                if (surf == null || !surf.IsCylinder() || !face.FaceInSurfaceSense()) continue;
                                double[] fBox = face.GetBox() as double[];
                                AssemblyGeometryHelper.TransformAabbFast(sMat, fBox, out double[] hMin, out double[] hMax);
                                holeBoxes.Add(new[] { hMin, hMax });
                            }
                        }
                    }
                    if (cache != null && !string.IsNullOrEmpty(sName))
                        cache.CompHoleBoxesCache[sName] = holeBoxes;
                }

                double pad = 0.002;
                foreach (double[][] hb in holeBoxes)
                {
                    double[] hMin = hb[0];
                    double[] hMax = hb[1];
                    if (hMax[0] >= fMin[0] - pad && hMin[0] <= fMax[0] + pad &&
                        hMax[1] >= fMin[1] - pad && hMin[1] <= fMax[1] + pad &&
                        hMax[2] >= fMin[2] - pad && hMin[2] <= fMax[2] + pad)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static List<List<HoleSegment>> ClusterCoaxialSegments(List<HoleSegment> holes)
        {
            List<List<HoleSegment>> clusters = new List<List<HoleSegment>>();
            bool[] used = new bool[holes.Count];

            for (int i = 0; i < holes.Count; i++)
            {
                if (used[i]) continue;
                HoleSegment seed = holes[i];
                List<HoleSegment> grp = new List<HoleSegment> { seed };
                used[i] = true;

                for (int j = i + 1; j < holes.Count; j++)
                {
                    if (used[j]) continue;
                    HoleSegment cand = holes[j];
                    if (Math.Abs(AssemblyGeometryHelper.Dot(seed.Axis, cand.Axis)) < 0.998)
                        continue;

                    double perpDist = PointToLineDistance(cand.MidpointM, seed.MidpointM, seed.Axis);
                    if (perpDist < 0.0004) // 0.4 mm coaxiality
                    {
                        grp.Add(cand);
                        used[j] = true;
                    }
                }
                clusters.Add(grp);
            }
            return clusters;
        }

        private static List<HoleSegment> MergeComponentLayers(List<HoleSegment> cluster, double[] anchor, double[] axis)
        {
            // Compute axial coordinates s along axis
            foreach (HoleSegment h in cluster)
            {
                double sMid = AssemblyGeometryHelper.Dot(Sub(h.MidpointM, anchor), axis);
                h.SMin = sMid - 0.5 * h.LengthM;
                h.SMax = sMid + 0.5 * h.LengthM;
                h.CboreDiaM = h.DiameterM;
                h.CboreSMin = h.SMin;
                h.CboreSMax = h.SMax;
                h.CboreDepthM = 0.0;
            }

            // Sort by SMin ascending
            cluster.Sort((a, b) => a.SMin.CompareTo(b.SMin));

            // Merge overlapping segments within the same component (e.g., stepped counterbore + through bore)
            List<HoleSegment> merged = new List<HoleSegment>();
            foreach (HoleSegment h in cluster)
            {
                HoleSegment match = null;
                for (int i = merged.Count - 1; i >= 0; i--)
                {
                    HoleSegment m = merged[i];
                    if (string.Equals(m.ComponentName, h.ComponentName, StringComparison.OrdinalIgnoreCase) &&
                        h.SMin <= m.SMax + 0.0005 && m.SMin <= h.SMax + 0.0005)
                    {
                        match = m;
                        break;
                    }
                }

                if (match == null)
                {
                    merged.Add(h);
                }
                else
                {
                    match.SMin = Math.Min(match.SMin, h.SMin);
                    match.SMax = Math.Max(match.SMax, h.SMax);
                    match.LengthM = match.SMax - match.SMin;
                    match.MinDiaMergedM = Math.Min(match.MinDiaMergedM, h.DiameterM);
                    match.MaxDiaMergedM = Math.Max(match.MaxDiaMergedM, h.DiameterM);

                    if (h.DiameterM > match.CboreDiaM + 0.0003)
                    {
                        match.CboreDiaM = h.DiameterM;
                        match.CboreSMin = h.SMin;
                        match.CboreSMax = h.SMax;
                    }
                    else if (Math.Abs(h.DiameterM - match.CboreDiaM) <= 0.0003)
                    {
                        match.CboreSMin = Math.Min(match.CboreSMin, h.SMin);
                        match.CboreSMax = Math.Max(match.CboreSMax, h.SMax);
                    }
                    match.CboreDepthM = (match.MaxDiaMergedM > match.MinDiaMergedM + 0.0012)
                        ? Math.Max(0.0, match.CboreSMax - match.CboreSMin)
                        : 0.0;

                    double sMid = 0.5 * (match.SMin + match.SMax);
                    match.MidpointM = new[] { anchor[0] + sMid * axis[0], anchor[1] + sMid * axis[1], anchor[2] + sMid * axis[2] };
                    if (h.DiameterM < match.DiameterM)
                    {
                        match.DiameterM = h.DiameterM;
                        match.FaceToken = h.FaceToken;
                    }
                }
            }

            merged.Sort((a, b) => a.SMin.CompareTo(b.SMin));
            return merged;
        }

        private static List<List<HoleSegment>> SplitByAxialGap(List<HoleSegment> sortedLayers, double maxGapM)
        {
            List<List<HoleSegment>> result = new List<List<HoleSegment>>();
            if (sortedLayers.Count == 0) return result;

            List<HoleSegment> current = new List<HoleSegment> { sortedLayers[0] };
            double currentMaxS = sortedLayers[0].SMax;

            for (int i = 1; i < sortedLayers.Count; i++)
            {
                HoleSegment next = sortedLayers[i];
                if (next.SMin - currentMaxS > maxGapM)
                {
                    result.Add(current);
                    current = new List<HoleSegment> { next };
                    currentMaxS = next.SMax;
                }
                else
                {
                    current.Add(next);
                    currentMaxS = Math.Max(currentMaxS, next.SMax);
                }
            }
            result.Add(current);
            return result;
        }

        private static bool HasMatchingConeNearEnd(
            HoleSegment layer,
            double[] endPtM,
            double[] axis,
            List<CompBoundsInfo> structComps,
            out ConeHoleInfo matched)
        {
            matched = null;
            if (layer == null) return false;
            double endS = AssemblyGeometryHelper.Dot(endPtM, axis);
            foreach (CompBoundsInfo cb in structComps)
            {
                if (!string.Equals(cb.Name, layer.ComponentName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (ConeHoleInfo c in cb.Cones)
                {
                    if (Math.Abs(AssemblyGeometryHelper.Dot(c.Axis, axis)) < 0.98) continue;
                    if (PointToLineDistance(c.OriginM, endPtM, axis) > 0.001) continue;
                    ProjectBoxAlongAxis(c.BoxMinM, c.BoxMaxM, axis, out double kMin, out double kMax);
                    if (endS >= kMin - 0.0035 && endS <= kMax + 0.0035)
                    {
                        matched = c;
                        return true;
                    }
                }
            }
            return false;
        }

        private static double GetNominalShcsHeadHeightMm(string threadSpec)
        {
            string ts = (threadSpec ?? "M6x1.0").ToUpperInvariant();
            if (ts.StartsWith("M2.5")) return 2.5;
            if (ts.StartsWith("M3")) return 3.0;
            if (ts.StartsWith("M4")) return 4.0;
            if (ts.StartsWith("M5")) return 5.0;
            if (ts.StartsWith("M6")) return 6.0;
            if (ts.StartsWith("M8")) return 8.0;
            if (ts.StartsWith("1/4")) return 6.35;
            return 9.5;
        }

        private static double GetNominalBhcsHeadHeightMm(string threadSpec)
        {
            string ts = (threadSpec ?? "M6x1.0").ToUpperInvariant();
            if (ts.StartsWith("M2.5")) return 1.5;
            if (ts.StartsWith("M3")) return 1.65;
            if (ts.StartsWith("M4")) return 2.2;
            if (ts.StartsWith("M5")) return 2.75;
            if (ts.StartsWith("M6")) return 3.3;
            if (ts.StartsWith("M8")) return 4.4;
            return Math.Round(0.55 * GetNominalShcsHeadHeightMm(threadSpec), 2);
        }

        private static Dictionary<string, object> AnalyzeJointStack(
            List<HoleSegment> stack,
            double[] axis,
            double[] anchor,
            List<CompBoundsInfo> structComps,
            List<FastenerInstanceInfo> fasteners,
            string units,
            string nutStyle,
            string material)
        {
            HoleSegment first = stack[0];
            HoleSegment last = stack[stack.Count - 1];

            // Determine bolt entry direction:
            // If first has smaller tap-drill dia and last has clearance dia -> enter from last (-axis).
            // Else if last has smaller tap-drill dia and first has clearance dia -> enter from first (+axis).
            // Else through-bolted: prefer CSK cone end or counterbored end.
            bool firstIsSmaller = first.MinDiaMergedM < last.MinDiaMergedM - 0.0007;
            bool lastIsSmaller = last.MinDiaMergedM < first.MinDiaMergedM - 0.0007;

            bool isTappedReceiver = firstIsSmaller || lastIsSmaller;
            double[] boltDir = axis; // Points from bolt head toward bolt tip
            HoleSegment entryLayer = first;
            HoleSegment exitLayer = last;

            double[] firstOuterPtM = new[] { anchor[0] + first.SMin * axis[0], anchor[1] + first.SMin * axis[1], anchor[2] + first.SMin * axis[2] };
            double[] lastOuterPtM = new[] { anchor[0] + last.SMax * axis[0], anchor[1] + last.SMax * axis[1], anchor[2] + last.SMax * axis[2] };
            bool firstHasCsk = HasMatchingConeNearEnd(first, firstOuterPtM, axis, structComps, out _);
            bool lastHasCsk = HasMatchingConeNearEnd(last, lastOuterPtM, axis, structComps, out _);

            if (firstIsSmaller && !lastIsSmaller)
            {
                boltDir = new[] { -axis[0], -axis[1], -axis[2] };
                entryLayer = last;
                exitLayer = first;
                anchor = first.MidpointM; // Keep relative positions consistent via points
            }
            else if (!isTappedReceiver)
            {
                if (lastHasCsk && !firstHasCsk)
                {
                    boltDir = new[] { -axis[0], -axis[1], -axis[2] };
                    entryLayer = last;
                    exitLayer = first;
                }
                else if (!firstHasCsk && !lastHasCsk)
                {
                    bool firstCbore = first.MaxDiaMergedM > first.MinDiaMergedM + 0.0012;
                    bool lastCbore = last.MaxDiaMergedM > last.MinDiaMergedM + 0.0012;
                    if (lastCbore && !firstCbore)
                    {
                        boltDir = new[] { -axis[0], -axis[1], -axis[2] };
                        entryLayer = last;
                        exitLayer = first;
                    }
                }
            }

            // Recompute exact global entry & exit points on boltDir
            double sMinBolt = double.MaxValue, sMaxBolt = double.MinValue;
            foreach (HoleSegment h in stack)
            {
                double sc = AssemblyGeometryHelper.Dot(Sub(h.MidpointM, anchor), boltDir);
                sMinBolt = Math.Min(sMinBolt, sc - 0.5 * h.LengthM);
                sMaxBolt = Math.Max(sMaxBolt, sc + 0.5 * h.LengthM);
            }

            double[] entryPtM = new[]
            {
                anchor[0] + sMinBolt * boltDir[0],
                anchor[1] + sMinBolt * boltDir[1],
                anchor[2] + sMinBolt * boltDir[2]
            };
            double[] exitPtM = new[]
            {
                anchor[0] + sMaxBolt * boltDir[0],
                anchor[1] + sMaxBolt * boltDir[1],
                anchor[2] + sMaxBolt * boltDir[2]
            };

            double totalStackSpanMm = (sMaxBolt - sMinBolt) * 1000.0;
            double gripLengthMm = isTappedReceiver
                ? (totalStackSpanMm - exitLayer.LengthM * 1000.0)
                : totalStackSpanMm;

            bool isCountersunk = HasMatchingConeNearEnd(entryLayer, entryPtM, boltDir, structComps, out _);
            bool isCounterbored = !isCountersunk && entryLayer.MaxDiaMergedM > entryLayer.MinDiaMergedM + 0.0012;
            double cboreDiaMm = isCounterbored ? Math.Round(entryLayer.MaxDiaMergedM * 1000.0, 2) : 0.0;
            double cboreDepthMm = isCounterbored ? Math.Round(entryLayer.CboreDepthM * 1000.0, 2) : 0.0;

            double[] headSeatPtM = new[] { entryPtM[0], entryPtM[1], entryPtM[2] };
            if (isCounterbored && cboreDepthMm > 0.1 && cboreDepthMm < gripLengthMm - 0.5)
            {
                headSeatPtM = new[]
                {
                    entryPtM[0] + (cboreDepthMm * 0.001) * boltDir[0],
                    entryPtM[1] + (cboreDepthMm * 0.001) * boltDir[1],
                    entryPtM[2] + (cboreDepthMm * 0.001) * boltDir[2]
                };
                gripLengthMm = Math.Max(1.0, gripLengthMm - cboreDepthMm);
            }

            // Check if already fastened
            List<string> presentFasteners = new List<string>();
            foreach (FastenerInstanceInfo f in fasteners)
            {
                double rad = PointToLineDistance(f.CenterM, entryPtM, boltDir);
                double sF = AssemblyGeometryHelper.Dot(Sub(f.CenterM, entryPtM), boltDir);
                if (rad <= Math.Max(0.004, 0.75 * entryLayer.MinDiaMergedM) &&
                    sF >= -0.020 && sF <= (sMaxBolt - sMinBolt) + 0.025)
                {
                    presentFasteners.Add(f.Name);
                }
            }

            // Infer thread size and standard 18-8 hardware
            string threadSpec = InferThreadSpec(
                isTappedReceiver ? exitLayer.MinDiaMergedM * 1000.0 : 0.0,
                entryLayer.MinDiaMergedM * 1000.0,
                entryLayer.DocUnits == "in");

            // Check radial boss/wall clearance at seat & exit planes, plus axial head & downstream clearances
            double[] headOutDir = new[] { -boltDir[0], -boltDir[1], -boltDir[2] };
            double entryRadClearMm = MeasureRadialBossClearance(headSeatPtM, headOutDir, structComps, out double[] entryObsVec, out string entryObsComp);
            double exitRadClearMm = MeasureRadialBossClearance(exitPtM, boltDir, structComps, out double[] exitObsVec, out string exitObsComp);

            double downstreamClearMm = MeasureAxialDownstreamClearance(exitPtM, boltDir, Math.Max(0.003, 0.5 * entryLayer.MinDiaMergedM), structComps, stack, out string downstreamBlocker);
            double headAxialClearMm = MeasureAxialDownstreamClearance(headSeatPtM, headOutDir, Math.Max(0.004, 0.75 * entryLayer.MinDiaMergedM), structComps, stack, out string headAxialBlocker);

            // Resolve material, standard dimensions, washer & hex clocking recommendations
            bool is18_8 = AssemblyGeometryHelper.IsMaterial18_8(material);
            AssemblyGeometryHelper.GetStandardFastenerDimensions(
                threadSpec,
                out double stdWasherOdMm,
                out double stdWasherThkMm,
                out double stdHexAfMm,
                out double stdHexNutHMm,
                out double stdJamNutHMm,
                out double stdNylockHMm);

            McMasterCatalogItem washerItem = null;
            List<McMasterCatalogItem> washers = AssemblyGeometryHelper.Search18_8Catalog(threadSpec.Split('x')[0].Replace("-20", ""), "flat_washer", 0);
            if (washers.Count > 0) washerItem = washers[0];

            string primaryNutKind = nutStyle == "nylock" ? "nylock_nut" : "hex_nut";
            McMasterCatalogItem nutItem = null;
            McMasterCatalogItem jamNutItem = null;
            if (!isTappedReceiver)
            {
                List<McMasterCatalogItem> nuts = AssemblyGeometryHelper.Search18_8Catalog(threadSpec, primaryNutKind, 0);
                if (nuts.Count > 0) nutItem = nuts[0];
                if (nutStyle == "jam_nut_stack")
                {
                    List<McMasterCatalogItem> jams = AssemblyGeometryHelper.Search18_8Catalog(threadSpec, "jam_nut", 0);
                    if (jams.Count > 0) jamNutItem = jams[0];
                }
            }

            double effWasherOdMm = washerItem != null ? washerItem.WasherOrHeadOdMm : stdWasherOdMm;
            double effWasherThkMm = washerItem != null ? washerItem.ThicknessOrHeightMm : stdWasherThkMm;
            double washerRadiusMm = 0.5 * effWasherOdMm;

            string headWasherOmitReason = null;
            bool includeHeadWasher = entryRadClearMm >= washerRadiusMm - 0.05;
            if (!includeHeadWasher) headWasherOmitReason = "radial_boss_clearance";

            if (isCountersunk)
            {
                includeHeadWasher = false;
                headWasherOmitReason = "countersunk_flush_head";
            }
            else if (isCounterbored && effWasherOdMm > cboreDiaMm - 0.1)
            {
                includeHeadWasher = false;
                headWasherOmitReason = "counterbore_smaller_than_washer_od";
            }

            bool includeNutWasher = !isTappedReceiver && exitRadClearMm >= washerRadiusMm - 0.05;

            // Head style decision (csk vs bhcs vs shcs vs hex_bolt)
            double shcsHeadHMm = GetNominalShcsHeadHeightMm(threadSpec);
            double bhcsHeadHMm = GetNominalBhcsHeadHeightMm(threadSpec);
            string prefKind = isCountersunk ? "csk" : ((threadSpec == "1/4-20") ? "hex_bolt" : "shcs");
            string headStyleReason = isCountersunk ? "countersunk_cone_detected" : "standard_profile";

            double prelimHeadWasherThk = includeHeadWasher ? effWasherThkMm : 0.0;
            if (!isCountersunk && !isCounterbored &&
                headAxialClearMm < shcsHeadHMm + prelimHeadWasherThk + 1.0 &&
                headAxialClearMm >= bhcsHeadHMm + 0.5)
            {
                prefKind = "bhcs";
                headStyleReason = "low_profile_bhcs_for_head_axial_clearance";
                if (includeHeadWasher && headAxialClearMm < bhcsHeadHMm + effWasherThkMm + 0.5)
                {
                    includeHeadWasher = false;
                    headWasherOmitReason = "insufficient_head_axial_clearance_for_washer";
                }
            }

            double headWasherThk = includeHeadWasher ? effWasherThkMm : 0.0;
            double nutWasherThk = includeNutWasher ? effWasherThkMm : 0.0;

            double primaryNutHMm = !isTappedReceiver
                ? (nutItem != null ? nutItem.ThicknessOrHeightMm : (nutStyle == "nylock" ? stdNylockHMm : stdHexNutHMm))
                : 0.0;
            double jamNutHMm = (!isTappedReceiver && nutStyle == "jam_nut_stack")
                ? (jamNutItem != null ? jamNutItem.ThicknessOrHeightMm : stdJamNutHMm)
                : 0.0;
            double nutStackHeightMm = primaryNutHMm + jamNutHMm;

            bool needsHexClock = false;
            double clockAngleDeg = 0.0;
            if (!isTappedReceiver && exitRadClearMm < 99.0)
            {
                double hexFlatRadius = 0.5 * (nutItem != null && nutItem.HexAcrossFlatsMm > 0 ? nutItem.HexAcrossFlatsMm : stdHexAfMm);
                double hexCornerRadius = hexFlatRadius / Math.Cos(Math.PI / 6.0);
                if (exitRadClearMm < hexCornerRadius + 0.15 && exitRadClearMm >= hexFlatRadius - 0.05 && exitObsVec != null)
                {
                    needsHexClock = true;
                    double[] refRad = Math.Abs(boltDir[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 0.0, 1.0 };
                    double dr = AssemblyGeometryHelper.Dot(refRad, boltDir);
                    double[] dstX = AssemblyGeometryHelper.Normalize(new[]
                    {
                        refRad[0] - dr * boltDir[0],
                        refRad[1] - dr * boltDir[1],
                        refRad[2] - dr * boltDir[2]
                    });
                    double[] dstZ = AssemblyGeometryHelper.Normalize(AssemblyGeometryHelper.Cross(dstX, boltDir));
                    clockAngleDeg = Math.Round(Math.Atan2(AssemblyGeometryHelper.Dot(exitObsVec, dstZ), AssemblyGeometryHelper.Dot(exitObsVec, dstX)) * 180.0 / Math.PI, 2);
                }
            }

            // Pick bolt length
            double minBoltLenMm;
            double maxSafeBoltLenMm;

            if (isTappedReceiver)
            {
                double receiverDepthMm = exitLayer.LengthM * 1000.0;
                double desiredEngageMm = Math.Min(receiverDepthMm - 0.5, Math.Max(4.0, 1.5 * exitLayer.MinDiaMergedM * 1000.0));
                minBoltLenMm = gripLengthMm + headWasherThk + desiredEngageMm;
                maxSafeBoltLenMm = gripLengthMm + headWasherThk + Math.Min(receiverDepthMm + (downstreamClearMm < 50 ? downstreamClearMm - 0.5 : 0.0), receiverDepthMm);
            }
            else
            {
                double protrusionMargin = nutStyle == "hex_nut" ? 0.8 : 1.0;
                minBoltLenMm = gripLengthMm + headWasherThk + nutWasherThk + nutStackHeightMm + protrusionMargin;
                maxSafeBoltLenMm = downstreamClearMm < 90.0
                    ? (gripLengthMm + headWasherThk + nutWasherThk + nutStackHeightMm + downstreamClearMm - 1.0)
                    : 0.0;
            }

            McMasterCatalogItem boltItem = is18_8
                ? AssemblyGeometryHelper.FindBestBolt18_8(threadSpec, prefKind, minBoltLenMm, maxSafeBoltLenMm)
                : null;
            double chosenBoltLenMm = boltItem != null
                ? boltItem.NominalLengthMm
                : AssemblyGeometryHelper.ChooseStandardBoltLengthMm(threadSpec, minBoltLenMm, maxSafeBoltLenMm);

            double[] primaryNutPosM = !isTappedReceiver
                ? new[]
                {
                    exitPtM[0] + (nutWasherThk * 0.001) * boltDir[0],
                    exitPtM[1] + (nutWasherThk * 0.001) * boltDir[1],
                    exitPtM[2] + (nutWasherThk * 0.001) * boltDir[2]
                }
                : null;
            double[] jamNutPosM = (!isTappedReceiver && nutStyle == "jam_nut_stack")
                ? new[]
                {
                    exitPtM[0] + ((nutWasherThk + primaryNutHMm) * 0.001) * boltDir[0],
                    exitPtM[1] + ((nutWasherThk + primaryNutHMm) * 0.001) * boltDir[1],
                    exitPtM[2] + ((nutWasherThk + primaryNutHMm) * 0.001) * boltDir[2]
                }
                : null;

            object nutStyleOptions = null;
            if (!isTappedReceiver)
            {
                double lenNylock = AssemblyGeometryHelper.ChooseStandardBoltLengthMm(threadSpec, gripLengthMm + headWasherThk + nutWasherThk + stdNylockHMm + 1.0, 0);
                double lenHex = AssemblyGeometryHelper.ChooseStandardBoltLengthMm(threadSpec, gripLengthMm + headWasherThk + nutWasherThk + stdHexNutHMm + 0.8, 0);
                double lenJam = AssemblyGeometryHelper.ChooseStandardBoltLengthMm(threadSpec, gripLengthMm + headWasherThk + nutWasherThk + stdHexNutHMm + stdJamNutHMm + 1.0, 0);
                nutStyleOptions = new
                {
                    nylock = new { bolt_length_mm = lenNylock, nut_stack_mm = stdNylockHMm },
                    hex_nut = new { bolt_length_mm = lenHex, nut_stack_mm = stdHexNutHMm },
                    jam_nut_stack = new { bolt_length_mm = lenJam, nut_stack_mm = Math.Round(stdHexNutHMm + stdJamNutHMm, 2) }
                };
            }

            string boltKindLabel = prefKind == "csk"
                ? "Flat Head Socket Cap Screw"
                : (prefKind == "bhcs"
                    ? "Button Head Hex Drive Screw"
                    : (prefKind == "hex_bolt" ? "Hex Head Screw" : "Socket Head Cap Screw"));
            string boltLenStr = threadSpec.Contains("/")
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3} in", chosenBoltLenMm / 25.4)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0}mm", chosenBoltLenMm);

            object recBoltObj = new
            {
                pn = is18_8 && boltItem != null ? boltItem.PartNumber : null,
                head_style = prefKind,
                head_style_reason = headStyleReason,
                material = material,
                description = is18_8 && boltItem != null
                    ? boltItem.Description
                    : string.Format("{0} {1} x {2} {3}", material, threadSpec, boltLenStr, boltKindLabel),
                length_mm = chosenBoltLenMm,
                mcmaster_search_query = string.Format("{0} {1} {2} {3}", material, threadSpec, boltLenStr, boltKindLabel)
            };

            string washerSpecShort = threadSpec.Split('x')[0].Replace("-20", "");
            object recWasherObj = new
            {
                pn = is18_8 && washerItem != null ? washerItem.PartNumber : null,
                material = material,
                description = is18_8 && washerItem != null
                    ? washerItem.Description
                    : string.Format("{0} {1} Flat Washer", material, washerSpecShort),
                thickness_mm = washerItem != null ? washerItem.ThicknessOrHeightMm : stdWasherThkMm,
                mcmaster_search_query = string.Format("{0} {1} flat washer", material, washerSpecShort)
            };

            object recNutObj = !isTappedReceiver
                ? new
                {
                    pn = is18_8 && nutItem != null ? nutItem.PartNumber : null,
                    style = nutStyle == "nylock" ? "nylock" : "hex_nut",
                    material = material,
                    description = is18_8 && nutItem != null
                        ? nutItem.Description
                        : string.Format("{0} {1} {2}", material, threadSpec, nutStyle == "nylock" ? "Nylon-Insert Locknut" : "Hex Nut"),
                    height_mm = primaryNutHMm,
                    mcmaster_search_query = string.Format("{0} {1} {2}", material, threadSpec, nutStyle == "nylock" ? "nylock locknut" : "hex nut")
                }
                : null;

            object recJamNutObj = (!isTappedReceiver && nutStyle == "jam_nut_stack")
                ? new
                {
                    pn = is18_8 && jamNutItem != null ? jamNutItem.PartNumber : null,
                    style = "jam_nut",
                    material = material,
                    description = is18_8 && jamNutItem != null
                        ? jamNutItem.Description
                        : string.Format("{0} {1} Thin Hex Jam Nut", material, threadSpec),
                    height_mm = jamNutHMm,
                    mcmaster_search_query = string.Format("{0} {1} thin hex jam nut", material, threadSpec)
                }
                : null;

            List<object> layersOut = new List<object>();
            foreach (HoleSegment h in stack)
            {
                layersOut.Add(new
                {
                    component = h.ComponentName,
                    face_token = h.FaceToken,
                    min_diameter = Math.Round(ShopCatalogService.FromMeters(h.MinDiaMergedM, units), 3),
                    max_diameter = Math.Round(ShopCatalogService.FromMeters(h.MaxDiaMergedM, units), 3),
                    length = Math.Round(ShopCatalogService.FromMeters(h.LengthM, units), 3)
                });
            }

            return new Dictionary<string, object>
            {
                { "joint_type", isTappedReceiver ? "tapped_receiver" : "through_bolted" },
                { "nut_style", isTappedReceiver ? "none_tapped" : nutStyle },
                { "material", material },
                { "thread_spec", threadSpec },
                { "recommended_head_style", prefKind },
                { "is_countersunk", isCountersunk },
                { "is_counterbored", isCounterbored },
                { "cbore_diameter_mm", isCounterbored ? (object)cboreDiaMm : null },
                { "cbore_depth_mm", isCounterbored ? (object)cboreDepthMm : null },
                { "already_fastened", presentFasteners.Count > 0 },
                { "present_fasteners", presentFasteners },
                { "grip_length", Math.Round(FromMm(gripLengthMm, units), 3) },
                { "total_stack_span", Math.Round(FromMm(totalStackSpanMm, units), 3) },
                { "entry_component", entryLayer.ComponentName },
                { "exit_component", exitLayer.ComponentName },
                { "entry_hole_token", entryLayer.FaceToken },
                { "exit_hole_token", exitLayer.FaceToken },
                { "entry_point", RoundVec(entryPtM, units) },
                { "head_seat_point", RoundVec(headSeatPtM, units) },
                { "exit_point", RoundVec(exitPtM, units) },
                { "primary_nut_position", primaryNutPosM != null ? RoundVec(primaryNutPosM, units) : null },
                { "jam_nut_position", jamNutPosM != null ? RoundVec(jamNutPosM, units) : null },
                { "bolt_axis", new[] { Math.Round(boltDir[0], 4), Math.Round(boltDir[1], 4), Math.Round(boltDir[2], 4) } },
                { "entry_radial_clearance", Math.Round(FromMm(entryRadClearMm, units), 2) },
                { "entry_obstruction", entryObsComp },
                { "exit_radial_clearance", Math.Round(FromMm(exitRadClearMm, units), 2) },
                { "exit_obstruction", exitObsComp },
                { "head_axial_clearance", Math.Round(FromMm(headAxialClearMm, units), 2) },
                { "head_axial_blocker", headAxialBlocker },
                { "downstream_axial_clearance", Math.Round(FromMm(downstreamClearMm, units), 2) },
                { "downstream_blocker", downstreamBlocker },
                { "include_head_washer", includeHeadWasher },
                { "head_washer_omitted_reason", headWasherOmitReason },
                { "include_nut_washer", includeNutWasher },
                { "requires_hex_flat_clocking", needsHexClock },
                { "recommended_clock_angle_deg", clockAngleDeg },
                { "nut_style_options", nutStyleOptions },
                { "recommended_bolt", recBoltObj },
                { "recommended_washer", recWasherObj },
                { "recommended_nut", recNutObj },
                { "recommended_jam_nut", recJamNutObj },
                { "layers", layersOut },
                { "_group_key", string.Format("{0}|{1}|{2}|{3}|{4:F1}|{5:F2}|{6}|{7}|{8}",
                    isTappedReceiver ? "tap" : "thru",
                    nutStyle,
                    prefKind,
                    threadSpec,
                    gripLengthMm,
                    chosenBoltLenMm,
                    nutItem != null ? nutItem.PartNumber : material,
                    includeHeadWasher,
                    includeNutWasher) }
            };
        }

        private static List<Dictionary<string, object>> BuildPatternGroups(List<Dictionary<string, object>> sites, string units, string nutStyle, string material)
        {
            Dictionary<string, List<Dictionary<string, object>>> buckets = new Dictionary<string, List<Dictionary<string, object>>>();
            List<string> order = new List<string>();
            foreach (Dictionary<string, object> s in sites)
            {
                string key = JsonHelper.GetString(s, "_group_key", "other");
                s.Remove("_group_key");
                if (!buckets.TryGetValue(key, out List<Dictionary<string, object>> list))
                {
                    list = new List<Dictionary<string, object>>();
                    buckets[key] = list;
                    order.Add(key);
                }
                list.Add(s);
            }

            List<Dictionary<string, object>> groups = new List<Dictionary<string, object>>();
            int gIdx = 1;
            foreach (string key in order)
            {
                List<Dictionary<string, object>> list = buckets[key];
                int fastened = 0;
                List<object> placements = new List<object>();
                foreach (Dictionary<string, object> s in list)
                {
                    bool already = JsonHelper.GetBool(s, "already_fastened", false);
                    if (already) fastened++;
                    placements.Add(new
                    {
                        already_fastened = already,
                        position = s.ContainsKey("head_seat_point") ? s["head_seat_point"] : s["entry_point"],
                        axis = s["bolt_axis"],
                        exit_point = s["exit_point"],
                        primary_nut_position = s["primary_nut_position"],
                        jam_nut_position = s["jam_nut_position"],
                        hole_token = s["entry_hole_token"],
                        clock_angle_deg = s["recommended_clock_angle_deg"]
                    });
                }

                Dictionary<string, object> sample = list[0];
                int unfastened = list.Count - fastened;
                bool isThru = string.Equals(JsonHelper.GetString(sample, "joint_type"), "through_bolted", StringComparison.OrdinalIgnoreCase);
                bool headW = JsonHelper.GetBool(sample, "include_head_washer", false);
                bool nutW = JsonHelper.GetBool(sample, "include_nut_washer", false);
                int washerPerSite = (headW ? 1 : 0) + (nutW ? 1 : 0);
                int primaryNutsPerSite = isThru ? 1 : 0;
                int jamNutsPerSite = (isThru && nutStyle == "jam_nut_stack") ? 1 : 0;

                groups.Add(new Dictionary<string, object>
                {
                    { "group_id", "JointGroup_" + (gIdx++) },
                    { "joint_type", sample["joint_type"] },
                    { "nut_style", sample["nut_style"] },
                    { "material", material },
                    { "thread_spec", sample["thread_spec"] },
                    { "recommended_head_style", sample["recommended_head_style"] },
                    { "is_countersunk", sample["is_countersunk"] },
                    { "is_counterbored", sample["is_counterbored"] },
                    { "head_axial_clearance", sample["head_axial_clearance"] },
                    { "total_sites", list.Count },
                    { "already_fastened_count", fastened },
                    { "unfastened_count", unfastened },
                    { "entry_component", sample["entry_component"] },
                    { "exit_component", sample["exit_component"] },
                    { "grip_length", sample["grip_length"] },
                    { "units", units },
                    { "include_head_washer", headW },
                    { "head_washer_omitted_reason", sample["head_washer_omitted_reason"] },
                    { "include_nut_washer", nutW },
                    { "requires_hex_flat_clocking", sample["requires_hex_flat_clocking"] },
                    { "nut_style_options", sample["nut_style_options"] },
                    { "recommended_bolt", sample["recommended_bolt"] },
                    { "recommended_washer", sample["recommended_washer"] },
                    { "recommended_nut", sample["recommended_nut"] },
                    { "recommended_jam_nut", sample["recommended_jam_nut"] },
                    { "total_needed_quantities", new
                        {
                            bolts = unfastened,
                            washers = unfastened * washerPerSite,
                            primary_nuts = unfastened * primaryNutsPerSite,
                            jam_nuts = unfastened * jamNutsPerSite,
                            nuts = unfastened * (primaryNutsPerSite + jamNutsPerSite)
                        }
                    },
                    { "placements", placements }
                });
            }
            return groups;
        }

        private static string InferThreadSpec(double tapDiaMm, double clearDiaMm, bool imperialPart)
        {
            if (tapDiaMm > 0.1)
            {
                if (tapDiaMm >= 1.95 && tapDiaMm < 2.30) return "M2.5x0.45";
                if (tapDiaMm >= 2.30 && tapDiaMm <= 2.65) return "M3x0.5";
                if (tapDiaMm >= 3.15 && tapDiaMm <= 3.50) return "M4x0.7";
                if (tapDiaMm >= 4.05 && tapDiaMm <= 4.35) return "M5x0.8";
                if (tapDiaMm >= 4.85 && tapDiaMm <= 5.05 && clearDiaMm <= 6.75) return "M6x1.0";
                if (tapDiaMm >= 5.05 && tapDiaMm <= 5.35) return "1/4-20";
                if (tapDiaMm >= 6.60 && tapDiaMm <= 7.00) return "M8x1.25";
            }

            if (clearDiaMm <= 2.85) return "M2.5x0.45";
            if (clearDiaMm <= 3.70) return "M3x0.5";
            if (clearDiaMm <= 4.70) return "M4x0.7";
            if (clearDiaMm <= 5.75) return "M5x0.8";
            if (clearDiaMm <= 6.65 && !imperialPart) return "M6x1.0";
            if (clearDiaMm <= 7.35) return "1/4-20";
            if (clearDiaMm <= 9.20) return "M8x1.25";
            return "3/8-16";
        }

        private static double MeasureRadialBossClearance(
            double[] bearingPtM,
            double[] outwardNormal,
            List<CompBoundsInfo> structComps,
            out double[] nearestVec,
            out string nearestCompName)
        {
            nearestVec = null;
            nearestCompName = null;
            double bestDistM = 0.100; // 100mm default open clearance

            foreach (CompBoundsInfo comp in structComps)
            {
                if (!PointNearAabb(bearingPtM, comp.BoxMinM, comp.BoxMaxM, 0.025)) continue;

                // Check cylindrical outer bosses projecting from or near this bearing plane
                foreach (BossCylInfo b in comp.Bosses)
                {
                    if (Math.Abs(AssemblyGeometryHelper.Dot(b.Axis, outwardNormal)) < 0.95) continue;
                    // Check if boss spans past the bearing plane
                    ProjectBoxAlongAxis(b.BoxMinM, b.BoxMaxM, outwardNormal, out double sMin, out double sMax);
                    double sPlane = AssemblyGeometryHelper.Dot(bearingPtM, outwardNormal);
                    if (sMax < sPlane + 0.0005 || sMin > sPlane + 0.008) continue;

                    double[] diff = Sub(b.OriginM, bearingPtM);
                    double ax = AssemblyGeometryHelper.Dot(diff, outwardNormal);
                    double[] radVec = new[]
                    {
                        diff[0] - ax * outwardNormal[0],
                        diff[1] - ax * outwardNormal[1],
                        diff[2] - ax * outwardNormal[2]
                    };
                    double c2c = Math.Sqrt(AssemblyGeometryHelper.Dot(radVec, radVec));
                    double surfaceGap = c2c - b.RadiusM;
                    if (surfaceGap > 0.0002 && surfaceGap < bestDistM)
                    {
                        bestDistM = surfaceGap;
                        nearestVec = AssemblyGeometryHelper.Normalize(radVec);
                        nearestCompName = comp.Name;
                    }
                }

                // Check nearby perpendicular planar walls protruding above the bearing plane
                foreach (PlanarFaceInfo p in comp.Planes)
                {
                    if (Math.Abs(AssemblyGeometryHelper.Dot(p.Normal, outwardNormal)) > 0.1) continue;
                    double sPlane = AssemblyGeometryHelper.Dot(bearingPtM, outwardNormal);
                    ProjectBoxAlongAxis(p.BoxMinM, p.BoxMaxM, outwardNormal, out double wMin, out double wMax);
                    if (wMax < sPlane + 0.0005 || wMin > sPlane + 0.008) continue;

                    double[] proj = Sub(bearingPtM, p.PointM);
                    double signedD = AssemblyGeometryHelper.Dot(proj, p.Normal);
                    double absD = Math.Abs(signedD);
                    if (absD > 0.0002 && absD < bestDistM)
                    {
                        // Check wall face point falls close to bearingPt in orthogonal directions
                        double[] wallPt = new[]
                        {
                            bearingPtM[0] - signedD * p.Normal[0],
                            bearingPtM[1] - signedD * p.Normal[1],
                            bearingPtM[2] - signedD * p.Normal[2]
                        };
                        if (PointNearAabb(wallPt, p.BoxMinM, p.BoxMaxM, 0.002))
                        {
                            bestDistM = absD;
                            nearestVec = AssemblyGeometryHelper.Normalize(Sub(wallPt, bearingPtM));
                            nearestCompName = comp.Name;
                        }
                    }
                }
            }

            return bestDistM * 1000.0;
        }

        private static double MeasureAxialDownstreamClearance(
            double[] exitPtM,
            double[] boltDir,
            double holeRadiusM,
            List<CompBoundsInfo> structComps,
            List<HoleSegment> currentStack,
            out string blockerName)
        {
            blockerName = null;
            double bestDistM = 0.150;
            HashSet<string> stackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HoleSegment h in currentStack) stackNames.Add(h.ComponentName);

            foreach (CompBoundsInfo comp in structComps)
            {
                if (stackNames.Contains(comp.Name)) continue;
                // Quick AABB intersection test with small cylinder around hole ray
                foreach (PlanarFaceInfo p in comp.Planes)
                {
                    double dn = AssemblyGeometryHelper.Dot(boltDir, p.Normal);
                    if (Math.Abs(dn) < 0.5) continue;
                    double t = AssemblyGeometryHelper.Dot(Sub(p.PointM, exitPtM), p.Normal) / dn;
                    if (t < 0.0002 || t >= bestDistM) continue;

                    double[] hit = new[]
                    {
                        exitPtM[0] + t * boltDir[0],
                        exitPtM[1] + t * boltDir[1],
                        exitPtM[2] + t * boltDir[2]
                    };
                    if (PointNearAabb(hit, p.BoxMinM, p.BoxMaxM, holeRadiusM + 0.001))
                    {
                        bestDistM = t;
                        blockerName = comp.Name;
                    }
                }
            }

            return bestDistM * 1000.0;
        }

        private static void ProjectBoxAlongAxis(double[] bMin, double[] bMax, double[] axis, out double sMin, out double sMax)
        {
            sMin = double.MaxValue;
            sMax = double.MinValue;
            for (int ix = 0; ix <= 1; ix++)
            {
                for (int iy = 0; iy <= 1; iy++)
                {
                    for (int iz = 0; iz <= 1; iz++)
                    {
                        double s = (ix == 0 ? bMin[0] : bMax[0]) * axis[0] +
                                   (iy == 0 ? bMin[1] : bMax[1]) * axis[1] +
                                   (iz == 0 ? bMin[2] : bMax[2]) * axis[2];
                        if (s < sMin) sMin = s;
                        if (s > sMax) sMax = s;
                    }
                }
            }
        }

        private static double[] ProjectMidpointToAxis(double[] origin, double[] axis, double targetS)
        {
            double s0 = AssemblyGeometryHelper.Dot(origin, axis);
            double d = targetS - s0;
            return new[] { origin[0] + d * axis[0], origin[1] + d * axis[1], origin[2] + d * axis[2] };
        }

        private static double[] CanonicalizeAxis(double[] v)
        {
            double[] n = AssemblyGeometryHelper.Normalize(v);
            if (Math.Abs(n[2]) > 0.1 ? n[2] < 0 : (Math.Abs(n[1]) > 0.1 ? n[1] < 0 : n[0] < 0))
                return new[] { -n[0], -n[1], -n[2] };
            return n;
        }

        private static double PointToLineDistance(double[] p, double[] linePt, double[] lineDir)
        {
            double[] d = Sub(p, linePt);
            double proj = AssemblyGeometryHelper.Dot(d, lineDir);
            double[] perp = new[] { d[0] - proj * lineDir[0], d[1] - proj * lineDir[1], d[2] - proj * lineDir[2] };
            return Math.Sqrt(AssemblyGeometryHelper.Dot(perp, perp));
        }

        private static bool PointNearAabb(double[] pt, double[] bMin, double[] bMax, double tolM)
        {
            return pt[0] >= bMin[0] - tolM && pt[0] <= bMax[0] + tolM &&
                   pt[1] >= bMin[1] - tolM && pt[1] <= bMax[1] + tolM &&
                   pt[2] >= bMin[2] - tolM && pt[2] <= bMax[2] + tolM;
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        }

        private static double FromMm(double mm, string units)
        {
            return string.Equals(units, "in", StringComparison.OrdinalIgnoreCase) ? (mm / 25.4) : mm;
        }

        private static double[] RoundVec(double[] m, string units)
        {
            return new[]
            {
                Math.Round(ShopCatalogService.FromMeters(m[0], units), 3),
                Math.Round(ShopCatalogService.FromMeters(m[1], units), 3),
                Math.Round(ShopCatalogService.FromMeters(m[2], units), 3)
            };
        }
    }
}
