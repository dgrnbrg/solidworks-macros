using System;
using System.Collections.Generic;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.Core
{
    public class TwoTierBridgeContours2D
    {
        public bool UseAnalyticalCircleTier1 { get; set; }
        public double OuterRadius { get; set; }
        public Point2D OuterCenter { get; set; }
        public BoundingBox2D InnerBox { get; set; }

        public Point2D RightChordBottom { get; set; }
        public Point2D RightChordTop { get; set; }
        public Point2D TopArcMid { get; set; }
        public Point2D LeftChordTop { get; set; }
        public Point2D LeftChordBottom { get; set; }
        public Point2D BottomArcMid { get; set; }

        public List<Point2D> Tier1Polygon { get; set; } = new List<Point2D>();
        public List<Point2D> Tier2Polygon { get; set; } = new List<Point2D>();
    }

    public class BridgeTierContours2D
    {
        public int TierIndex { get; set; }
        public double PrimaryAngleDeg { get; set; }
        public List<List<Point2D>> CutPolygons { get; set; } = new List<List<Point2D>>();
    }

    /// <summary>
    /// Generates simple, guaranteed non-self-intersecting 2D closed contours for single-hole
    /// 2-tier bridges and multi-hole N-tier tangent-line bridge decompositions.
    /// </summary>
    public static class BridgeClippingEngine
    {
        public const double InnerTangentClearanceMeters = 0.000015; // 15 microns
        public const double OuterSliverInsetMeters = 0.000010;      // 10 microns
        public const double MinBridgeWidthMeters = 0.00025;         // 0.25 mm minimum printable bridge ribbon
        public const int MaxBridgeTiers = 6;

        public static BoundingBox2D ComputeBoundingBox(IList<Point2D> loopPoints)
        {
            if (loopPoints == null || loopPoints.Count == 0)
                return new BoundingBox2D(0, 0, 0, 0);

            double xMin = double.MaxValue, xMax = double.MinValue;
            double yMin = double.MaxValue, yMax = double.MinValue;

            for (int i = 0; i < loopPoints.Count; i++)
            {
                Point2D p = loopPoints[i];
                if (p.X < xMin) xMin = p.X;
                if (p.X > xMax) xMax = p.X;
                if (p.Y < yMin) yMin = p.Y;
                if (p.Y > yMax) yMax = p.Y;
            }

            return new BoundingBox2D(xMin, xMax, yMin, yMax);
        }

        public static BoundingBox2D ExpandBox(BoundingBox2D box, double expandMeters)
        {
            return new BoundingBox2D(
                box.XMin - expandMeters,
                box.XMax + expandMeters,
                box.YMin - expandMeters,
                box.YMax + expandMeters);
        }

        public static List<Point2D> InsetPolygonTowardCentroid(IList<Point2D> polygon, double insetMeters)
        {
            if (polygon == null || polygon.Count < 3 || insetMeters <= 0.0)
                return polygon != null ? new List<Point2D>(polygon) : new List<Point2D>();

            double cx = 0.0, cy = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                cx += polygon[i].X;
                cy += polygon[i].Y;
            }
            cx /= polygon.Count;
            cy /= polygon.Count;

            List<Point2D> inset = new List<Point2D>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                double dx = polygon[i].X - cx;
                double dy = polygon[i].Y - cy;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > insetMeters * 2.0)
                {
                    double scale = (dist - insetMeters) / dist;
                    inset.Add(new Point2D(cx + dx * scale, cy + dy * scale));
                }
                else
                {
                    inset.Add(polygon[i]);
                }
            }
            return inset;
        }

        public static List<Point2D> ClipPolygonToVerticalStrip(IList<Point2D> polygon, double xMin, double xMax)
        {
            if (polygon == null || polygon.Count < 3)
                return new List<Point2D>();

            List<Point2D> stage1 = ClipAgainstHalfPlane(polygon, 1.0, 0.0, xMin);
            List<Point2D> stage2 = ClipAgainstHalfPlane(stage1, -1.0, 0.0, -xMax);
            return RemoveNearDuplicateVertices(stage2);
        }

        public static List<Point2D> ClipPolygonToBox(IList<Point2D> polygon, BoundingBox2D box)
        {
            List<Point2D> strip = ClipPolygonToVerticalStrip(polygon, box.XMin, box.XMax);
            if (strip.Count < 3)
                return new List<Point2D>();

            List<Point2D> stage3 = ClipAgainstHalfPlane(strip, 0.0, 1.0, box.YMin);
            List<Point2D> stage4 = ClipAgainstHalfPlane(stage3, 0.0, -1.0, -box.YMax);
            return RemoveNearDuplicateVertices(stage4, 1e-5);
        }

        /// <summary>
        /// Clips a 2D polygon to the slab { x : sMin <= x . (nx, ny) <= sMax }.
        /// </summary>
        public static List<Point2D> ClipPolygonToOrientedStrip(
            IList<Point2D> polygon,
            double nx,
            double ny,
            double sMin,
            double sMax)
        {
            if (polygon == null || polygon.Count < 3)
                return new List<Point2D>();

            List<Point2D> stage1 = ClipAgainstHalfPlane(polygon, nx, ny, sMin);
            List<Point2D> stage2 = ClipAgainstHalfPlane(stage1, -nx, -ny, -sMax);
            return RemoveNearDuplicateVertices(stage2, 1e-5);
        }

        private class PreparedHole2D
        {
            public Point2D Center;
            public double Radius; // Effective radius including InnerTangentClearanceMeters
            public bool IsCircular;
            public List<Point2D> Polygon;

            public void ProjectAlongNormal(double nx, double ny, out double sMin, out double sMax)
            {
                if (IsCircular || Polygon == null || Polygon.Count < 3)
                {
                    double cProj = Center.X * nx + Center.Y * ny;
                    sMin = cProj - Radius;
                    sMax = cProj + Radius;
                    return;
                }

                double minP = double.MaxValue;
                double maxP = double.MinValue;
                for (int i = 0; i < Polygon.Count; i++)
                {
                    double p = Polygon[i].X * nx + Polygon[i].Y * ny;
                    if (p < minP) minP = p;
                    if (p > maxP) maxP = p;
                }
                sMin = minP - InnerTangentClearanceMeters;
                sMax = maxP + InnerTangentClearanceMeters;
            }
        }

        private class ActiveBridgeRegion2D
        {
            public List<Point2D> Polygon;
            public List<PreparedHole2D> Holes;
            public List<double> ClippedAxesRad;
        }

        private class IntervalGroup2D
        {
            public double SMin;
            public double SMax;
            public List<PreparedHole2D> Holes = new List<PreparedHole2D>();
        }

        private class SplitCandidate2D
        {
            public double AngleRad;
            public int OrthoOk;
            public int GroupCount;
            public int SingleCount;
            public double MinGap;
            public double TotalGap;
            public double PrefDiff;
            public List<IntervalGroup2D> Groups;
            public List<List<Point2D>> ChildPolygons;
        }

        private class PlannerSearchState
        {
            public int NodesVisited;
            public const int MaxSearchNodes = 64;
        }

        /// <summary>
        /// Plans a sequence of N tiers of disjoint, non-self-intersecting 2D cut polygons
        /// so that every inner hole in the recess gets full tangent-line crisscross bridge support.
        /// Explicitly verifies bridge-capability (straight, two-end-supported chords avoiding holes)
        /// and performs recursive backtracking and starting-angle retry on complex or non-convex recesses.
        /// </summary>
        public static List<BridgeTierContours2D> ComputeMultiTierBridgePlan(
            bool isCircularOuter,
            Point2D outerCircleCenter,
            double outerCircleRadius,
            IList<Point2D> outerPolygon,
            IList<InnerHole2D> innerHoles,
            IList<BoundingBox2D> fallbackBoxes = null,
            double preferredAngleDeg = 0.0)
        {
            List<PreparedHole2D> preparedHoles = BuildPreparedHoles(innerHoles, fallbackBoxes);
            if (preparedHoles.Count == 0)
                return new List<BridgeTierContours2D>();

            List<Point2D> effectiveOuter = BuildEffectiveOuterPolygon(isCircularOuter, outerCircleCenter, outerCircleRadius, outerPolygon);

            // Single-hole fast path preserves exact 2-tier circular chord refinement when verified bridge-capable
            if (preparedHoles.Count == 1 && effectiveOuter.Count >= 3)
            {
                PreparedHole2D h = preparedHoles[0];
                BoundingBox2D rawBox = (fallbackBoxes != null && fallbackBoxes.Count > 0)
                    ? fallbackBoxes[0]
                    : new BoundingBox2D(
                        h.Center.X - Math.Max(1e-5, h.Radius - InnerTangentClearanceMeters),
                        h.Center.X + Math.Max(1e-5, h.Radius - InnerTangentClearanceMeters),
                        h.Center.Y - Math.Max(1e-5, h.Radius - InnerTangentClearanceMeters),
                        h.Center.Y + Math.Max(1e-5, h.Radius - InnerTangentClearanceMeters));

                TwoTierBridgeContours2D single = ComputeTwoTierBridgeContours(
                    isCircularOuter, outerCircleCenter, outerCircleRadius, outerPolygon, rawBox);

                List<BridgeTierContours2D> twoTiers = new List<BridgeTierContours2D>();
                if (single.Tier1Polygon != null && single.Tier1Polygon.Count >= 3)
                {
                    twoTiers.Add(new BridgeTierContours2D
                    {
                        TierIndex = 1,
                        PrimaryAngleDeg = preferredAngleDeg,
                        CutPolygons = new List<List<Point2D>> { single.Tier1Polygon }
                    });
                }
                if (single.Tier2Polygon != null && single.Tier2Polygon.Count >= 3)
                {
                    twoTiers.Add(new BridgeTierContours2D
                    {
                        TierIndex = 2,
                        PrimaryAngleDeg = preferredAngleDeg + 90.0,
                        CutPolygons = new List<List<Point2D>> { single.Tier2Polygon }
                    });
                }

                // Verify Tier 1 chord against effectiveOuter (catches concave outer notches) and all polygons
                bool fastPathBridgeOk = twoTiers.Count == 2 &&
                    IsSliceBridgeCapable(effectiveOuter, preparedHoles, 1.0, 0.0,
                        single.InnerBox.XMin, single.InnerBox.XMax, single.Tier1Polygon) &&
                    VerifyBridgePlan(effectiveOuter, innerHoles, fallbackBoxes, twoTiers);

                if (fastPathBridgeOk)
                    return twoTiers;
            }

            if (effectiveOuter.Count < 3)
                return new List<BridgeTierContours2D>();

            double basePreferredRad = NormalizeAxisAngleRad(0.0); // TopologyScanner already aligned (u2, u1) by BridgeAngleOffsetDeg
            List<BridgeTierContours2D> bestVerifiedPlan = null;

            double[] retryOffsetsDeg = new[] { 0.0, 15.0, -15.0, 30.0, -30.0, 45.0, 60.0, 75.0 };
            foreach (double offsetDeg in retryOffsetsDeg)
            {
                double trialPrefRad = NormalizeAxisAngleRad(basePreferredRad + offsetDeg * Math.PI / 180.0);
                List<ActiveBridgeRegion2D> initialRegions = new List<ActiveBridgeRegion2D>
                {
                    new ActiveBridgeRegion2D
                    {
                        Polygon = effectiveOuter,
                        Holes = preparedHoles,
                        ClippedAxesRad = new List<double>()
                    }
                };

                List<BridgeTierContours2D> candidatePlan = new List<BridgeTierContours2D>();
                PlannerSearchState search = new PlannerSearchState();

                bool solved = TrySolvePlanRecursive(
                    initialRegions,
                    tierIdx: 1,
                    preferredRad: trialPrefRad,
                    preferredAngleDeg: preferredAngleDeg,
                    search: search,
                    tiersOut: candidatePlan);

                if (solved && VerifyBridgePlan(effectiveOuter, innerHoles, fallbackBoxes, candidatePlan))
                {
                    if (bestVerifiedPlan == null || candidatePlan.Count < bestVerifiedPlan.Count)
                    {
                        bestVerifiedPlan = candidatePlan;
                    }
                    // Accept immediately if initial angle solved cleanly in <= 4 tiers
                    if (offsetDeg == 0.0 && bestVerifiedPlan.Count <= 4)
                        return bestVerifiedPlan;
                }
            }

            if (bestVerifiedPlan != null)
                return bestVerifiedPlan;

            // Explicit failure / abort: no verified bridge-capable plan exists across all backtracking branches and retries
            return new List<BridgeTierContours2D>();
        }

        private static bool TrySolvePlanRecursive(
            List<ActiveBridgeRegion2D> activeRegions,
            int tierIdx,
            double preferredRad,
            double preferredAngleDeg,
            PlannerSearchState search,
            List<BridgeTierContours2D> tiersOut)
        {
            if (activeRegions == null || activeRegions.Count == 0)
                return true;

            if (tierIdx > MaxBridgeTiers || search.NodesVisited >= PlannerSearchState.MaxSearchNodes)
                return false;

            search.NodesVisited++;

            List<ActiveBridgeRegion2D> singleRegions = new List<ActiveBridgeRegion2D>();
            List<ActiveBridgeRegion2D> multiRegions = new List<ActiveBridgeRegion2D>();
            foreach (ActiveBridgeRegion2D reg in activeRegions)
            {
                if (reg.Holes == null || reg.Holes.Count == 0) continue;
                if (reg.Holes.Count == 1) singleRegions.Add(reg);
                else multiRegions.Add(reg);
            }

            // Deterministic verified processing of single-hole regions at this tier
            List<List<Point2D>> singleCutPolys = new List<List<Point2D>>();
            List<ActiveBridgeRegion2D> singleNextActive = new List<ActiveBridgeRegion2D>();
            double? singleRepAngle = null;

            foreach (ActiveBridgeRegion2D sReg in singleRegions)
            {
                if (!TryProcessSingleHoleRegionVerified(sReg, preferredRad, singleCutPolys, singleNextActive, out double usedAng, out _))
                    return false;
                if (!singleRepAngle.HasValue) singleRepAngle = usedAng;
            }

            if (multiRegions.Count == 0)
            {
                if (singleCutPolys.Count == 0)
                    return true;

                tiersOut.Add(new BridgeTierContours2D
                {
                    TierIndex = tierIdx,
                    PrimaryAngleDeg = preferredAngleDeg + (singleRepAngle ?? preferredRad) * (180.0 / Math.PI),
                    CutPolygons = singleCutPolys
                });

                if (TrySolvePlanRecursive(singleNextActive, tierIdx + 1, preferredRad, preferredAngleDeg, search, tiersOut))
                    return true;

                tiersOut.RemoveAt(tiersOut.Count - 1);
                return false;
            }

            // Build top-K verified candidates for each multi-hole region at this tier
            List<List<SplitCandidate2D>> multiCandidateLists = new List<List<SplitCandidate2D>>(multiRegions.Count);
            foreach (ActiveBridgeRegion2D mReg in multiRegions)
            {
                List<SplitCandidate2D> cands = BuildVerifiedSplitCandidates(mReg, preferredRad, maxCandidates: 5);
                if (cands.Count == 0)
                {
                    SplitCandidate2D peel = FindVerifiedPairwiseTangentPeelSplit(mReg, preferredRad);
                    if (peel != null) cands.Add(peel);
                }
                if (cands.Count == 0)
                    return false;
                multiCandidateLists.Add(cands);
            }

            // Try top combinations (vary the primary multi-region's choice first, up to 5 branches)
            int primaryBranchLimit = Math.Min(5, multiCandidateLists[0].Count);
            for (int bIdx = 0; bIdx < primaryBranchLimit; bIdx++)
            {
                if (search.NodesVisited >= PlannerSearchState.MaxSearchNodes)
                    break;

                List<List<Point2D>> tierPolys = new List<List<Point2D>>(singleCutPolys);
                List<ActiveBridgeRegion2D> nextActive = new List<ActiveBridgeRegion2D>(singleNextActive);
                double repAngle = double.NaN;

                for (int mIdx = 0; mIdx < multiRegions.Count; mIdx++)
                {
                    SplitCandidate2D chosen = (mIdx == 0)
                        ? multiCandidateLists[0][bIdx]
                        : multiCandidateLists[mIdx][0];

                    if (double.IsNaN(repAngle))
                        repAngle = chosen.AngleRad;

                    for (int gIdx = 0; gIdx < chosen.Groups.Count; gIdx++)
                    {
                        List<Point2D> childPoly = chosen.ChildPolygons[gIdx];
                        IntervalGroup2D grp = chosen.Groups[gIdx];
                        tierPolys.Add(childPoly);

                        List<double> updatedAxes = new List<double>(multiRegions[mIdx].ClippedAxesRad) { chosen.AngleRad };
                        if (grp.Holes.Count == 1 && IsSingleHoleRegionComplete(childPoly, grp.Holes[0], updatedAxes))
                            continue;

                        nextActive.Add(new ActiveBridgeRegion2D
                        {
                            Polygon = childPoly,
                            Holes = grp.Holes,
                            ClippedAxesRad = updatedAxes
                        });
                    }
                }

                if (double.IsNaN(repAngle))
                    repAngle = singleRepAngle ?? preferredRad;

                tiersOut.Add(new BridgeTierContours2D
                {
                    TierIndex = tierIdx,
                    PrimaryAngleDeg = preferredAngleDeg + repAngle * (180.0 / Math.PI),
                    CutPolygons = tierPolys
                });

                if (TrySolvePlanRecursive(nextActive, tierIdx + 1, preferredRad, preferredAngleDeg, search, tiersOut))
                    return true;

                tiersOut.RemoveAt(tiersOut.Count - 1);
            }

            return false;
        }

        /// <summary>
        /// Explicitly verifies that an entire multi-tier bridge plan satisfies:
        /// 1. Every polygon is valid and non-self-intersecting.
        /// 2. Sibling polygons within every tier are mutually disjoint.
        /// 3. Tier k polygons strictly nest inside Tier k-1 (or effectiveOuter for Tier 1).
        /// 4. Every inner hole has a deepest enclosing terminal polygon containing only that hole,
        ///    with edges avoiding all hole interiors.
        /// </summary>
        public static bool VerifyBridgePlan(
            IList<Point2D> outerBoundary,
            IList<InnerHole2D> innerHoles,
            IList<BoundingBox2D> fallbackBoxes,
            IList<BridgeTierContours2D> tiers)
        {
            if (tiers == null || tiers.Count == 0)
                return false;

            List<PreparedHole2D> preparedHoles = BuildPreparedHoles(innerHoles, fallbackBoxes);
            if (preparedHoles.Count == 0)
                return false;

            // 1-3: Validate polygons, intra-tier disjointness, and tier-to-tier nesting
            for (int tIdx = 0; tIdx < tiers.Count; tIdx++)
            {
                IList<List<Point2D>> polys = tiers[tIdx].CutPolygons;
                if (polys == null || polys.Count == 0)
                    return false;

                for (int i = 0; i < polys.Count; i++)
                {
                    if (!IsValidCutPolygon(polys[i]))
                        return false;
                    if (!DoesPolygonBoundaryAvoidHoles(polys[i], preparedHoles))
                        return false;
                }

                for (int i = 0; i < polys.Count; i++)
                {
                    for (int j = i + 1; j < polys.Count; j++)
                    {
                        if (!ArePolygonsDisjoint(polys[i], polys[j]))
                            return false;
                    }
                }

                if (tIdx == 0 && outerBoundary != null && outerBoundary.Count >= 3)
                {
                    foreach (List<Point2D> p in polys)
                    {
                        Point2D interior = ComputeSampleInteriorPoint(p);
                        if (!IsPointInOrOnPolygon(interior, outerBoundary, 2e-5))
                            return false;
                    }
                }
                else if (tIdx > 0)
                {
                    IList<List<Point2D>> prevPolys = tiers[tIdx - 1].CutPolygons;
                    foreach (List<Point2D> p in polys)
                    {
                        Point2D interior = ComputeSampleInteriorPoint(p);
                        bool insideAnyParent = false;
                        foreach (List<Point2D> parent in prevPolys)
                        {
                            if (IsPointInOrOnPolygon(interior, parent, 2e-5))
                            {
                                insideAnyParent = true;
                                break;
                            }
                        }
                        if (!insideAnyParent)
                            return false;
                    }
                }
            }

            // 4: Verify every hole reaches an isolated final polygon without bundling two holes together
            foreach (PreparedHole2D hole in preparedHoles)
            {
                List<Point2D> deepestPoly = null;
                for (int tIdx = tiers.Count - 1; tIdx >= 0 && deepestPoly == null; tIdx--)
                {
                    foreach (List<Point2D> poly in tiers[tIdx].CutPolygons)
                    {
                        if (IsPointInOrOnPolygon(hole.Center, poly, 1e-6))
                        {
                            deepestPoly = poly;
                            break;
                        }
                    }
                }

                if (deepestPoly == null)
                    return false;

                foreach (PreparedHole2D other in preparedHoles)
                {
                    if (ReferenceEquals(other, hole)) continue;
                    if (IsPointInOrOnPolygon(other.Center, deepestPoly, 1e-6))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Verifies that slicing parentPoly at position s along (nx, ny) yields at most one continuous
        /// chord with both endpoints anchored on parentPoly's boundary and chord length >= MinBridgeWidthMeters.
        /// Rejects lines that split across concave notches in parentPoly.
        /// </summary>
        public static bool IsChordSingleSupportedSegment(
            IList<Point2D> parentPoly,
            double nx,
            double ny,
            double s,
            out Point2D segA,
            out Point2D segB)
        {
            segA = new Point2D(0, 0);
            segB = new Point2D(0, 0);
            if (parentPoly == null || parentPoly.Count < 3)
                return false;

            double tx = -ny;
            double ty = nx;
            List<double> tHits = new List<double>();
            int n = parentPoly.Count;

            for (int i = 0; i < n; i++)
            {
                Point2D a = parentPoly[i];
                Point2D b = parentPoly[(i + 1) % n];
                double da = (a.X * nx + a.Y * ny) - s;
                double db = (b.X * nx + b.Y * ny) - s;

                if (Math.Abs(da) <= 1e-8 && Math.Abs(db) <= 1e-8)
                {
                    tHits.Add(a.X * tx + a.Y * ty);
                    tHits.Add(b.X * tx + b.Y * ty);
                }
                else if ((da > 1e-8 && db < -1e-8) || (da < -1e-8 && db > 1e-8))
                {
                    double frac = da / (da - db);
                    double px = a.X + frac * (b.X - a.X);
                    double py = a.Y + frac * (b.Y - a.Y);
                    tHits.Add(px * tx + py * ty);
                }
                else if (Math.Abs(da) <= 1e-8)
                {
                    tHits.Add(a.X * tx + a.Y * ty);
                }
            }

            if (tHits.Count < 2)
                return false;

            tHits.Sort();
            List<double> uniqueT = new List<double> { tHits[0] };
            for (int i = 1; i < tHits.Count; i++)
            {
                if (tHits[i] - uniqueT[uniqueT.Count - 1] > 2e-5)
                    uniqueT.Add(tHits[i]);
            }

            if (uniqueT.Count < 2)
                return false;

            double tMin = uniqueT[0];
            double tMax = uniqueT[uniqueT.Count - 1];
            segA = new Point2D(s * nx + tMin * tx, s * ny + tMin * ty);
            segB = new Point2D(s * nx + tMax * tx, s * ny + tMax * ty);

            if (segA.DistanceTo(segB) < MinBridgeWidthMeters)
                return false;

            // Every sub-interval along [tMin, tMax] must stay inside parentPoly (no concave exterior gaps)
            for (int i = 0; i < uniqueT.Count - 1; i++)
            {
                double tMid = 0.5 * (uniqueT[i] + uniqueT[i + 1]);
                Point2D pMid = new Point2D(s * nx + tMid * tx, s * ny + tMid * ty);
                if (!IsPointInOrOnPolygon(pMid, parentPoly, 1e-5))
                    return false;
            }

            return true;
        }

        private static bool DoesSegmentAvoidAllHoles(
            Point2D a,
            Point2D b,
            IList<PreparedHole2D> holes,
            double clearanceTol = 1e-5)
        {
            if (holes == null || holes.Count == 0)
                return true;

            foreach (PreparedHole2D h in holes)
            {
                if (h.IsCircular || h.Polygon == null || h.Polygon.Count < 3)
                {
                    double d = DistancePointToSegment(h.Center, a, b);
                    if (d < h.Radius - clearanceTol)
                        return false;
                }
                else
                {
                    Point2D mid = new Point2D(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y));
                    if (IsPointInOrOnPolygon(mid, h.Polygon, -clearanceTol))
                        return false;
                    int hn = h.Polygon.Count;
                    for (int i = 0; i < hn; i++)
                    {
                        if (SegmentsIntersect(a, b, h.Polygon[i], h.Polygon[(i + 1) % hn]))
                            return false;
                    }
                }
            }

            return true;
        }

        private static bool DoesPolygonBoundaryAvoidHoles(
            IList<Point2D> poly,
            IList<PreparedHole2D> holes)
        {
            if (poly == null || poly.Count < 3 || holes == null)
                return true;

            // Actual physical CAD hole radius is h.Radius - InnerTangentClearanceMeters
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2D a = poly[i];
                Point2D b = poly[(i + 1) % n];
                foreach (PreparedHole2D h in holes)
                {
                    if (h.IsCircular || h.Polygon == null || h.Polygon.Count < 3)
                    {
                        double rawRadius = Math.Max(1e-6, h.Radius - InnerTangentClearanceMeters);
                        if (DistancePointToSegment(h.Center, a, b) < rawRadius - 1e-5)
                            return false;
                    }
                    else
                    {
                        int hn = h.Polygon.Count;
                        for (int j = 0; j < hn; j++)
                        {
                            if (SegmentsIntersect(a, b, h.Polygon[j], h.Polygon[(j + 1) % hn]))
                                return false;
                        }
                    }
                }
            }
            return true;
        }

        private static bool IsSliceBridgeCapable(
            IList<Point2D> parentPoly,
            IList<PreparedHole2D> parentHoles,
            double nx,
            double ny,
            double sMin,
            double sMax,
            IList<Point2D> childPoly)
        {
            if (!IsValidCutPolygon(childPoly))
                return false;

            double parentMin = double.MaxValue;
            double parentMax = double.MinValue;
            for (int i = 0; i < parentPoly.Count; i++)
            {
                double p = parentPoly[i].X * nx + parentPoly[i].Y * ny;
                if (p < parentMin) parentMin = p;
                if (p > parentMax) parentMax = p;
            }

            if (sMin > parentMin + 1e-5 && sMin < parentMax - 1e-5)
            {
                if (!IsChordSingleSupportedSegment(parentPoly, nx, ny, sMin, out Point2D segA, out Point2D segB))
                    return false;
                if (!DoesSegmentAvoidAllHoles(segA, segB, parentHoles))
                    return false;
            }

            if (sMax > parentMin + 1e-5 && sMax < parentMax - 1e-5)
            {
                if (!IsChordSingleSupportedSegment(parentPoly, nx, ny, sMax, out Point2D segA, out Point2D segB))
                    return false;
                if (!DoesSegmentAvoidAllHoles(segA, segB, parentHoles))
                    return false;
            }

            return true;
        }

        private static bool ArePolygonsDisjoint(IList<Point2D> a, IList<Point2D> b)
        {
            int na = a.Count;
            int nb = b.Count;
            for (int i = 0; i < na; i++)
            {
                Point2D a1 = a[i];
                Point2D a2 = a[(i + 1) % na];
                for (int j = 0; j < nb; j++)
                {
                    if (SegmentsIntersect(a1, a2, b[j], b[(j + 1) % nb]))
                        return false;
                }
            }

            Point2D pA = ComputeSampleInteriorPoint(a);
            Point2D pB = ComputeSampleInteriorPoint(b);
            if (IsPointInOrOnPolygon(pA, b, -2e-5) || IsPointInOrOnPolygon(pB, a, -2e-5))
                return false;

            return true;
        }

        private static Point2D ComputeSampleInteriorPoint(IList<Point2D> poly)
        {
            if (poly == null || poly.Count == 0)
                return new Point2D(0, 0);

            double cx = 0.0, cy = 0.0;
            for (int i = 0; i < poly.Count; i++)
            {
                cx += poly[i].X;
                cy += poly[i].Y;
            }
            Point2D centroid = new Point2D(cx / poly.Count, cy / poly.Count);
            if (IsPointInOrOnPolygon(centroid, poly, -1e-6))
                return centroid;

            // Ear fallback if centroid falls outside a concave polygon
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Point2D pPrev = poly[(i - 1 + n) % n];
                Point2D pCurr = poly[i];
                Point2D pNext = poly[(i + 1) % n];
                Point2D mid = new Point2D((pPrev.X + pCurr.X + pNext.X) / 3.0, (pPrev.Y + pCurr.Y + pNext.Y) / 3.0);
                if (IsPointInOrOnPolygon(mid, poly, -1e-6))
                    return mid;
            }

            return centroid;
        }

        private static bool IsPointInOrOnPolygon(Point2D pt, IList<Point2D> poly, double boundaryTol = 1e-5)
        {
            if (poly == null || poly.Count < 3)
                return false;

            int n = poly.Count;
            double absTol = Math.Abs(boundaryTol);
            for (int i = 0; i < n; i++)
            {
                if (DistancePointToSegment(pt, poly[i], poly[(i + 1) % n]) <= absTol)
                    return boundaryTol >= 0.0;
            }

            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Point2D pi = poly[i];
                Point2D pj = poly[j];
                if (((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                    (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-18) + pi.X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistancePointToSegment(Point2D p, Point2D a, Point2D b)
        {
            double abx = b.X - a.X;
            double aby = b.Y - a.Y;
            double lenSq = abx * abx + aby * aby;
            if (lenSq < 1e-18)
                return p.DistanceTo(a);

            double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;

            double dx = p.X - (a.X + t * abx);
            double dy = p.Y - (a.Y + t * aby);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool TryProcessSingleHoleRegionVerified(
            ActiveBridgeRegion2D region,
            double preferredRad,
            List<List<Point2D>> tierCuts,
            List<ActiveBridgeRegion2D> nextActive,
            out double usedAngleRad,
            out bool didEmit)
        {
            usedAngleRad = preferredRad;
            didEmit = false;

            PreparedHole2D hole = region.Holes[0];
            if (IsSingleHoleRegionComplete(region.Polygon, hole, region.ClippedAxesRad))
                return true;

            List<double> tightAxes = GetTightAxesForHole(region.Polygon, hole, region.ClippedAxesRad);
            List<double> trialAngles = new List<double>();
            if (tightAxes.Count > 0)
                trialAngles.Add(NormalizeAxisAngleRad(tightAxes[0] + Math.PI * 0.5));
            trialAngles.Add(preferredRad);
            trialAngles.Add(NormalizeAxisAngleRad(preferredRad + Math.PI * 0.5));
            for (int deg = 0; deg < 180; deg += 15)
                trialAngles.Add(deg * Math.PI / 180.0);

            foreach (double ang in trialAngles)
            {
                double nx = Math.Cos(ang);
                double ny = Math.Sin(ang);
                hole.ProjectAlongNormal(nx, ny, out double hMin, out double hMax);
                List<Point2D> clipped = ClipPolygonToOrientedStrip(region.Polygon, nx, ny, hMin, hMax);
                if (!IsSliceBridgeCapable(region.Polygon, region.Holes, nx, ny, hMin, hMax, clipped))
                    continue;

                tierCuts.Add(clipped);
                usedAngleRad = ang;
                didEmit = true;

                List<double> newAxes = new List<double>(region.ClippedAxesRad) { ang };
                if (!IsSingleHoleRegionComplete(clipped, hole, newAxes))
                {
                    nextActive.Add(new ActiveBridgeRegion2D
                    {
                        Polygon = clipped,
                        Holes = new List<PreparedHole2D> { hole },
                        ClippedAxesRad = newAxes
                    });
                }
                return true;
            }

            return false;
        }

        private static List<SplitCandidate2D> BuildVerifiedSplitCandidates(
            ActiveBridgeRegion2D region,
            double preferredRad,
            int maxCandidates)
        {
            List<double> candidates = GenerateCandidateNormalAngles(region, preferredRad);
            double[] gapThresholds = new[] { MinBridgeWidthMeters, 0.00012, 0.00004, 0.000008 };
            bool hasPrevAxis = region.ClippedAxesRad.Count > 0;
            double lastPrevAxis = hasPrevAxis ? region.ClippedAxesRad[region.ClippedAxesRad.Count - 1] : 0.0;

            foreach (double minGapThresh in gapThresholds)
            {
                List<SplitCandidate2D> scored = new List<SplitCandidate2D>();
                foreach (double ang in candidates)
                {
                    List<IntervalGroup2D> groups = MergeProjectedHoleIntervals(
                        region.Holes, ang, minGapThresh, out double minGap, out double totalGap);
                    if (groups.Count < 2)
                        continue;

                    double nx = Math.Cos(ang);
                    double ny = Math.Sin(ang);

                    // Verify every cut slice AND every separating corridor midpoint cleanly spans parent polygon
                    bool allValid = true;
                    List<List<Point2D>> childPolys = new List<List<Point2D>>(groups.Count);

                    for (int gIdx = 0; gIdx < groups.Count; gIdx++)
                    {
                        IntervalGroup2D grp = groups[gIdx];
                        List<Point2D> slice = ClipPolygonToOrientedStrip(region.Polygon, nx, ny, grp.SMin, grp.SMax);
                        if (!IsSliceBridgeCapable(region.Polygon, region.Holes, nx, ny, grp.SMin, grp.SMax, slice))
                        {
                            allValid = false;
                            break;
                        }
                        childPolys.Add(slice);

                        if (gIdx < groups.Count - 1)
                        {
                            double bridgeMidS = 0.5 * (grp.SMax + groups[gIdx + 1].SMin);
                            if (!IsChordSingleSupportedSegment(region.Polygon, nx, ny, bridgeMidS, out Point2D mA, out Point2D mB) ||
                                !DoesSegmentAvoidAllHoles(mA, mB, region.Holes))
                            {
                                allValid = false;
                                break;
                            }
                        }
                    }

                    if (!allValid)
                        continue;

                    int singleCount = 0;
                    foreach (IntervalGroup2D g in groups)
                        if (g.Holes.Count == 1) singleCount++;

                    scored.Add(new SplitCandidate2D
                    {
                        AngleRad = ang,
                        OrthoOk = (!hasPrevAxis || Math.Abs(Math.Sin(ang - lastPrevAxis)) >= 0.5 - 1e-6) ? 1 : 0,
                        GroupCount = groups.Count,
                        SingleCount = singleCount,
                        MinGap = minGap,
                        TotalGap = totalGap,
                        PrefDiff = Math.Min(
                            AxisAngleDiffRad(ang, preferredRad),
                            AxisAngleDiffRad(ang, NormalizeAxisAngleRad(preferredRad + Math.PI * 0.5))),
                        Groups = groups,
                        ChildPolygons = childPolys
                    });
                }

                if (scored.Count > 0)
                {
                    scored.Sort((a, b) =>
                    {
                        int c = b.OrthoOk.CompareTo(a.OrthoOk);
                        if (c != 0) return c;
                        c = b.GroupCount.CompareTo(a.GroupCount);
                        if (c != 0) return c;
                        c = b.SingleCount.CompareTo(a.SingleCount);
                        if (c != 0) return c;
                        if (Math.Abs(b.MinGap - a.MinGap) > 5e-5) return b.MinGap.CompareTo(a.MinGap);
                        if (Math.Abs(b.TotalGap - a.TotalGap) > 5e-5) return b.TotalGap.CompareTo(a.TotalGap);
                        return a.PrefDiff.CompareTo(b.PrefDiff);
                    });

                    if (scored.Count > maxCandidates)
                        scored.RemoveRange(maxCandidates, scored.Count - maxCandidates);
                    return scored;
                }
            }

            return new List<SplitCandidate2D>();
        }

        private static SplitCandidate2D FindVerifiedPairwiseTangentPeelSplit(
            ActiveBridgeRegion2D region,
            double preferredRad)
        {
            if (region.Holes == null || region.Holes.Count < 2)
                return null;

            double cx = 0.0, cy = 0.0;
            foreach (PreparedHole2D h in region.Holes)
            {
                cx += h.Center.X;
                cy += h.Center.Y;
            }
            cx /= region.Holes.Count;
            cy /= region.Holes.Count;

            List<PreparedHole2D> ordered = new List<PreparedHole2D>(region.Holes);
            ordered.Sort((a, b) =>
            {
                double da = (a.Center.X - cx) * (a.Center.X - cx) + (a.Center.Y - cy) * (a.Center.Y - cy);
                double db = (b.Center.X - cx) * (b.Center.X - cx) + (b.Center.Y - cy) * (b.Center.Y - cy);
                return db.CompareTo(da);
            });

            List<double> angles = GenerateCandidateNormalAngles(region, preferredRad);
            foreach (PreparedHole2D target in ordered)
            {
                foreach (double ang in angles)
                {
                    List<IntervalGroup2D> groups = MergeProjectedHoleIntervals(region.Holes, ang, 1e-6, out double minGap, out double totalGap);
                    if (groups.Count < 2) continue;

                    double nx = Math.Cos(ang);
                    double ny = Math.Sin(ang);
                    bool allValid = true;
                    List<List<Point2D>> polys = new List<List<Point2D>>();
                    foreach (IntervalGroup2D grp in groups)
                    {
                        List<Point2D> slice = ClipPolygonToOrientedStrip(region.Polygon, nx, ny, grp.SMin, grp.SMax);
                        if (!IsSliceBridgeCapable(region.Polygon, region.Holes, nx, ny, grp.SMin, grp.SMax, slice))
                        {
                            allValid = false;
                            break;
                        }
                        polys.Add(slice);
                    }
                    if (!allValid) continue;

                    return new SplitCandidate2D
                    {
                        AngleRad = ang,
                        OrthoOk = 1,
                        GroupCount = groups.Count,
                        SingleCount = 1,
                        MinGap = minGap,
                        TotalGap = totalGap,
                        PrefDiff = 0.0,
                        Groups = groups,
                        ChildPolygons = polys
                    };
                }
            }

            return null;
        }

        private static List<PreparedHole2D> BuildPreparedHoles(
            IList<InnerHole2D> innerHoles,
            IList<BoundingBox2D> fallbackBoxes)
        {
            List<PreparedHole2D> list = new List<PreparedHole2D>();

            if (innerHoles != null && innerHoles.Count > 0)
            {
                foreach (InnerHole2D h in innerHoles)
                {
                    if (h == null) continue;
                    if (h.IsCircular && h.RadiusMeters > 1e-6)
                    {
                        list.Add(new PreparedHole2D
                        {
                            Center = h.Center,
                            Radius = h.RadiusMeters + InnerTangentClearanceMeters,
                            IsCircular = true,
                            Polygon = h.Polygon2D
                        });
                    }
                    else if (h.Polygon2D != null && h.Polygon2D.Count >= 3)
                    {
                        list.Add(new PreparedHole2D
                        {
                            Center = h.Center,
                            Radius = Math.Max(1e-5, h.RadiusMeters) + InnerTangentClearanceMeters,
                            IsCircular = false,
                            Polygon = new List<Point2D>(h.Polygon2D)
                        });
                    }
                }
            }
            else if (fallbackBoxes != null && fallbackBoxes.Count > 0)
            {
                foreach (BoundingBox2D b in fallbackBoxes)
                {
                    List<Point2D> rect = MakeRectanglePolygon(b.XMin, b.XMax, b.YMin, b.YMax);
                    double rad = 0.5 * Math.Sqrt(b.Width * b.Width + b.Height * b.Height);
                    list.Add(new PreparedHole2D
                    {
                        Center = b.Center,
                        Radius = rad + InnerTangentClearanceMeters,
                        IsCircular = false,
                        Polygon = rect
                    });
                }
            }

            return list;
        }

        private static List<Point2D> BuildEffectiveOuterPolygon(
            bool isCircularOuter,
            Point2D outerCircleCenter,
            double outerCircleRadius,
            IList<Point2D> outerPolygon)
        {
            double effectiveOuterRadius = Math.Max(1e-5, outerCircleRadius - OuterSliverInsetMeters);
            if (isCircularOuter && outerCircleRadius > 1e-6)
            {
                return SampleCirclePolygon(outerCircleCenter, effectiveOuterRadius, 72);
            }
            if (outerPolygon != null && outerPolygon.Count >= 3)
            {
                return InsetPolygonTowardCentroid(outerPolygon, OuterSliverInsetMeters);
            }
            if (outerCircleRadius > 1e-6)
            {
                return SampleCirclePolygon(outerCircleCenter, effectiveOuterRadius, 72);
            }
            return new List<Point2D>();
        }

        private static List<double> GenerateCandidateNormalAngles(ActiveBridgeRegion2D region, double preferredRad)
        {
            List<double> angles = new List<double>();
            Action<double> addUnique = (rad) =>
            {
                double norm = NormalizeAxisAngleRad(rad);
                for (int i = 0; i < angles.Count; i++)
                {
                    if (AxisAngleDiffRad(angles[i], norm) < 0.001)
                        return;
                }
                angles.Add(norm);
            };

            addUnique(preferredRad);
            addUnique(preferredRad + Math.PI * 0.5);

            foreach (double prev in region.ClippedAxesRad)
            {
                addUnique(prev + Math.PI * 0.5);
                addUnique(prev);
            }

            // Pairwise tangent normals (separating corridor, parallel centerline, external & internal bitangents)
            IList<PreparedHole2D> holes = region.Holes;
            for (int i = 0; i < holes.Count; i++)
            {
                for (int j = i + 1; j < holes.Count; j++)
                {
                    double dx = holes[j].Center.X - holes[i].Center.X;
                    double dy = holes[j].Center.Y - holes[i].Center.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 1e-6) continue;

                    double phi = Math.Atan2(dy, dx);
                    addUnique(phi);
                    addUnique(phi + Math.PI * 0.5);

                    double rDiffRatio = (holes[j].Radius - holes[i].Radius) / dist;
                    if (Math.Abs(rDiffRatio) < 0.999)
                    {
                        double extOffset = Math.Asin(rDiffRatio);
                        addUnique(phi + Math.PI * 0.5 + extOffset);
                        addUnique(phi + Math.PI * 0.5 - extOffset);
                    }

                    double rSumRatio = (holes[i].Radius + holes[j].Radius) / dist;
                    if (rSumRatio > 0.0 && rSumRatio < 0.999)
                    {
                        double intOffset = Math.Acos(rSumRatio);
                        addUnique(phi + intOffset);
                        addUnique(phi - intOffset);
                    }
                }
            }

            // 5-degree sweep fallback
            for (int deg = 0; deg < 180; deg += 5)
            {
                addUnique(deg * Math.PI / 180.0);
            }

            return angles;
        }

        private static List<IntervalGroup2D> MergeProjectedHoleIntervals(
            IList<PreparedHole2D> holes,
            double angleRad,
            double minGapMeters,
            out double minGapMetersOut,
            out double totalGapMetersOut)
        {
            double nx = Math.Cos(angleRad);
            double ny = Math.Sin(angleRad);

            List<IntervalGroup2D> raw = new List<IntervalGroup2D>(holes.Count);
            for (int i = 0; i < holes.Count; i++)
            {
                holes[i].ProjectAlongNormal(nx, ny, out double sMin, out double sMax);
                IntervalGroup2D grp = new IntervalGroup2D { SMin = sMin, SMax = sMax };
                grp.Holes.Add(holes[i]);
                raw.Add(grp);
            }

            raw.Sort((a, b) => a.SMin.CompareTo(b.SMin));

            List<IntervalGroup2D> merged = new List<IntervalGroup2D>(raw.Count);
            merged.Add(raw[0]);

            for (int i = 1; i < raw.Count; i++)
            {
                IntervalGroup2D cur = merged[merged.Count - 1];
                IntervalGroup2D nxt = raw[i];
                if (nxt.SMin - cur.SMax < minGapMeters - 1e-9)
                {
                    if (nxt.SMax > cur.SMax) cur.SMax = nxt.SMax;
                    cur.Holes.AddRange(nxt.Holes);
                }
                else
                {
                    merged.Add(nxt);
                }
            }

            totalGapMetersOut = 0.0;
            minGapMetersOut = merged.Count >= 2 ? double.MaxValue : 0.0;
            for (int i = 0; i < merged.Count - 1; i++)
            {
                double gap = merged[i + 1].SMin - merged[i].SMax;
                totalGapMetersOut += gap;
                if (gap < minGapMetersOut) minGapMetersOut = gap;
            }

            return merged;
        }

        private static bool IsTightlyBoundedAlongAxis(
            IList<Point2D> polygon,
            PreparedHole2D hole,
            double axisRad,
            double tolMeters = MinBridgeWidthMeters)
        {
            double nx = Math.Cos(axisRad);
            double ny = Math.Sin(axisRad);
            hole.ProjectAlongNormal(nx, ny, out double hMin, out double hMax);

            double qMin = double.MaxValue;
            double qMax = double.MinValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                double p = polygon[i].X * nx + polygon[i].Y * ny;
                if (p < qMin) qMin = p;
                if (p > qMax) qMax = p;
            }

            return (hMin - qMin) <= tolMeters + 1e-6 && (qMax - hMax) <= tolMeters + 1e-6;
        }

        private static List<double> GetTightAxesForHole(
            IList<Point2D> polygon,
            PreparedHole2D hole,
            IList<double> candidateAxes)
        {
            List<double> tight = new List<double>();
            if (candidateAxes == null) return tight;

            foreach (double ax in candidateAxes)
            {
                if (IsTightlyBoundedAlongAxis(polygon, hole, ax))
                {
                    tight.Add(ax);
                }
            }
            return tight;
        }

        private static bool IsSingleHoleRegionComplete(
            IList<Point2D> polygon,
            PreparedHole2D hole,
            IList<double> clippedAxes)
        {
            if (polygon == null || polygon.Count < 3 || hole == null)
                return false;

            List<double> tight = GetTightAxesForHole(polygon, hole, clippedAxes);
            if (tight.Count == 0)
                return false;

            foreach (double ax in tight)
            {
                double perp = NormalizeAxisAngleRad(ax + Math.PI * 0.5);
                if (IsTightlyBoundedAlongAxis(polygon, hole, perp))
                    return true;
            }

            if (tight.Count < 2)
                return false;

            // Check for two nearly orthogonal axes (>= 80 deg apart) OR small corner excess (<= 0.48*R + 0.25mm)
            bool hasOrthoPair = false;
            double minPairSep = 80.0 * Math.PI / 180.0 - 1e-4;
            for (int i = 0; i < tight.Count; i++)
            {
                for (int j = i + 1; j < tight.Count; j++)
                {
                    if (AxisAngleDiffRad(tight[i], tight[j]) >= minPairSep)
                    {
                        hasOrthoPair = true;
                        break;
                    }
                }
                if (hasOrthoPair) break;
            }

            if (hasOrthoPair)
                return true;

            double maxExcess = ComputeMaxVertexExcessBeyondHole(polygon, hole);
            double allowedExcess = 0.48 * hole.Radius + MinBridgeWidthMeters;
            return maxExcess <= allowedExcess;
        }

        private static double ComputeMaxVertexExcessBeyondHole(IList<Point2D> polygon, PreparedHole2D hole)
        {
            double maxExcess = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                double dx = polygon[i].X - hole.Center.X;
                double dy = polygon[i].Y - hole.Center.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-9) continue;

                double nx = dx / dist;
                double ny = dy / dist;
                hole.ProjectAlongNormal(nx, ny, out _, out double hMax);
                double vProj = polygon[i].X * nx + polygon[i].Y * ny;
                double excess = vProj - hMax;
                if (excess > maxExcess) maxExcess = excess;
            }
            return maxExcess;
        }

        private static bool IsValidCutPolygon(IList<Point2D> poly)
        {
            if (poly == null || poly.Count < 3)
                return false;
            if (!IsSimpleNonIntersectingPolygon(poly))
                return false;

            double twiceArea = 0.0;
            for (int i = 0; i < poly.Count; i++)
            {
                Point2D p1 = poly[i];
                Point2D p2 = poly[(i + 1) % poly.Count];
                twiceArea += (p1.X * p2.Y - p2.X * p1.Y);
            }
            return Math.Abs(twiceArea) * 0.5 > 1e-8;
        }

        private static double NormalizeAxisAngleRad(double rad)
        {
            double norm = rad % Math.PI;
            if (norm < 0.0) norm += Math.PI;
            if (Math.PI - norm < 1e-9) norm = 0.0;
            return norm;
        }

        private static double AxisAngleDiffRad(double a, double b)
        {
            double diff = Math.Abs(NormalizeAxisAngleRad(a) - NormalizeAxisAngleRad(b));
            if (diff > Math.PI * 0.5) diff = Math.PI - diff;
            return diff;
        }

        public static TwoTierBridgeContours2D ComputeTwoTierBridgeContours(
            bool isCircularOuter,
            Point2D outerCircleCenter,
            double outerCircleRadius,
            IList<Point2D> outerPolygon,
            BoundingBox2D innerHoleBox)
        {
            BoundingBox2D expandedInner = ExpandBox(innerHoleBox, InnerTangentClearanceMeters);
            double effectiveOuterRadius = Math.Max(1e-5, outerCircleRadius - OuterSliverInsetMeters);

            TwoTierBridgeContours2D result = new TwoTierBridgeContours2D
            {
                OuterCenter = outerCircleCenter,
                OuterRadius = effectiveOuterRadius,
                InnerBox = expandedInner
            };

            List<Point2D> effectiveOuterPoly;
            if (isCircularOuter && outerCircleRadius > 1e-6)
            {
                effectiveOuterPoly = SampleCirclePolygon(outerCircleCenter, effectiveOuterRadius, 72);
            }
            else if (outerPolygon != null && outerPolygon.Count >= 3)
            {
                effectiveOuterPoly = InsetPolygonTowardCentroid(outerPolygon, OuterSliverInsetMeters);
            }
            else
            {
                effectiveOuterPoly = SampleCirclePolygon(outerCircleCenter, effectiveOuterRadius, 72);
            }

            if (isCircularOuter && effectiveOuterRadius > 1e-6)
            {
                double dxRight = expandedInner.XMax - outerCircleCenter.X;
                double dxLeft = expandedInner.XMin - outerCircleCenter.X;

                if (Math.Abs(dxRight) < effectiveOuterRadius - 1e-6 &&
                    Math.Abs(dxLeft) < effectiveOuterRadius - 1e-6)
                {
                    double dyRight = Math.Sqrt(effectiveOuterRadius * effectiveOuterRadius - dxRight * dxRight);
                    double dyLeft = Math.Sqrt(effectiveOuterRadius * effectiveOuterRadius - dxLeft * dxLeft);

                    result.UseAnalyticalCircleTier1 = true;
                    result.RightChordBottom = new Point2D(expandedInner.XMax, outerCircleCenter.Y - dyRight);
                    result.RightChordTop = new Point2D(expandedInner.XMax, outerCircleCenter.Y + dyRight);
                    result.TopArcMid = new Point2D(outerCircleCenter.X, outerCircleCenter.Y + effectiveOuterRadius);
                    result.LeftChordTop = new Point2D(expandedInner.XMin, outerCircleCenter.Y + dyLeft);
                    result.LeftChordBottom = new Point2D(expandedInner.XMin, outerCircleCenter.Y - dyLeft);
                    result.BottomArcMid = new Point2D(outerCircleCenter.X, outerCircleCenter.Y - effectiveOuterRadius);

                    result.Tier1Polygon = BuildSimpleCircularSlotPolygon(
                        outerCircleCenter, effectiveOuterRadius,
                        expandedInner.XMin, expandedInner.XMax,
                        dyLeft, dyRight, arcSegments: 10);
                }
            }

            if (result.Tier1Polygon == null || result.Tier1Polygon.Count < 3)
            {
                List<Point2D> clipped1 = ClipPolygonToVerticalStrip(effectiveOuterPoly, expandedInner.XMin, expandedInner.XMax);
                if (IsSimpleNonIntersectingPolygon(clipped1))
                {
                    result.Tier1Polygon = clipped1;
                }
                else
                {
                    ComputeVerticalSpanInPolygon(effectiveOuterPoly, expandedInner, out double yBot, out double yTop);
                    result.Tier1Polygon = MakeRectanglePolygon(expandedInner.XMin, expandedInner.XMax, yBot, yTop);
                }
            }

            List<Point2D> rect2 = MakeRectanglePolygon(
                expandedInner.XMin, expandedInner.XMax,
                expandedInner.YMin, expandedInner.YMax);

            if (isCircularOuter && effectiveOuterRadius > 1e-6)
            {
                bool allCornersInside = true;
                foreach (Point2D pt in rect2)
                {
                    if (pt.DistanceTo(outerCircleCenter) >= effectiveOuterRadius - 1e-6)
                    {
                        allCornersInside = false;
                        break;
                    }
                }
                if (allCornersInside)
                {
                    result.Tier2Polygon = rect2;
                }
                else
                {
                    List<Point2D> clipped2 = ClipPolygonToBox(effectiveOuterPoly, expandedInner);
                    result.Tier2Polygon = IsSimpleNonIntersectingPolygon(clipped2) ? clipped2 : rect2;
                }
            }
            else
            {
                List<Point2D> clipped2 = ClipPolygonToBox(effectiveOuterPoly, expandedInner);
                result.Tier2Polygon = IsSimpleNonIntersectingPolygon(clipped2) ? clipped2 : rect2;
            }

            return result;
        }

        private static List<Point2D> BuildSimpleCircularSlotPolygon(
            Point2D center,
            double radius,
            double xMin,
            double xMax,
            double dyLeft,
            double dyRight,
            int arcSegments)
        {
            List<Point2D> poly = new List<Point2D>(2 * (arcSegments + 1));

            double angTopRight = Math.Atan2(dyRight, xMax - center.X);
            double angTopLeft = Math.Atan2(dyLeft, xMin - center.X);

            for (int i = 0; i <= arcSegments; i++)
            {
                double t = (double)i / arcSegments;
                double ang = angTopRight + t * (angTopLeft - angTopRight);
                poly.Add(new Point2D(center.X + radius * Math.Cos(ang), center.Y + radius * Math.Sin(ang)));
            }

            double angBotLeft = Math.Atan2(-dyLeft, xMin - center.X);
            double angBotRight = Math.Atan2(-dyRight, xMax - center.X);

            for (int i = 0; i <= arcSegments; i++)
            {
                double t = (double)i / arcSegments;
                double ang = angBotLeft + t * (angBotRight - angBotLeft);
                poly.Add(new Point2D(center.X + radius * Math.Cos(ang), center.Y + radius * Math.Sin(ang)));
            }

            return RemoveNearDuplicateVertices(poly);
        }

        private static List<Point2D> MakeRectanglePolygon(double xMin, double xMax, double yMin, double yMax)
        {
            return new List<Point2D>
            {
                new Point2D(xMax, yMin),
                new Point2D(xMax, yMax),
                new Point2D(xMin, yMax),
                new Point2D(xMin, yMin)
            };
        }

        private static void ComputeVerticalSpanInPolygon(
            IList<Point2D> outerPoly,
            BoundingBox2D innerBox,
            out double yBot,
            out double yTop)
        {
            BoundingBox2D outerBox = ComputeBoundingBox(outerPoly);
            double maxSpan = Math.Max(innerBox.Width, innerBox.Height) * 2.5;
            Point2D ic = innerBox.Center;

            yBot = Math.Max(outerBox.YMin + OuterSliverInsetMeters, ic.Y - maxSpan);
            yTop = Math.Min(outerBox.YMax - OuterSliverInsetMeters, ic.Y + maxSpan);

            if (yTop <= innerBox.YMax) yTop = innerBox.YMax + 0.001;
            if (yBot >= innerBox.YMin) yBot = innerBox.YMin - 0.001;
        }

        private static bool IsSimpleNonIntersectingPolygon(IList<Point2D> poly)
        {
            if (poly == null || poly.Count < 3)
                return false;

            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (poly[i].DistanceTo(poly[j]) < 1e-6)
                        return false;
                }
            }

            for (int i = 0; i < n; i++)
            {
                Point2D a1 = poly[i];
                Point2D a2 = poly[(i + 1) % n];

                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    Point2D b1 = poly[j];
                    Point2D b2 = poly[(j + 1) % n];
                    if (SegmentsIntersect(a1, a2, b1, b2))
                        return false;
                }
            }

            return true;
        }

        private static bool SegmentsIntersect(Point2D p1, Point2D p2, Point2D p3, Point2D p4)
        {
            double d1 = CrossProductSign(p3, p4, p1);
            double d2 = CrossProductSign(p3, p4, p2);
            double d3 = CrossProductSign(p1, p2, p3);
            double d4 = CrossProductSign(p1, p2, p4);

            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
                   ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        private static double CrossProductSign(Point2D a, Point2D b, Point2D c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        public static List<Point2D> SampleCirclePolygon(Point2D center, double radius, int segments = 64)
        {
            List<Point2D> pts = new List<Point2D>(segments);
            for (int i = 0; i < segments; i++)
            {
                double angle = (2.0 * Math.PI * i) / segments;
                pts.Add(new Point2D(
                    center.X + radius * Math.Cos(angle),
                    center.Y + radius * Math.Sin(angle)));
            }
            return pts;
        }

        private static List<Point2D> ClipAgainstHalfPlane(IList<Point2D> input, double nx, double ny, double c)
        {
            List<Point2D> output = new List<Point2D>();
            if (input == null || input.Count == 0)
                return output;

            int count = input.Count;
            Point2D prev = input[count - 1];
            double prevDist = (nx * prev.X + ny * prev.Y) - c;
            bool prevInside = prevDist >= -1e-10;

            for (int i = 0; i < count; i++)
            {
                Point2D curr = input[i];
                double currDist = (nx * curr.X + ny * curr.Y) - c;
                bool currInside = currDist >= -1e-10;

                if (currInside)
                {
                    if (!prevInside)
                    {
                        output.Add(IntersectEdge(prev, curr, prevDist, currDist));
                    }
                    output.Add(curr);
                }
                else if (prevInside)
                {
                    output.Add(IntersectEdge(prev, curr, prevDist, currDist));
                }

                prev = curr;
                prevDist = currDist;
                prevInside = currInside;
            }

            return output;
        }

        private static Point2D IntersectEdge(Point2D p1, Point2D p2, double d1, double d2)
        {
            double denom = d1 - d2;
            if (Math.Abs(denom) < 1e-14)
                return p1;
            double t = d1 / denom;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return new Point2D(
                p1.X + t * (p2.X - p1.X),
                p1.Y + t * (p2.Y - p1.Y));
        }

        private static List<Point2D> RemoveNearDuplicateVertices(List<Point2D> pts, double eps = 1e-6)
        {
            if (pts == null || pts.Count == 0)
                return new List<Point2D>();

            List<Point2D> cleaned = new List<Point2D>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                Point2D curr = pts[i];
                if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].DistanceTo(curr) > eps)
                {
                    cleaned.Add(curr);
                }
            }

            if (cleaned.Count > 1 && cleaned[0].DistanceTo(cleaned[cleaned.Count - 1]) <= eps)
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }

            return cleaned;
        }
    }
}
