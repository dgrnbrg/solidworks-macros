using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Core;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    public struct AngleIntervalDeg
    {
        public double Start;
        public double End;
        public AngleIntervalDeg(double start, double end) { Start = start; End = end; }
    }

    public class DetectedHoleFaceGroup
    {
        public TeardropHoleTarget Target { get; set; }
        public List<IFace2> Faces { get; set; } = new List<IFace2>();
        public Vector3D RefBasisE1 { get; set; }
        public Vector3D RefBasisE2 { get; set; }
        public List<AngleIntervalDeg> AngularIntervals { get; set; } = new List<AngleIntervalDeg>();

        public double TotalCoveredAngleDeg => TopologyScanner.ComputeMergedAngleSpanDeg(AngularIntervals);
    }

    public class DetectedRecessFace
    {
        public CrisscrossBridgeTarget Target { get; set; }
        public IFace2 Face { get; set; }
    }

    public static class TopologyScanner
    {
        public const double MinCircularHoleSweepDeg = 240.0;

        public static bool TryResolveBedReference(
            object selectedEntity,
            bool flipDirection,
            out Vector3D bedUp,
            out Vector3D refPoint)
        {
            bedUp = new Vector3D(0, 0, 1);
            refPoint = new Vector3D(0, 0, 0);

            if (selectedEntity == null)
                return false;

            try
            {
                if (selectedEntity is IFace2 face)
                {
                    ISurface surf = (ISurface)face.GetSurface();
                    if (surf != null && surf.IsPlane())
                    {
                        double[] pParams = (double[])surf.PlaneParams;
                        Vector3D surfNormal = new Vector3D(pParams[0], pParams[1], pParams[2]).Normalized();
                        Vector3D outwardNormal = face.FaceInSurfaceSense() ? -surfNormal : surfNormal;

                        bedUp = flipDirection ? outwardNormal : -outwardNormal;
                        refPoint = new Vector3D(pParams[3], pParams[4], pParams[5]);
                        return true;
                    }
                }
                else if (selectedEntity is IFeature feat)
                {
                    object specFeat = feat.GetSpecificFeature2();
                    if (specFeat is IRefPlane refPlane)
                    {
                        IMathTransform xform = refPlane.Transform;
                        if (xform != null)
                        {
                            double[] arr = (double[])xform.ArrayData;
                            Vector3D normal = new Vector3D(arr[6], arr[7], arr[8]).Normalized();
                            bedUp = flipDirection ? -normal : normal;
                            refPoint = new Vector3D(arr[9], arr[10], arr[11]);
                            return true;
                        }
                    }
                }
                else if (selectedEntity is IEdge edge)
                {
                    ICurve curve = (ICurve)edge.GetCurve();
                    if (curve != null && curve.IsLine())
                    {
                        double[] lParams = (double[])curve.LineParams;
                        Vector3D dir = new Vector3D(lParams[3], lParams[4], lParams[5]).Normalized();
                        bedUp = flipDirection ? -dir : dir;
                        refPoint = new Vector3D(lParams[0], lParams[1], lParams[2]);
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        public static IFace2 FindAdjacentPerpendicularPlanarFace(
            IList<IFace2> holeFaces,
            Vector3D holeAxisUnit,
            out double planeAxialPositionW,
            Vector3D holeAxisOrigin)
        {
            planeAxialPositionW = 0.0;
            if (holeFaces == null) return null;

            foreach (IFace2 cylFace in holeFaces)
            {
                if (cylFace == null) continue;
                object[] edges = (object[])cylFace.GetEdges();
                if (edges == null) continue;

                foreach (object eObj in edges)
                {
                    IEdge edge = eObj as IEdge;
                    if (edge == null) continue;

                    object[] adjFaces = (object[])edge.GetTwoAdjacentFaces2();
                    if (adjFaces == null) continue;

                    foreach (object afObj in adjFaces)
                    {
                        IFace2 adjFace = afObj as IFace2;
                        if (adjFace == null || adjFace == cylFace) continue;

                        ISurface surf = (ISurface)adjFace.GetSurface();
                        if (surf != null && surf.IsPlane())
                        {
                            double[] pParams = (double[])surf.PlaneParams;
                            if (pParams != null && pParams.Length >= 6)
                            {
                                Vector3D n = new Vector3D(pParams[0], pParams[1], pParams[2]).Normalized();
                                if (Math.Abs(n.Dot(holeAxisUnit)) > 0.998)
                                {
                                    Vector3D planePt = new Vector3D(pParams[3], pParams[4], pParams[5]);
                                    planeAxialPositionW = (planePt - holeAxisOrigin).Dot(holeAxisUnit);
                                    return adjFace;
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public static IEdge FindCircularHoleEdge(
            IList<IFace2> holeFaces,
            Vector3D holeAxisOrigin,
            Vector3D holeAxisUnit,
            out double edgeAxialPositionW)
        {
            edgeAxialPositionW = 0.0;
            if (holeFaces == null) return null;

            foreach (IFace2 cylFace in holeFaces)
            {
                if (cylFace == null) continue;
                object[] edges = (object[])cylFace.GetEdges();
                if (edges == null) continue;

                foreach (object eObj in edges)
                {
                    IEdge edge = eObj as IEdge;
                    if (edge == null) continue;

                    ICurve curve = (ICurve)edge.GetCurve();
                    if (curve != null && curve.IsCircle())
                    {
                        double[] cParams = (double[])curve.CircleParams;
                        if (cParams != null && cParams.Length >= 3)
                        {
                            Vector3D cPt = new Vector3D(cParams[0], cParams[1], cParams[2]);
                            edgeAxialPositionW = (cPt - holeAxisOrigin).Dot(holeAxisUnit);
                            return edge;
                        }
                    }
                }
            }

            return null;
        }

        public static List<IBody2> GetTargetSolidBodies(IModelDoc2 model, IList<IBody2> explicitBodies = null)
        {
            List<IBody2> bodies = new List<IBody2>();
            if (explicitBodies != null && explicitBodies.Count > 0)
            {
                bodies.AddRange(explicitBodies);
                return bodies;
            }

            if (model is IPartDoc partDoc)
            {
                object rawBodies = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, true);
                if (rawBodies is object[] arr)
                {
                    foreach (object obj in arr)
                    {
                        if (obj is IBody2 b)
                            bodies.Add(b);
                    }
                }
            }
            return bodies;
        }

        /// <summary>
        /// Validates whether a single clicked face belongs to a qualifying circular hole (>= 240 deg total
        /// angular sweep across coaxial concave faces on its owning body), rejecting rectangular corner fillets
        /// (90 deg) and planar/non-circular faces.
        /// </summary>
        public static bool IsQualifyingCircularHoleFace(IFace2 face, DfmConfig config)
        {
            if (face == null || config == null) return false;

            if (!TryExtractConcaveCylinderHole(
                face,
                config.BedUpVector,
                5.0,
                0.0005,
                out Vector3D axisOrigin,
                out Vector3D axisUnit,
                out double radius,
                out double wMin,
                out double wMax,
                out double circleAngleDeg))
            {
                return false;
            }

            Vector3D.BuildInPlaneBasis(axisUnit, 0.0, out Vector3D e1, out Vector3D e2);
            List<AngleIntervalDeg> intervals = ExtractCircularAngularIntervals(face, axisOrigin, axisUnit, e1, e2);
            double singleCoverage = ComputeMergedAngleSpanDeg(intervals);
            if (singleCoverage >= MinCircularHoleSweepDeg)
                return true;

            // Check coaxial partner faces on the owning body (e.g., two 180 deg half-cylinders of a split circle)
            try
            {
                IBody2 body = (IBody2)face.GetBody();
                if (body != null)
                {
                    object[] faces = (object[])body.GetFaces();
                    if (faces != null)
                    {
                        foreach (object fObj in faces)
                        {
                            IFace2 other = fObj as IFace2;
                            if (other == null || other == face) continue;

                            if (!TryExtractConcaveCylinderHole(
                                other,
                                config.BedUpVector,
                                5.0,
                                0.0005,
                                out Vector3D oOrigin,
                                out Vector3D oUnit,
                                out double oRadius,
                                out double owMin,
                                out double owMax,
                                out double oAngleDeg))
                            {
                                continue;
                            }

                            if (IsCoaxialAndOverlapping(
                                axisOrigin, axisUnit, radius, wMin, wMax,
                                oOrigin, oUnit, oRadius, owMin, owMax))
                            {
                                intervals.AddRange(ExtractCircularAngularIntervals(other, axisOrigin, axisUnit, e1, e2));
                            }
                        }
                    }
                }
            }
            catch { }

            return ComputeMergedAngleSpanDeg(intervals) >= MinCircularHoleSweepDeg;
        }

        public static List<DetectedHoleFaceGroup> ScanQualifyingTeardropHoles(
            IList<IBody2> bodies,
            DfmConfig config)
        {
            List<DetectedHoleFaceGroup> groups = new List<DetectedHoleFaceGroup>();
            if (bodies == null || config == null)
                return groups;

            int holeCounter = 1;
            foreach (IBody2 body in bodies)
            {
                if (body == null) continue;
                object[] faces = (object[])body.GetFaces();
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    IFace2 face = fObj as IFace2;
                    if (face == null) continue;

                    if (!TryExtractConcaveCylinderHole(
                        face,
                        config.BedUpVector,
                        config.MinCircleAngleToBedDeg,
                        config.MinHoleDiameterMeters,
                        out Vector3D axisOrigin,
                        out Vector3D axisUnit,
                        out double radius,
                        out double wMin,
                        out double wMax,
                        out double circleAngleDeg))
                    {
                        continue;
                    }

                    DetectedHoleFaceGroup existing = FindCoaxialHoleGroup(groups, axisOrigin, axisUnit, radius, wMin, wMax);
                    if (existing != null)
                    {
                        existing.Faces.Add(face);
                        if (wMin < existing.Target.AxialMinMeters) existing.Target.AxialMinMeters = wMin;
                        if (wMax > existing.Target.AxialMaxMeters) existing.Target.AxialMaxMeters = wMax;
                        existing.AngularIntervals.AddRange(ExtractCircularAngularIntervals(
                            face, existing.Target.AxisOrigin, existing.Target.AxisUnit, existing.RefBasisE1, existing.RefBasisE2));
                    }
                    else
                    {
                        Vector3D.BuildInPlaneBasis(axisUnit, 0.0, out Vector3D e1, out Vector3D e2);
                        List<AngleIntervalDeg> intervals = ExtractCircularAngularIntervals(face, axisOrigin, axisUnit, e1, e2);

                        groups.Add(new DetectedHoleFaceGroup
                        {
                            Faces = new List<IFace2> { face },
                            RefBasisE1 = e1,
                            RefBasisE2 = e2,
                            AngularIntervals = intervals,
                            Target = new TeardropHoleTarget
                            {
                                HoleId = string.Format("Hole_{0}", holeCounter++),
                                AxisOrigin = axisOrigin,
                                AxisUnit = axisUnit,
                                RadiusMeters = radius,
                                AxialMinMeters = wMin,
                                AxialMaxMeters = wMax,
                                CircleAngleToBedDeg = circleAngleDeg
                            }
                        });
                    }
                }
            }

            // Filter out rectangular corner fillets (90 deg) and partial arcs (< 240 deg total circular sweep)
            List<DetectedHoleFaceGroup> circularHoles = new List<DetectedHoleFaceGroup>();
            int finalIndex = 1;
            foreach (DetectedHoleFaceGroup g in groups)
            {
                if (g.TotalCoveredAngleDeg >= MinCircularHoleSweepDeg)
                {
                    g.Target.HoleId = string.Format("Hole_{0}", finalIndex++);
                    circularHoles.Add(g);
                }
            }

            return circularHoles;
        }

        public static List<DetectedHoleFaceGroup> BuildTeardropTargetsFromSelectedFaces(
            IList<IFace2> selectedFaces,
            DfmConfig config)
        {
            List<DetectedHoleFaceGroup> groups = new List<DetectedHoleFaceGroup>();
            if (selectedFaces == null || config == null)
                return groups;

            int holeCounter = 1;
            foreach (IFace2 face in selectedFaces)
            {
                if (face == null) continue;
                if (!IsQualifyingCircularHoleFace(face, config))
                    continue;

                if (!TryExtractConcaveCylinderHole(
                    face,
                    config.BedUpVector,
                    5.0,
                    0.0005,
                    out Vector3D axisOrigin,
                    out Vector3D axisUnit,
                    out double radius,
                    out double wMin,
                    out double wMax,
                    out double circleAngleDeg))
                {
                    continue;
                }

                DetectedHoleFaceGroup existing = FindCoaxialHoleGroup(groups, axisOrigin, axisUnit, radius, wMin, wMax);
                if (existing != null)
                {
                    if (!existing.Faces.Contains(face))
                        existing.Faces.Add(face);
                    if (wMin < existing.Target.AxialMinMeters) existing.Target.AxialMinMeters = wMin;
                    if (wMax > existing.Target.AxialMaxMeters) existing.Target.AxialMaxMeters = wMax;
                }
                else
                {
                    Vector3D.BuildInPlaneBasis(axisUnit, 0.0, out Vector3D e1, out Vector3D e2);
                    groups.Add(new DetectedHoleFaceGroup
                    {
                        Faces = new List<IFace2> { face },
                        RefBasisE1 = e1,
                        RefBasisE2 = e2,
                        Target = new TeardropHoleTarget
                        {
                            HoleId = string.Format("Hole_{0}", holeCounter++),
                            AxisOrigin = axisOrigin,
                            AxisUnit = axisUnit,
                            RadiusMeters = radius,
                            AxialMinMeters = wMin,
                            AxialMaxMeters = wMax,
                            CircleAngleToBedDeg = circleAngleDeg
                        }
                    });
                }
            }

            return groups;
        }

        public static List<DetectedRecessFace> ScanQualifyingDownwardRecesses(
            IList<IBody2> bodies,
            DfmConfig config)
        {
            List<DetectedRecessFace> results = new List<DetectedRecessFace>();
            if (bodies == null || config == null)
                return results;

            Vector3D bedUp = config.BedUpVector.Normalized();
            int recessCounter = 1;

            foreach (IBody2 body in bodies)
            {
                if (body == null) continue;

                double bodyMinZ = GetBodyMinProjectionAlongAxis(body, bedUp);

                object[] faces = (object[])body.GetFaces();
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    IFace2 face = fObj as IFace2;
                    if (face == null) continue;

                    if (TryExtractRecessTarget(
                        face,
                        bedUp,
                        config.BridgeAngleOffsetDeg,
                        requireDownwardNormal: true,
                        requireInnerLoop: true,
                        minHeightAboveBedPlane: bodyMinZ + Math.Max(0.00005, 0.5 * config.LayerHeightMeters),
                        recessId: string.Format("Recess_{0}", recessCounter),
                        out CrisscrossBridgeTarget target))
                    {
                        recessCounter++;
                        results.Add(new DetectedRecessFace
                        {
                            Face = face,
                            Target = target
                        });
                    }
                }
            }

            return results;
        }

        public static List<DetectedRecessFace> BuildBridgeTargetsFromSelectedFaces(
            IList<IFace2> selectedFaces,
            DfmConfig config)
        {
            List<DetectedRecessFace> results = new List<DetectedRecessFace>();
            if (selectedFaces == null || config == null)
                return results;

            Vector3D bedUp = config.BedUpVector.Normalized();
            int recessCounter = 1;

            foreach (IFace2 face in selectedFaces)
            {
                if (face == null) continue;

                if (TryExtractRecessTarget(
                    face,
                    bedUp,
                    config.BridgeAngleOffsetDeg,
                    requireDownwardNormal: false,
                    requireInnerLoop: false,
                    minHeightAboveBedPlane: double.NegativeInfinity,
                    recessId: string.Format("Recess_{0}", recessCounter),
                    out CrisscrossBridgeTarget target))
                {
                    recessCounter++;
                    results.Add(new DetectedRecessFace
                    {
                        Face = face,
                        Target = target
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Extracts normalized [0, 360) circular angular intervals around axisUnit from a cylindrical face's
        /// circular edges (ICurve.IsCircle()). Used to distinguish true circular holes (>= 240 deg) from
        /// rectangular corner fillets (90 deg).
        /// </summary>
        public static List<AngleIntervalDeg> ExtractCircularAngularIntervals(
            IFace2 face,
            Vector3D axisOrigin,
            Vector3D axisUnit,
            Vector3D e1,
            Vector3D e2)
        {
            List<AngleIntervalDeg> intervals = new List<AngleIntervalDeg>();
            if (face == null) return intervals;

            try
            {
                object[] edges = (object[])face.GetEdges();
                if (edges == null) return intervals;

                foreach (object eObj in edges)
                {
                    IEdge edge = eObj as IEdge;
                    if (edge == null) continue;

                    ICurve curve = (ICurve)edge.GetCurve();
                    if (curve == null || !curve.IsCircle())
                        continue;

                    double[] cParams = (double[])curve.CircleParams;
                    if (cParams != null && cParams.Length >= 7)
                    {
                        Vector3D cNormal = new Vector3D(cParams[3], cParams[4], cParams[5]).Normalized();
                        if (Math.Abs(cNormal.Dot(axisUnit)) < 0.995)
                            continue;
                    }

                    // Full closed circular edge (no start vertex or closed full 2*PI curve)
                    if (edge.GetStartVertex() == null)
                    {
                        intervals.Add(new AngleIntervalDeg(0.0, 360.0));
                        continue;
                    }

                    double[] ep = (double[])edge.GetCurveParams2();
                    if (ep == null || ep.Length < 8)
                        continue;

                    double tStart = ep[6];
                    double tEnd = ep[7];
                    if (Math.Abs(tEnd - tStart) < 1e-6)
                        continue;

                    // Sample 8 points along the circular arc to unwrap its exact continuous angle span
                    const int samples = 8;
                    double unwrappedMin = 0.0;
                    double unwrappedMax = 0.0;
                    double prevAngle = 0.0;
                    double unwrapped = 0.0;
                    bool hasFirst = false;

                    for (int i = 0; i <= samples; i++)
                    {
                        double t = tStart + (tEnd - tStart) * ((double)i / samples);
                        double[] eval = (double[])curve.Evaluate2(t, 0);
                        if (eval == null || eval.Length < 3) continue;

                        Vector3D rel = new Vector3D(eval[0], eval[1], eval[2]) - axisOrigin;
                        double x = rel.Dot(e1);
                        double y = rel.Dot(e2);
                        double angle = Math.Atan2(y, x) * (180.0 / Math.PI); // (-180, 180]

                        if (!hasFirst)
                        {
                            unwrapped = angle;
                            unwrappedMin = unwrapped;
                            unwrappedMax = unwrapped;
                            prevAngle = angle;
                            hasFirst = true;
                        }
                        else
                        {
                            double delta = angle - prevAngle;
                            while (delta > 180.0) delta -= 360.0;
                            while (delta < -180.0) delta += 360.0;
                            unwrapped += delta;
                            if (unwrapped < unwrappedMin) unwrappedMin = unwrapped;
                            if (unwrapped > unwrappedMax) unwrappedMax = unwrapped;
                            prevAngle = angle;
                        }
                    }

                    if (!hasFirst) continue;

                    double span = unwrappedMax - unwrappedMin;
                    if (span >= 359.5)
                    {
                        intervals.Add(new AngleIntervalDeg(0.0, 360.0));
                    }
                    else if (span > 0.5)
                    {
                        AddNormalizedInterval(intervals, unwrappedMin, unwrappedMax);
                    }
                }
            }
            catch { }

            return intervals;
        }

        private static void AddNormalizedInterval(List<AngleIntervalDeg> list, double aMin, double aMax)
        {
            // Shift into [0, 720)
            while (aMin < 0.0) { aMin += 360.0; aMax += 360.0; }
            while (aMin >= 360.0) { aMin -= 360.0; aMax -= 360.0; }

            if (aMax <= 360.0)
            {
                list.Add(new AngleIntervalDeg(aMin, aMax));
            }
            else
            {
                list.Add(new AngleIntervalDeg(aMin, 360.0));
                list.Add(new AngleIntervalDeg(0.0, Math.Min(360.0, aMax - 360.0)));
            }
        }

        public static double ComputeMergedAngleSpanDeg(List<AngleIntervalDeg> rawIntervals)
        {
            if (rawIntervals == null || rawIntervals.Count == 0)
                return 0.0;

            List<AngleIntervalDeg> sorted = new List<AngleIntervalDeg>(rawIntervals);
            sorted.Sort((a, b) => a.Start.CompareTo(b.Start));

            double total = 0.0;
            double curStart = sorted[0].Start;
            double curEnd = sorted[0].End;

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start <= curEnd + 0.5)
                {
                    if (sorted[i].End > curEnd) curEnd = sorted[i].End;
                }
                else
                {
                    total += (curEnd - curStart);
                    curStart = sorted[i].Start;
                    curEnd = sorted[i].End;
                }
            }
            total += (curEnd - curStart);
            return Math.Min(360.0, total);
        }

        private static bool TryExtractConcaveCylinderHole(
            IFace2 face,
            Vector3D bedUp,
            double minCircleAngleToBedDeg,
            double minDiameterMeters,
            out Vector3D axisOrigin,
            out Vector3D axisUnit,
            out double radius,
            out double wMin,
            out double wMax,
            out double circleAngleDeg)
        {
            axisOrigin = new Vector3D();
            axisUnit = new Vector3D(0, 0, 1);
            radius = 0;
            wMin = 0;
            wMax = 0;
            circleAngleDeg = 0;

            try
            {
                ISurface surf = (ISurface)face.GetSurface();
                if (surf == null || !surf.IsCylinder())
                    return false;

                double[] cParams = (double[])surf.CylinderParams;
                if (cParams == null || cParams.Length < 7)
                    return false;

                axisOrigin = new Vector3D(cParams[0], cParams[1], cParams[2]);
                axisUnit = new Vector3D(cParams[3], cParams[4], cParams[5]).Normalized();
                radius = cParams[6];

                if (radius * 2.0 < minDiameterMeters)
                    return false;

                // Internal hole (concave) check
                if (!face.FaceInSurfaceSense())
                    return false;

                circleAngleDeg = TeardropEngine.ComputeCircleAngleToBedDeg(axisUnit, bedUp);
                if (!TeardropEngine.QualifiesForTeardrop(axisUnit, bedUp, minCircleAngleToBedDeg))
                    return false;

                ComputeFaceAxialSpan(face, axisOrigin, axisUnit, out wMin, out wMax);
                if (wMax - wMin < 1e-5)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ComputeFaceAxialSpan(
            IFace2 face,
            Vector3D axisOrigin,
            Vector3D axisUnit,
            out double wMin,
            out double wMax)
        {
            wMin = double.MaxValue;
            wMax = double.MinValue;

            object[] edges = (object[])face.GetEdges();
            if (edges != null)
            {
                foreach (object eObj in edges)
                {
                    IEdge edge = eObj as IEdge;
                    if (edge == null) continue;

                    ICurve curve = (ICurve)edge.GetCurve();
                    object rawParams = edge.GetCurveParams2();
                    if (rawParams is double[] ep && ep.Length >= 6)
                    {
                        Vector3D pStart = new Vector3D(ep[0], ep[1], ep[2]);
                        Vector3D pEnd = new Vector3D(ep[3], ep[4], ep[5]);
                        double w1 = (pStart - axisOrigin).Dot(axisUnit);
                        double w2 = (pEnd - axisOrigin).Dot(axisUnit);
                        if (w1 < wMin) wMin = w1;
                        if (w1 > wMax) wMax = w1;
                        if (w2 < wMin) wMin = w2;
                        if (w2 > wMax) wMax = w2;

                        if (curve != null && ep.Length >= 8)
                        {
                            double uMid = 0.5 * (ep[6] + ep[7]);
                            double[] eval = (double[])curve.Evaluate2(uMid, 0);
                            if (eval != null && eval.Length >= 3)
                            {
                                Vector3D pMid = new Vector3D(eval[0], eval[1], eval[2]);
                                double wMid = (pMid - axisOrigin).Dot(axisUnit);
                                if (wMid < wMin) wMin = wMid;
                                if (wMid > wMax) wMax = wMid;
                            }
                        }
                    }
                }
            }

            if (wMin > wMax)
            {
                double[] box = (double[])face.GetBox();
                if (box != null && box.Length >= 6)
                {
                    Vector3D[] corners = new[]
                    {
                        new Vector3D(box[0], box[1], box[2]),
                        new Vector3D(box[3], box[4], box[5])
                    };
                    foreach (Vector3D c in corners)
                    {
                        double w = (c - axisOrigin).Dot(axisUnit);
                        if (w < wMin) wMin = w;
                        if (w > wMax) wMax = w;
                    }
                }
            }
        }

        private static bool IsCoaxialAndOverlapping(
            Vector3D originA, Vector3D axisA, double radiusA, double wMinA, double wMaxA,
            Vector3D originB, Vector3D axisB, double radiusB, double wMinB, double wMaxB)
        {
            if (Math.Abs(radiusA - radiusB) > 1e-5)
                return false;

            double dot = Math.Abs(axisA.Dot(axisB));
            if (dot < 0.9995)
                return false;

            Vector3D delta = originB - originA;
            Vector3D perp = delta - axisA * delta.Dot(axisA);
            if (perp.Length > 1e-4)
                return false;

            double offset = delta.Dot(axisA);
            double sign = axisA.Dot(axisB) >= 0 ? 1.0 : -1.0;
            double mappedA = offset + sign * wMinB;
            double mappedB = offset + sign * wMaxB;
            double mappedMin = Math.Min(mappedA, mappedB);
            double mappedMax = Math.Max(mappedA, mappedB);

            return mappedMax >= wMinA - 1e-4 && mappedMin <= wMaxA + 1e-4;
        }

        private static DetectedHoleFaceGroup FindCoaxialHoleGroup(
            List<DetectedHoleFaceGroup> groups,
            Vector3D axisOrigin,
            Vector3D axisUnit,
            double radius,
            double wMin,
            double wMax)
        {
            foreach (DetectedHoleFaceGroup g in groups)
            {
                if (IsCoaxialAndOverlapping(
                    g.Target.AxisOrigin, g.Target.AxisUnit, g.Target.RadiusMeters, g.Target.AxialMinMeters, g.Target.AxialMaxMeters,
                    axisOrigin, axisUnit, radius, wMin, wMax))
                {
                    return g;
                }
            }
            return null;
        }

        private static bool TryExtractRecessTarget(
            IFace2 face,
            Vector3D bedUp,
            double bridgeAngleOffsetDeg,
            bool requireDownwardNormal,
            bool requireInnerLoop,
            double minHeightAboveBedPlane,
            string recessId,
            out CrisscrossBridgeTarget target)
        {
            target = null;

            try
            {
                ISurface surf = (ISurface)face.GetSurface();
                if (surf == null || !surf.IsPlane())
                    return false;

                double[] pParams = (double[])surf.PlaneParams;
                Vector3D surfNormal = new Vector3D(pParams[0], pParams[1], pParams[2]).Normalized();
                Vector3D outwardNormal = face.FaceInSurfaceSense() ? -surfNormal : surfNormal;

                if (requireDownwardNormal)
                {
                    if (outwardNormal.Dot(bedUp) > -0.99)
                        return false;
                }

                Vector3D planeOrigin = new Vector3D(pParams[3], pParams[4], pParams[5]);
                double[] box = (double[])face.GetBox();
                if (box != null && box.Length >= 6)
                {
                    Vector3D boxCenter = new Vector3D(
                        0.5 * (box[0] + box[3]),
                        0.5 * (box[1] + box[4]),
                        0.5 * (box[2] + box[5]));
                    planeOrigin = boxCenter - outwardNormal * (boxCenter - planeOrigin).Dot(outwardNormal);
                }

                double zProj = planeOrigin.Dot(bedUp);
                if (zProj < minHeightAboveBedPlane)
                    return false;

                object[] loops = (object[])face.GetLoops();
                if (loops == null || loops.Length == 0)
                    return false;

                ILoop2 outerLoop = null;
                List<ILoop2> innerLoops = new List<ILoop2>();

                foreach (object lObj in loops)
                {
                    ILoop2 lp = lObj as ILoop2;
                    if (lp == null) continue;
                    if (lp.IsOuter())
                        outerLoop = lp;
                    else
                        innerLoops.Add(lp);
                }

                if (outerLoop == null)
                    return false;

                if (requireInnerLoop && innerLoops.Count == 0)
                    return false;

                Vector3D cutUp = -outwardNormal;
                Vector3D.BuildInPlaneBasis(cutUp, bridgeAngleOffsetDeg, out Vector3D u1, out Vector3D u2);

                List<Point2D> outerPoly = ExtractLoopPolygon2D(outerLoop, planeOrigin, u2, u1, 64,
                    out bool isOuterCircle, out Point2D outerCircleCenter, out double outerCircleRadius);

                if (outerPoly.Count < 3)
                    return false;

                List<BoundingBox2D> innerBoxes = new List<BoundingBox2D>();
                List<InnerHole2D> innerHoles = new List<InnerHole2D>();
                foreach (ILoop2 innerLp in innerLoops)
                {
                    List<Point2D> innerPoly = ExtractLoopPolygon2D(innerLp, planeOrigin, u2, u1, 36,
                        out bool isInnerCircle, out Point2D innerCenter, out double innerRadius);
                    if (isInnerCircle && innerRadius > 1e-6)
                    {
                        innerBoxes.Add(new BoundingBox2D(
                            innerCenter.X - innerRadius,
                            innerCenter.X + innerRadius,
                            innerCenter.Y - innerRadius,
                            innerCenter.Y + innerRadius));
                        innerHoles.Add(new InnerHole2D
                        {
                            Center = innerCenter,
                            RadiusMeters = innerRadius,
                            IsCircular = true,
                            Polygon2D = innerPoly
                        });
                    }
                    else if (innerPoly.Count >= 3)
                    {
                        BoundingBox2D box2d = BridgeClippingEngine.ComputeBoundingBox(innerPoly);
                        innerBoxes.Add(box2d);
                        Point2D polyCenter = box2d.Center;
                        double maxRad = 0.0;
                        foreach (Point2D pt in innerPoly)
                        {
                            double d = pt.DistanceTo(polyCenter);
                            if (d > maxRad) maxRad = d;
                        }
                        innerHoles.Add(new InnerHole2D
                        {
                            Center = polyCenter,
                            RadiusMeters = maxRad,
                            IsCircular = false,
                            Polygon2D = innerPoly
                        });
                    }
                }

                if (innerBoxes.Count == 0)
                {
                    BoundingBox2D outerBox = BridgeClippingEngine.ComputeBoundingBox(outerPoly);
                    double halfW = 0.25 * outerBox.Width;
                    double halfH = 0.25 * outerBox.Height;
                    Point2D c = outerBox.Center;
                    BoundingBox2D synthBox = new BoundingBox2D(c.X - halfW, c.X + halfW, c.Y - halfH, c.Y + halfH);
                    innerBoxes.Add(synthBox);
                    innerHoles.Add(new InnerHole2D
                    {
                        Center = c,
                        RadiusMeters = Math.Sqrt(halfW * halfW + halfH * halfH),
                        IsCircular = false,
                        Polygon2D = new List<Point2D>
                        {
                            new Point2D(synthBox.XMin, synthBox.YMin),
                            new Point2D(synthBox.XMax, synthBox.YMin),
                            new Point2D(synthBox.XMax, synthBox.YMax),
                            new Point2D(synthBox.XMin, synthBox.YMax)
                        }
                    });
                }

                target = new CrisscrossBridgeTarget
                {
                    RecessId = recessId,
                    PlaneOrigin = planeOrigin,
                    OutwardNormal = outwardNormal,
                    IsCircularOuter = isOuterCircle,
                    OuterCircleCenter2D = outerCircleCenter,
                    OuterCircleRadiusMeters = outerCircleRadius,
                    OuterLoopPolygon2D = outerPoly,
                    InnerHoleBoxes2D = innerBoxes,
                    InnerHoles2D = innerHoles
                };

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Point2D> ExtractLoopPolygon2D(
            ILoop2 loop,
            Vector3D planeOrigin,
            Vector3D xAxisU2,
            Vector3D yAxisU1,
            int arcSubdivisions,
            out bool isSingleCircle,
            out Point2D circleCenter2D,
            out double circleRadius)
        {
            isSingleCircle = false;
            circleCenter2D = new Point2D(0, 0);
            circleRadius = 0;

            List<Point2D> pts = new List<Point2D>();
            object[] coEdges = (object[])loop.GetCoEdges();
            if (coEdges == null || coEdges.Length == 0)
                return pts;

            // Check if all CoEdges in the loop form a single circle (even if split into 2 semicircular CoEdges)
            bool allCircular = true;
            Point2D candidateCenter = new Point2D(0, 0);
            double candidateRadius = 0.0;

            for (int cIdx = 0; cIdx < coEdges.Length; cIdx++)
            {
                ICoEdge ce = coEdges[cIdx] as ICoEdge;
                IEdge edge = ce != null ? (IEdge)ce.GetEdge() : null;
                ICurve curve = edge != null ? (ICurve)edge.GetCurve() : null;
                if (curve == null || !curve.IsCircle())
                {
                    allCircular = false;
                    break;
                }

                double[] cParams = (double[])curve.CircleParams;
                if (cParams == null || cParams.Length < 7)
                {
                    allCircular = false;
                    break;
                }

                Vector3D c3d = new Vector3D(cParams[0], cParams[1], cParams[2]);
                Vector3D d = c3d - planeOrigin;
                Point2D center2d = new Point2D(d.Dot(xAxisU2), d.Dot(yAxisU1));
                double rad = cParams[6];

                if (cIdx == 0)
                {
                    candidateCenter = center2d;
                    candidateRadius = rad;
                }
                else if (candidateCenter.DistanceTo(center2d) > 1e-4 || Math.Abs(candidateRadius - rad) > 1e-4)
                {
                    allCircular = false;
                    break;
                }
            }

            if (allCircular && candidateRadius > 1e-6)
            {
                circleCenter2D = candidateCenter;
                circleRadius = candidateRadius;
                isSingleCircle = true;
                return BridgeClippingEngine.SampleCirclePolygon(circleCenter2D, circleRadius, arcSubdivisions);
            }

            foreach (object ceObj in coEdges)
            {
                ICoEdge ce = ceObj as ICoEdge;
                if (ce == null) continue;

                IEdge edge = (IEdge)ce.GetEdge();
                ICurve curve = edge != null ? (ICurve)edge.GetCurve() : null;
                double[] cParams = (double[])ce.GetCurveParams();
                if (curve == null || cParams == null || cParams.Length < 8)
                    continue;

                Vector3D directedStart3D = new Vector3D(cParams[0], cParams[1], cParams[2]);

                if (curve.IsLine())
                {
                    Vector3D rel = directedStart3D - planeOrigin;
                    pts.Add(new Point2D(rel.Dot(xAxisU2), rel.Dot(yAxisU1)));
                    continue;
                }

                double tA = cParams[6];
                double tB = cParams[7];
                double[] evalA = (double[])curve.Evaluate2(tA, 0);
                double[] evalB = (double[])curve.Evaluate2(tB, 0);
                if (evalA != null && evalB != null && evalA.Length >= 3 && evalB.Length >= 3)
                {
                    Vector3D pA = new Vector3D(evalA[0], evalA[1], evalA[2]);
                    Vector3D pB = new Vector3D(evalB[0], evalB[1], evalB[2]);
                    if ((pB - directedStart3D).Length < (pA - directedStart3D).Length)
                    {
                        double tmp = tA;
                        tA = tB;
                        tB = tmp;
                    }
                }

                int steps = Math.Max(8, arcSubdivisions / Math.Max(1, coEdges.Length));
                for (int i = 0; i < steps; i++)
                {
                    double t = tA + (tB - tA) * ((double)i / steps);
                    double[] eval = (double[])curve.Evaluate2(t, 0);
                    if (eval != null && eval.Length >= 3)
                    {
                        Vector3D p3d = new Vector3D(eval[0], eval[1], eval[2]);
                        Vector3D rel = p3d - planeOrigin;
                        pts.Add(new Point2D(rel.Dot(xAxisU2), rel.Dot(yAxisU1)));
                    }
                }
            }

            return pts;
        }

        private static double GetBodyMinProjectionAlongAxis(IBody2 body, Vector3D unitAxis)
        {
            try
            {
                double[] box = (double[])body.GetBodyBox();
                if (box != null && box.Length >= 6)
                {
                    double minProj = double.MaxValue;
                    for (int ix = 0; ix <= 3; ix += 3)
                    {
                        for (int iy = 1; iy <= 4; iy += 3)
                        {
                            for (int iz = 2; iz <= 5; iz += 3)
                            {
                                Vector3D corner = new Vector3D(box[ix], box[iy], box[iz]);
                                double proj = corner.Dot(unitAxis);
                                if (proj < minProj) minProj = proj;
                            }
                        }
                    }
                    return minProj;
                }
            }
            catch { }

            return 0.0;
        }
    }
}
