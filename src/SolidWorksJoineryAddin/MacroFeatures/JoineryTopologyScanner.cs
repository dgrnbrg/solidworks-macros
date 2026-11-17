using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    public class DetectedJointContact
    {
        public IFace2 SourceFace { get; set; }
        public IFace2 TargetFace { get; set; }
        public IBody2 SourceBody { get; set; }
        public IBody2 TargetBody { get; set; }
        public JointDefinition Joint { get; set; }
    }

    public static class JoineryTopologyScanner
    {
        private const double NORMAL_ANTI_PARALLEL_DOT = -0.995;
        private const double COPLANAR_TOL_METERS = 0.0005; // 0.5 mm
        private const double MIN_OVERLAP_METERS = 0.001;   // 1.0 mm

        public static byte[] GetPersistReferenceBytes(IModelDoc2 model, object entity)
        {
            if (model == null || entity == null) return null;
            try
            {
                object raw = model.Extension.GetPersistReference3(entity);
                return raw as byte[];
            }
            catch
            {
                return null;
            }
        }

        public static object ResolvePersistReference(IModelDoc2 model, byte[] persistId)
        {
            if (model == null || persistId == null || persistId.Length == 0)
                return null;

            try
            {
                int err = 0;
                return model.Extension.GetObjectByPersistReference3(persistId, out err);
            }
            catch
            {
                return null;
            }
        }

        public static List<IBody2> GetTargetSolidBodies(IModelDoc2 model, IList<IBody2> explicitBodies = null)
        {
            List<IBody2> bodies = new List<IBody2>();
            if (explicitBodies != null && explicitBodies.Count > 0)
            {
                foreach (IBody2 b in explicitBodies)
                {
                    if (b != null && !bodies.Contains(b))
                        bodies.Add(b);
                }
                return bodies;
            }

            if (model is IPartDoc partDoc)
            {
                object rawBodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true);
                if (rawBodies is object[] arr)
                {
                    foreach (object obj in arr)
                    {
                        if (obj is IBody2 b && !bodies.Contains(b))
                            bodies.Add(b);
                    }
                }
            }
            return bodies;
        }

        private class PlanarFaceInfo
        {
            public IFace2 Face;
            public IBody2 Body;
            public int BodyIndex;
            public Vector3D PointOnPlane;
            public Vector3D OutwardNormal;
            public double Area;
        }

        private static List<PlanarFaceInfo> ExtractPlanarFaces(IList<IBody2> bodies)
        {
            List<PlanarFaceInfo> list = new List<PlanarFaceInfo>();
            if (bodies == null) return list;

            for (int bIdx = 0; bIdx < bodies.Count; bIdx++)
            {
                IBody2 body = bodies[bIdx];
                if (body == null) continue;

                object[] faces = (object[])body.GetFaces();
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    IFace2 face = fObj as IFace2;
                    if (face == null) continue;

                    if (TryGetPlanarFaceInfo(face, body, bIdx, out PlanarFaceInfo info))
                    {
                        list.Add(info);
                    }
                }
            }
            return list;
        }

        private static bool TryGetPlanarFaceInfo(IFace2 face, IBody2 body, int bodyIdx, out PlanarFaceInfo info)
        {
            info = null;
            try
            {
                ISurface surf = (ISurface)face.GetSurface();
                if (surf == null || !surf.IsPlane())
                    return false;

                double[] pParams = (double[])surf.PlaneParams;
                if (pParams == null || pParams.Length < 6)
                    return false;

                Vector3D surfNormal = new Vector3D(pParams[0], pParams[1], pParams[2]).Normalized();
                Vector3D outwardNormal = face.FaceInSurfaceSense() ? -surfNormal : surfNormal;
                Vector3D pt = new Vector3D(pParams[3], pParams[4], pParams[5]);

                double area = 0.0;
                try { area = face.GetArea(); } catch { }

                info = new PlanarFaceInfo
                {
                    Face = face,
                    Body = body,
                    BodyIndex = bodyIdx,
                    PointOnPlane = pt,
                    OutwardNormal = outwardNormal.Normalized(),
                    Area = area
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static List<DetectedJointContact> ScanContactingBodyPairs(
            IModelDoc2 model,
            IList<IBody2> bodies,
            JointGroupDefinition group)
        {
            List<DetectedJointContact> detected = new List<DetectedJointContact>();
            if (model == null || bodies == null || bodies.Count < 2 || group == null)
                return detected;

            List<PlanarFaceInfo> allFaces = ExtractPlanarFaces(bodies);
            HashSet<string> seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int jointCounter = 1;
            for (int i = 0; i < allFaces.Count; i++)
            {
                PlanarFaceInfo f1 = allFaces[i];
                for (int j = i + 1; j < allFaces.Count; j++)
                {
                    PlanarFaceInfo f2 = allFaces[j];
                    if (f1.BodyIndex == f2.BodyIndex || f1.Body == f2.Body)
                        continue;

                    if (TryBuildContactJoint(model, f1, f2, group, jointCounter, out DetectedJointContact contact))
                    {
                        string pairKey = MakePairKey(contact.SourceBody, contact.TargetBody, contact.Joint.Origin);
                        if (seenPairs.Add(pairKey))
                        {
                            jointCounter++;
                            detected.Add(contact);
                        }
                    }
                }
            }

            return detected;
        }

        public static List<DetectedJointContact> BuildJointsFromSelectedEntities(
            IModelDoc2 model,
            IList<IFace2> selectedFaces,
            IList<IEdge> selectedEdges,
            IList<IBody2> targetBodies,
            JointGroupDefinition group)
        {
            List<DetectedJointContact> detected = new List<DetectedJointContact>();
            if (model == null || group == null)
                return detected;

            List<IBody2> allBodies = GetTargetSolidBodies(model, targetBodies);
            if (allBodies.Count < 2)
                allBodies = GetTargetSolidBodies(model, null);

            List<PlanarFaceInfo> allPlanarFaces = ExtractPlanarFaces(allBodies);
            HashSet<string> seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int jointCounter = 1;

            List<IFace2> candidateFaces = new List<IFace2>();
            if (selectedFaces != null)
            {
                foreach (IFace2 f in selectedFaces)
                {
                    if (f != null && !candidateFaces.Contains(f))
                        candidateFaces.Add(f);
                }
            }

            if (selectedEdges != null)
            {
                foreach (IEdge edge in selectedEdges)
                {
                    if (edge == null) continue;
                    try
                    {
                        object[] adjFaces = (object[])edge.GetTwoAdjacentFaces2();
                        if (adjFaces != null)
                        {
                            foreach (object fObj in adjFaces)
                            {
                                IFace2 adj = fObj as IFace2;
                                if (adj != null && !candidateFaces.Contains(adj))
                                    candidateFaces.Add(adj);
                            }
                        }
                    }
                    catch { }
                }
            }

            // First try pairing selected faces with each other if multiple faces from different bodies are picked
            for (int i = 0; i < candidateFaces.Count; i++)
            {
                IFace2 selFace = candidateFaces[i];
                IBody2 selBody = selFace.GetBody() as IBody2;
                if (selBody == null) continue;
                int selBodyIdx = allBodies.IndexOf(selBody);

                if (!TryGetPlanarFaceInfo(selFace, selBody, selBodyIdx, out PlanarFaceInfo fInfo))
                    continue;

                // Match against another candidate face first, or any mating face on another body
                bool matchedWithCandidate = false;
                for (int j = i + 1; j < candidateFaces.Count; j++)
                {
                    IFace2 otherFace = candidateFaces[j];
                    IBody2 otherBody = otherFace.GetBody() as IBody2;
                    if (otherBody == null || otherBody == selBody) continue;
                    int otherBodyIdx = allBodies.IndexOf(otherBody);

                    if (!TryGetPlanarFaceInfo(otherFace, otherBody, otherBodyIdx, out PlanarFaceInfo otherInfo))
                        continue;

                    if (TryBuildContactJoint(model, fInfo, otherInfo, group, jointCounter, out DetectedJointContact contact))
                    {
                        string pairKey = MakePairKey(contact.SourceBody, contact.TargetBody, contact.Joint.Origin);
                        if (seenPairs.Add(pairKey))
                        {
                            jointCounter++;
                            detected.Add(contact);
                        }
                        matchedWithCandidate = true;
                    }
                }

                if (!matchedWithCandidate)
                {
                    foreach (PlanarFaceInfo candidateMate in allPlanarFaces)
                    {
                        if (candidateMate.Body == selBody) continue;

                        if (TryBuildContactJoint(model, fInfo, candidateMate, group, jointCounter, out DetectedJointContact contact))
                        {
                            string pairKey = MakePairKey(contact.SourceBody, contact.TargetBody, contact.Joint.Origin);
                            if (seenPairs.Add(pairKey))
                            {
                                jointCounter++;
                                detected.Add(contact);
                            }
                        }
                    }
                }
            }

            return detected;
        }

        private static string MakePairKey(IBody2 src, IBody2 tgt, Vector3D origin)
        {
            string sName = "";
            string tName = "";
            try { sName = src.Name; } catch { }
            try { tName = tgt.Name; } catch { }

            string first = string.Compare(sName, tName, StringComparison.OrdinalIgnoreCase) <= 0 ? sName : tName;
            string second = string.Compare(sName, tName, StringComparison.OrdinalIgnoreCase) <= 0 ? tName : sName;
            return string.Format("{0}|{1}|{2:F3},{3:F3},{4:F3}", first, second, origin.X, origin.Y, origin.Z);
        }

        private static bool TryBuildContactJoint(
            IModelDoc2 model,
            PlanarFaceInfo f1,
            PlanarFaceInfo f2,
            JointGroupDefinition group,
            int jointIndex,
            out DetectedJointContact contact)
        {
            contact = null;

            // Normals must be anti-parallel (faces touching face-to-face)
            if (f1.OutwardNormal.Dot(f2.OutwardNormal) > NORMAL_ANTI_PARALLEL_DOT)
                return false;

            // Planes must be coplanar within tolerance
            double planeDist = Math.Abs((f2.PointOnPlane - f1.PointOnPlane).Dot(f1.OutwardNormal));
            if (planeDist > COPLANAR_TOL_METERS)
                return false;

            // Choose source (tab/inserting) vs target (slot/receiving): smaller face area is normally the plate end face
            PlanarFaceInfo srcInfo = f1.Area <= f2.Area ? f1 : f2;
            PlanarFaceInfo tgtInfo = f1.Area <= f2.Area ? f2 : f1;

            Vector3D cutDir = srcInfo.OutwardNormal.Normalized(); // Points from Source INTO Target
            Vector3D spanUnit = DeterminePrincipalSpanAxis(srcInfo.Face, cutDir);
            Vector3D thickUnit = cutDir.Cross(spanUnit).Normalized();

            GetFaceBoundsInBasis(srcInfo.Face, srcInfo.PointOnPlane, spanUnit, thickUnit,
                out double sMinA, out double sMaxA, out double tMinA, out double tMaxA);
            GetFaceBoundsInBasis(tgtInfo.Face, srcInfo.PointOnPlane, spanUnit, thickUnit,
                out double sMinB, out double sMaxB, out double tMinB, out double tMaxB);

            double sOvMin = Math.Max(sMinA, sMinB);
            double sOvMax = Math.Min(sMaxA, sMaxB);
            double tOvMin = Math.Max(tMinA, tMinB);
            double tOvMax = Math.Min(tMaxA, tMaxB);

            double sLen = sOvMax - sOvMin;
            double tLen = tOvMax - tOvMin;

            if (sLen < MIN_OVERLAP_METERS || tLen < MIN_OVERLAP_METERS)
                return false;

            // Ensure SpanLength is the longer axis and Thickness is the shorter axis
            if (tLen > sLen)
            {
                Vector3D tmpAxis = spanUnit;
                spanUnit = thickUnit;
                thickUnit = cutDir.Cross(spanUnit).Normalized();

                GetFaceBoundsInBasis(srcInfo.Face, srcInfo.PointOnPlane, spanUnit, thickUnit,
                    out sMinA, out sMaxA, out tMinA, out tMaxA);
                GetFaceBoundsInBasis(tgtInfo.Face, srcInfo.PointOnPlane, spanUnit, thickUnit,
                    out sMinB, out sMaxB, out tMinB, out tMaxB);

                sOvMin = Math.Max(sMinA, sMinB);
                sOvMax = Math.Min(sMaxA, sMaxB);
                tOvMin = Math.Max(tMinA, tMinB);
                tOvMax = Math.Min(tMaxA, tMaxB);

                sLen = sOvMax - sOvMin;
                tLen = tOvMax - tOvMin;
            }

            double tMid = 0.5 * (tOvMin + tOvMax);
            Vector3D origin = srcInfo.PointOnPlane + spanUnit * sOvMin + thickUnit * tMid;

            double targetThickness = ComputeBodyThicknessAlongNormal(tgtInfo.Body, srcInfo.PointOnPlane, cutDir);

            string srcBodyName = "";
            string tgtBodyName = "";
            try { srcBodyName = srcInfo.Body.Name; } catch { }
            try { tgtBodyName = tgtInfo.Body.Name; } catch { }

            JointDefinition joint = new JointDefinition
            {
                JointId = "J" + jointIndex,
                Type = group.DefaultType,
                SourceBodyId = srcInfo.BodyIndex,
                TargetBodyId = tgtInfo.BodyIndex,
                SourceEdgeTag = srcBodyName,
                TargetFaceTag = tgtBodyName,
                SourceFacePersistId = GetPersistReferenceBytes(model, srcInfo.Face),
                TargetFacePersistId = GetPersistReferenceBytes(model, tgtInfo.Face),
                SourceBodyPersistId = GetPersistReferenceBytes(model, srcInfo.Body),
                TargetBodyPersistId = GetPersistReferenceBytes(model, tgtInfo.Body),
                Origin = origin,
                SpanUnit = spanUnit,
                ThicknessUnit = thickUnit,
                CutDirectionUnit = cutDir,
                SpanLength = sLen,
                Thickness = tLen,
                TargetBodyThickness = targetThickness,
                TabCount = Math.Max(1, group.DefaultTabCount),
                NominalTabWidth = group.DefaultTabWidth,
                DadoDepth = Math.Min(0.5 * targetThickness, 0.00635),
                SignedClearance = group.DefaultSignedClearance,
                EnableDogbones = group.MasterEnableDogbones,
                ReliefType = group.DefaultReliefType,
                ToolBitRadius = group.DefaultToolBitRadius,
                EnableKeying = group.MasterEnableKeying,
                KeyOffset = group.DefaultKeyDelta
            };

            contact = new DetectedJointContact
            {
                SourceFace = srcInfo.Face,
                TargetFace = tgtInfo.Face,
                SourceBody = srcInfo.Body,
                TargetBody = tgtInfo.Body,
                Joint = joint
            };

            return true;
        }

        private static Vector3D DeterminePrincipalSpanAxis(IFace2 face, Vector3D planeNormal)
        {
            Vector3D bestDir = new Vector3D(0, 0, 0);
            double bestLen = 0.0;

            try
            {
                object[] edges = (object[])face.GetEdges();
                if (edges != null)
                {
                    foreach (object eObj in edges)
                    {
                        IEdge edge = eObj as IEdge;
                        if (edge == null) continue;

                        ICurve curve = (ICurve)edge.GetCurve();
                        if (curve == null || !curve.IsLine()) continue;

                        double[] ep = (double[])edge.GetCurveParams2();
                        if (ep == null || ep.Length < 6) continue;

                        Vector3D delta = new Vector3D(ep[3] - ep[0], ep[4] - ep[1], ep[5] - ep[2]);
                        // Project out any component along normal
                        delta = delta - planeNormal * delta.Dot(planeNormal);
                        double len = delta.Length;
                        if (len > bestLen)
                        {
                            bestLen = len;
                            bestDir = delta.Normalized();
                        }
                    }
                }
            }
            catch { }

            if (bestLen > 1e-6)
                return bestDir;

            Vector3D.BuildOrthonormalBasis(planeNormal, out Vector3D u1, out Vector3D u2);
            return u1;
        }

        private static void GetFaceBoundsInBasis(
            IFace2 face,
            Vector3D origin,
            Vector3D spanUnit,
            Vector3D thickUnit,
            out double sMin,
            out double sMax,
            out double tMin,
            out double tMax)
        {
            sMin = double.MaxValue;
            sMax = double.MinValue;
            tMin = double.MaxValue;
            tMax = double.MinValue;

            bool foundPoint = false;
            try
            {
                object[] edges = (object[])face.GetEdges();
                if (edges != null)
                {
                    foreach (object eObj in edges)
                    {
                        IEdge edge = eObj as IEdge;
                        if (edge == null) continue;

                        object rawParams = edge.GetCurveParams2();
                        if (rawParams is double[] ep && ep.Length >= 6)
                        {
                            Vector3D[] pts = new[]
                            {
                                new Vector3D(ep[0], ep[1], ep[2]),
                                new Vector3D(ep[3], ep[4], ep[5])
                            };
                            foreach (Vector3D p in pts)
                            {
                                Vector3D rel = p - origin;
                                double s = rel.Dot(spanUnit);
                                double t = rel.Dot(thickUnit);
                                if (s < sMin) sMin = s;
                                if (s > sMax) sMax = s;
                                if (t < tMin) tMin = t;
                                if (t > tMax) tMax = t;
                                foundPoint = true;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!foundPoint)
            {
                double[] box = (double[])face.GetBox();
                if (box != null && box.Length >= 6)
                {
                    for (int ix = 0; ix < 2; ix++)
                    {
                        for (int iy = 0; iy < 2; iy++)
                        {
                            for (int iz = 0; iz < 2; iz++)
                            {
                                Vector3D corner = new Vector3D(
                                    ix == 0 ? box[0] : box[3],
                                    iy == 0 ? box[1] : box[4],
                                    iz == 0 ? box[2] : box[5]);
                                Vector3D rel = corner - origin;
                                double s = rel.Dot(spanUnit);
                                double t = rel.Dot(thickUnit);
                                if (s < sMin) sMin = s;
                                if (s > sMax) sMax = s;
                                if (t < tMin) tMin = t;
                                if (t > tMax) tMax = t;
                            }
                        }
                    }
                }
                else
                {
                    sMin = sMax = tMin = tMax = 0.0;
                }
            }
        }

        private static double ComputeBodyThicknessAlongNormal(IBody2 body, Vector3D planePoint, Vector3D cutDir)
        {
            double maxDepth = 0.0;
            try
            {
                double[] box = (double[])body.GetBodyBox();
                if (box != null && box.Length >= 6)
                {
                    double planeW = planePoint.Dot(cutDir);
                    for (int ix = 0; ix < 2; ix++)
                    {
                        for (int iy = 0; iy < 2; iy++)
                        {
                            for (int iz = 0; iz < 2; iz++)
                            {
                                Vector3D corner = new Vector3D(
                                    ix == 0 ? box[0] : box[3],
                                    iy == 0 ? box[1] : box[4],
                                    iz == 0 ? box[2] : box[5]);
                                double d = corner.Dot(cutDir) - planeW;
                                if (d > maxDepth)
                                    maxDepth = d;
                            }
                        }
                    }
                }
            }
            catch { }

            return maxDepth > 0.0005 ? maxDepth : 0.0127;
        }
    }
}
