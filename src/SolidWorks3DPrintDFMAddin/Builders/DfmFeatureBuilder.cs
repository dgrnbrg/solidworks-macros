using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Core;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    public static class DfmFeatureBuilder
    {
        [ThreadStatic]
        internal static bool SuppressUserDialogs;

        public static List<IFeature> ApplyTeardropFeatures(
            ISldWorks swApp,
            IModelDoc2 model,
            DfmConfig config,
            IList<DetectedHoleFaceGroup> liveGroups = null)
        {
            List<IFeature> createdFeatures = new List<IFeature>();
            if (swApp == null || model == null || config == null)
                return createdFeatures;

            PartEventCleanUpHandler.SuppressCleanup = true;
            try
            {
                DeleteManagedTeardropFeatures(model, config);

                if (config.TeardropTargets != null)
                {
                    for (int i = 0; i < config.TeardropTargets.Count; i++)
                    {
                        TeardropHoleTarget hole = config.TeardropTargets[i];
                        IList<IFace2> liveFaces = (liveGroups != null && i < liveGroups.Count) ? liveGroups[i].Faces : null;

                        if (CreateTeardropCutForHole(swApp, model, config, hole, liveFaces, i + 1,
                            out IFeature cutFeat, out IFeature refPlaneFeat))
                        {
                            if (refPlaneFeat != null) createdFeatures.Add(refPlaneFeat);
                            if (cutFeat != null) createdFeatures.Add(cutFeat);
                        }
                    }
                }

                model.ClearSelection2(true);
                model.EditRebuild3();
            }
            catch (Exception ex)
            {
                SwAddin.Log("ApplyTeardropFeatures error: " + ex);
            }
            finally
            {
                PartEventCleanUpHandler.SuppressCleanup = false;
            }

            return createdFeatures;
        }

        public static List<IFeature> ApplyCrisscrossBridgeFeatures(
            ISldWorks swApp,
            IModelDoc2 model,
            DfmConfig config,
            IList<DetectedRecessFace> liveRecessFaces = null)
        {
            List<IFeature> createdFeatures = new List<IFeature>();
            if (swApp == null || model == null || config == null)
                return createdFeatures;

            PartEventCleanUpHandler.SuppressCleanup = true;
            try
            {
                DeleteManagedBridgeFeatures(model, config);

                if (config.BridgeTargets != null)
                {
                    for (int i = 0; i < config.BridgeTargets.Count; i++)
                    {
                        CrisscrossBridgeTarget bridge = config.BridgeTargets[i];
                        IFace2 liveFace = (liveRecessFaces != null && i < liveRecessFaces.Count) ? liveRecessFaces[i].Face : null;

                        List<IFeature> recessCuts = new List<IFeature>();
                        if (CreateCrisscrossBridgeCutsForRecess(swApp, model, config, bridge, liveFace, i + 1,
                            out IFeature refPlaneFeat, recessCuts))
                        {
                            if (refPlaneFeat != null) createdFeatures.Add(refPlaneFeat);
                            createdFeatures.AddRange(recessCuts);
                        }
                    }
                }

                model.ClearSelection2(true);
                model.EditRebuild3();
            }
            catch (Exception ex)
            {
                SwAddin.Log("ApplyCrisscrossBridgeFeatures error: " + ex);
            }
            finally
            {
                PartEventCleanUpHandler.SuppressCleanup = false;
            }

            return createdFeatures;
        }

        private static bool CreateTeardropCutForHole(
            ISldWorks swApp,
            IModelDoc2 model,
            DfmConfig config,
            TeardropHoleTarget hole,
            IList<IFace2> liveHoleFaces,
            int index,
            out IFeature createdCutFeat,
            out IFeature createdRefPlaneFeat)
        {
            createdCutFeat = null;
            createdRefPlaneFeat = null;

            if (!TeardropEngine.BuildHoleCrossSectionBasis(
                hole.AxisUnit,
                config.BedUpVector,
                out Vector3D uRight,
                out Vector3D vUp,
                out Vector3D wAxis))
            {
                return false;
            }

            TeardropProfile2D profile = TeardropEngine.ComputeProfile2D(
                hole.RadiusMeters,
                config.OverhangAngleFromBedDeg,
                hole.CircleAngleToBedDeg,
                config.EnableMaxHeightCap,
                config.MaxCapHeightMeters);

            double holeSpan = Math.Max(0.0001, hole.AxialMaxMeters - hole.AxialMinMeters);
            double wPlane = hole.AxialMinMeters;
            bool sketchOpened = false;

            ISketchManager sketchMgr = model.SketchManager;

            // Priority 1: Sketch directly on an adjacent perpendicular planar face
            IFace2 adjPlanarFace = TopologyScanner.FindAdjacentPerpendicularPlanarFace(
                liveHoleFaces, wAxis, out double wAdjPlane, hole.AxisOrigin);

            if (adjPlanarFace is IEntity planarEnt)
            {
                model.ClearSelection2(true);
                if (planarEnt.Select4(false, null))
                {
                    sketchMgr.InsertSketch(true);
                    if (sketchMgr.ActiveSketch != null)
                    {
                        wPlane = wAdjPlane;
                        sketchOpened = true;
                    }
                }
            }

            // Priority 2: Coincident Reference Plane on a circular edge of the hole
            if (!sketchOpened)
            {
                IEdge circEdge = TopologyScanner.FindCircularHoleEdge(
                    liveHoleFaces, hole.AxisOrigin, wAxis, out double wCircEdge);
                if (circEdge is IEntity edgeEnt)
                {
                    model.ClearSelection2(true);
                    if (edgeEnt.Select4(false, null))
                    {
                        createdRefPlaneFeat = (IFeature)model.FeatureManager.InsertRefPlane(
                            (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                            0, 0.0, 0, 0.0);
                        if (createdRefPlaneFeat != null)
                        {
                            try { createdRefPlaneFeat.Name = string.Format("TeardropPlane_{0}", index); } catch { }
                            hole.ManagedRefPlaneName = createdRefPlaneFeat.Name;
                            model.ClearSelection2(true);
                            createdRefPlaneFeat.Select2(false, 0);
                            sketchMgr.InsertSketch(true);
                            if (sketchMgr.ActiveSketch != null)
                            {
                                wPlane = wCircEdge;
                                sketchOpened = true;
                            }
                        }
                    }
                }
            }

            // Priority 3: Fallback perpendicular Reference Plane at hole midpoint
            if (!sketchOpened)
            {
                wPlane = 0.5 * (hole.AxialMinMeters + hole.AxialMaxMeters);
                Vector3D midCenter = hole.AxisOrigin + wAxis * wPlane;
                createdRefPlaneFeat = CreatePerpendicularPlaneAtPoint(swApp, model, midCenter, wAxis, string.Format("TeardropPlane_{0}", index));
                if (createdRefPlaneFeat != null)
                {
                    hole.ManagedRefPlaneName = createdRefPlaneFeat.Name;
                    model.ClearSelection2(true);
                    createdRefPlaneFeat.Select2(false, 0);
                    sketchMgr.InsertSketch(true);
                    sketchOpened = (sketchMgr.ActiveSketch != null);
                }
            }

            if (!sketchOpened || sketchMgr.ActiveSketch == null)
            {
                SwAddin.Log("CreateTeardropCutForHole: Failed to open 2D sketch for hole " + index);
                return false;
            }

            ISketch activeSketch = sketchMgr.ActiveSketch;
            Vector3D sketchPlaneOrigin3D = hole.AxisOrigin + wAxis * wPlane;

            IMathUtility mathUtil = (IMathUtility)swApp.GetMathUtility();
            IMathTransform modelToSketch = activeSketch.ModelToSketchTransform;

            sketchMgr.AddToDB = true;

            Point2D sLeft = MapPointToSketch2D(mathUtil, modelToSketch, TeardropEngine.MapTo3D(profile.TangentLeft, sketchPlaneOrigin3D, uRight, vUp));
            Point2D sRight = MapPointToSketch2D(mathUtil, modelToSketch, TeardropEngine.MapTo3D(profile.TangentRight, sketchPlaneOrigin3D, uRight, vUp));
            Point2D sArcMid = MapPointToSketch2D(mathUtil, modelToSketch, TeardropEngine.MapTo3D(profile.ArcMidpoint, sketchPlaneOrigin3D, uRight, vUp));

            sketchMgr.Create3PointArc(sLeft.X, sLeft.Y, 0, sRight.X, sRight.Y, 0, sArcMid.X, sArcMid.Y, 0);

            List<Point2D> roofChain = profile.GetRoofChain();
            for (int i = 0; i < roofChain.Count - 1; i++)
            {
                Point2D pA = MapPointToSketch2D(mathUtil, modelToSketch, TeardropEngine.MapTo3D(roofChain[i], sketchPlaneOrigin3D, uRight, vUp));
                Point2D pB = MapPointToSketch2D(mathUtil, modelToSketch, TeardropEngine.MapTo3D(roofChain[i + 1], sketchPlaneOrigin3D, uRight, vUp));
                sketchMgr.CreateLine(pA.X, pA.Y, 0, pB.X, pB.Y, 0);
            }

            sketchMgr.AddToDB = false;
            IFeature sketchFeat = (IFeature)activeSketch;
            sketchMgr.InsertSketch(true);

            if (sketchFeat != null)
            {
                try { sketchFeat.Name = string.Format("TeardropSketch_{0}", index); } catch { }
                hole.ManagedSketchName = sketchFeat.Name;
            }

            double distToMin = Math.Abs(wPlane - hole.AxialMinMeters);
            double distToMax = Math.Abs(hole.AxialMaxMeters - wPlane);

            if (distToMin < 1e-4 || distToMax < 1e-4)
            {
                createdCutFeat = ExecuteSingleDirectionCutIntoSolid(model, sketchFeat, holeSpan);
            }
            else
            {
                double d1 = Math.Max(0.0001, hole.AxialMaxMeters - wPlane);
                double d2 = Math.Max(0.0001, wPlane - hole.AxialMinMeters);
                model.ClearSelection2(true);
                sketchFeat.Select2(false, 0);
                createdCutFeat = model.FeatureManager.FeatureCut4(
                    false, false, false,
                    (int)swEndConditions_e.swEndCondBlind,
                    (int)swEndConditions_e.swEndCondBlind,
                    d1, d2,
                    false, false, false, false, 0.0, 0.0,
                    false, false, false, false, false,
                    false, false, false, false, false,
                    (int)swStartConditions_e.swStartSketchPlane,
                    0.0, false, false);

                if (createdCutFeat == null)
                {
                    createdCutFeat = ExecuteSingleDirectionCutIntoSolid(model, sketchFeat, holeSpan);
                }
            }

            if (createdRefPlaneFeat != null)
            {
                model.ClearSelection2(true);
                createdRefPlaneFeat.Select2(false, 0);
                model.BlankRefGeom();
            }

            if (createdCutFeat != null)
            {
                try { createdCutFeat.Name = string.Format("TeardropCut_{0}_D{1:F1}mm", index, hole.DiameterMm); } catch { }
                hole.ManagedCutFeatureName = createdCutFeat.Name;
                return true;
            }

            SwAddin.Log("CreateTeardropCutForHole: Cut failed for hole " + index);
            return false;
        }

        private class PendingBridgeTierSketch
        {
            public int TierIndex;
            public BridgeTierContours2D TierData;
            public IFeature SketchFeat;
            public string SketchName;
            public double DepthMeters;
        }

        private static bool CreateCrisscrossBridgeCutsForRecess(
            ISldWorks swApp,
            IModelDoc2 model,
            DfmConfig config,
            CrisscrossBridgeTarget bridge,
            IFace2 liveFace,
            int index,
            out IFeature recessPlaneFeat,
            List<IFeature> createdCuts)
        {
            recessPlaneFeat = null;

            bool hasHoles = (bridge.InnerHoles2D != null && bridge.InnerHoles2D.Count > 0) ||
                            (bridge.InnerHoleBoxes2D != null && bridge.InnerHoleBoxes2D.Count > 0);
            if (!hasHoles)
            {
                SwAddin.Log("CreateCrisscrossBridgeCutsForRecess: No inner holes for recess " + index);
                return false;
            }

            bridge.ManagedRefPlaneName = null;
            bridge.ManagedTier1CutName = null;
            bridge.ManagedTier1SketchName = null;
            bridge.ManagedTier2CutName = null;
            bridge.ManagedTier2SketchName = null;
            if (bridge.ManagedCutNames == null) bridge.ManagedCutNames = new List<string>();
            else bridge.ManagedCutNames.Clear();
            if (bridge.ManagedSketchNames == null) bridge.ManagedSketchNames = new List<string>();
            else bridge.ManagedSketchNames.Clear();

            Vector3D cutUp = -bridge.OutwardNormal.Normalized();
            Vector3D.BuildInPlaneBasis(cutUp, config.BridgeAngleOffsetDeg, out Vector3D u1, out Vector3D u2);

            List<BridgeTierContours2D> tiers = BridgeClippingEngine.ComputeMultiTierBridgePlan(
                bridge.IsCircularOuter,
                bridge.OuterCircleCenter2D,
                bridge.OuterCircleRadiusMeters,
                bridge.OuterLoopPolygon2D,
                bridge.InnerHoles2D,
                bridge.InnerHoleBoxes2D,
                config.BridgeAngleOffsetDeg);

            if (tiers == null || tiers.Count == 0)
            {
                string abortMsg = string.Format(
                    "ABORT: Backtracking failed to find a verified bridge-capable multi-tier plan for recess {0}. " +
                    "No partial bridge sketches or cuts were created.",
                    index);
                SwAddin.Log("CreateCrisscrossBridgeCutsForRecess: " + abortMsg);
                if (swApp != null && !SuppressUserDialogs)
                {
                    try
                    {
                        swApp.SendMsgToUser2(
                            abortMsg,
                            (int)swMessageBoxIcon_e.swMbWarning,
                            (int)swMessageBoxBtn_e.swMbOk);
                    }
                    catch { }
                }
                return false;
            }

            SwAddin.Log(string.Format("CreateCrisscrossBridgeCutsForRecess: Recess {0} planned {1} tier(s).", index, tiers.Count));

            IMathUtility mathUtil = (IMathUtility)swApp.GetMathUtility();
            ISketchManager sketchMgr = model.SketchManager;

            // Phase 1: Create all tier sketches directly on liveFace while it is 100% intact (no RefPlane needed).
            // Only create a fallback RefPlane if direct selection of the planar face fails.
            List<PendingBridgeTierSketch> pendingTiers = new List<PendingBridgeTierSketch>();
            foreach (BridgeTierContours2D tier in tiers)
            {
                if (tier.CutPolygons == null || tier.CutPolygons.Count == 0)
                    continue;

                if (!SelectRecessPlanarFaceOrFallbackPlane(swApp, model, ref recessPlaneFeat, liveFace, bridge, index))
                {
                    SwAddin.Log(string.Format("CreateCrisscrossBridgeCutsForRecess: Could not select face for Tier {0} on recess {1}", tier.TierIndex, index));
                    continue;
                }

                sketchMgr.InsertSketch(true);
                ISketch activeSketch = sketchMgr.ActiveSketch;
                if (activeSketch == null)
                    continue;

                IMathTransform xform = activeSketch.ModelToSketchTransform;
                sketchMgr.AddToDB = true;
                foreach (List<Point2D> poly in tier.CutPolygons)
                {
                    DrawClosedPolygonInSketch(sketchMgr, mathUtil, xform, bridge.PlaneOrigin, u2, u1, poly);
                }
                sketchMgr.AddToDB = false;

                IFeature sFeat = (IFeature)activeSketch;
                sketchMgr.InsertSketch(true);

                if (sFeat != null)
                {
                    try { sFeat.Name = string.Format("BridgeTier{0}Sketch_{1}", tier.TierIndex, index); } catch { }
                    bridge.ManagedSketchNames.Add(sFeat.Name);
                    if (tier.TierIndex == 1 && string.IsNullOrEmpty(bridge.ManagedTier1SketchName))
                        bridge.ManagedTier1SketchName = sFeat.Name;
                    else if (tier.TierIndex == 2 && string.IsNullOrEmpty(bridge.ManagedTier2SketchName))
                        bridge.ManagedTier2SketchName = sFeat.Name;

                    pendingTiers.Add(new PendingBridgeTierSketch
                    {
                        TierIndex = tier.TierIndex,
                        TierData = tier,
                        SketchFeat = sFeat,
                        SketchName = sFeat.Name,
                        DepthMeters = Math.Max(0.00005, tier.TierIndex * config.LayerHeightMeters)
                    });
                }
            }

            // Phase 2: Execute Cut-Extrude for each tier in ascending depth order (1x, 2x, 3x, ... LayerHeight).
            foreach (PendingBridgeTierSketch item in pendingTiers)
            {
                IFeature cut = ExecuteSingleDirectionCutIntoSolid(model, item.SketchFeat, item.DepthMeters);
                if (cut != null)
                {
                    try { cut.Name = string.Format("BridgeTier{0}_{1}_{2:F2}mm", item.TierIndex, index, item.DepthMeters * 1000.0); } catch { }
                    bridge.ManagedCutNames.Add(cut.Name);
                    if (item.TierIndex == 1 && string.IsNullOrEmpty(bridge.ManagedTier1CutName))
                        bridge.ManagedTier1CutName = cut.Name;
                    else if (item.TierIndex == 2 && string.IsNullOrEmpty(bridge.ManagedTier2CutName))
                        bridge.ManagedTier2CutName = cut.Name;
                    createdCuts.Add(cut);
                    continue;
                }

                // Multi-contour cut failed: clean up that multi-contour sketch immediately so no loose sketch remains,
                // then retry with isolated per-polygon sketches and cuts for this tier.
                SwAddin.Log(string.Format("CreateCrisscrossBridgeCutsForRecess: Tier {0} multi-contour cut returned null for recess {1}; falling back to per-polygon cuts.", item.TierIndex, index));
                DeleteFeatureByName(model, model.Extension, item.SketchName);
                bridge.ManagedSketchNames.Remove(item.SketchName);

                for (int pIdx = 0; pIdx < item.TierData.CutPolygons.Count; pIdx++)
                {
                    List<Point2D> poly = item.TierData.CutPolygons[pIdx];
                    if (poly == null || poly.Count < 3) continue;

                    if (!SelectRecessPlanarFaceOrFallbackPlane(swApp, model, ref recessPlaneFeat, liveFace, bridge, index))
                        continue;

                    sketchMgr.InsertSketch(true);
                    ISketch polySketch = sketchMgr.ActiveSketch;
                    if (polySketch == null)
                        continue;

                    IMathTransform pxform = polySketch.ModelToSketchTransform;
                    sketchMgr.AddToDB = true;
                    DrawClosedPolygonInSketch(sketchMgr, mathUtil, pxform, bridge.PlaneOrigin, u2, u1, poly);
                    sketchMgr.AddToDB = false;

                    IFeature pSketchFeat = (IFeature)polySketch;
                    sketchMgr.InsertSketch(true);

                    if (pSketchFeat == null)
                        continue;

                    string pSketchName = string.Format("BridgeTier{0}Sketch_{1}_{2}", item.TierIndex, index, pIdx + 1);
                    try { pSketchFeat.Name = pSketchName; } catch { }
                    pSketchName = pSketchFeat.Name;

                    IFeature polyCut = ExecuteSingleDirectionCutIntoSolid(model, pSketchFeat, item.DepthMeters);
                    if (polyCut != null)
                    {
                        try { polyCut.Name = string.Format("BridgeTier{0}_{1}_{2}_{3:F2}mm", item.TierIndex, index, pIdx + 1, item.DepthMeters * 1000.0); } catch { }
                        bridge.ManagedSketchNames.Add(pSketchName);
                        bridge.ManagedCutNames.Add(polyCut.Name);
                        createdCuts.Add(polyCut);
                    }
                    else
                    {
                        SwAddin.Log(string.Format("CreateCrisscrossBridgeCutsForRecess: Fallback cut returned null for {0}; removing empty sketch.", pSketchName));
                        DeleteFeatureByName(model, model.Extension, pSketchName);
                    }
                }
            }

            if (recessPlaneFeat != null)
            {
                model.ClearSelection2(true);
                recessPlaneFeat.Select2(false, 0);
                model.BlankRefGeom();
            }

            return createdCuts.Count > 0;
        }

        private static bool SelectRecessPlanarFaceOrFallbackPlane(
            ISldWorks swApp,
            IModelDoc2 model,
            ref IFeature recessPlaneFeat,
            IFace2 liveFace,
            CrisscrossBridgeTarget bridge,
            int index)
        {
            if (recessPlaneFeat == null && SelectRecessPlanarFace(model, liveFace, bridge))
                return true;

            if (recessPlaneFeat != null && SelectSketchPlane(model, recessPlaneFeat, liveFace, bridge))
                return true;

            if (recessPlaneFeat == null)
            {
                recessPlaneFeat = CreatePerpendicularPlaneAtPoint(
                    swApp,
                    model,
                    bridge.PlaneOrigin,
                    bridge.OutwardNormal,
                    string.Format("BridgePlane_{0}", index));
                if (recessPlaneFeat != null)
                {
                    bridge.ManagedRefPlaneName = recessPlaneFeat.Name;
                    model.ClearSelection2(true);
                    return recessPlaneFeat.Select2(false, 0);
                }
            }

            return false;
        }

        private static bool SelectSketchPlane(IModelDoc2 model, IFeature refPlaneFeat, IFace2 liveFace, CrisscrossBridgeTarget bridge)
        {
            model.ClearSelection2(true);
            if (refPlaneFeat != null && refPlaneFeat.Select2(false, 0))
                return true;
            return SelectRecessPlanarFace(model, liveFace, bridge);
        }

        private static bool SelectRecessPlanarFace(IModelDoc2 model, IFace2 liveFace, CrisscrossBridgeTarget bridge)
        {
            model.ClearSelection2(true);
            if (liveFace is IEntity ent && ent.Select4(false, null))
            {
                return true;
            }

            // Cast ray inward onto planar face (offset +2 mm along outward normal, pointing -OutwardNormal)
            Vector3D n = bridge.OutwardNormal.Normalized();
            Vector3D rayDir = -n;
            List<Vector3D> candidates = new List<Vector3D> { bridge.PlaneOrigin };

            if (bridge.OuterLoopPolygon2D != null && bridge.OuterLoopPolygon2D.Count > 0)
            {
                Vector3D cutUp = -n;
                Vector3D.BuildInPlaneBasis(cutUp, 0.0, out Vector3D u1, out Vector3D u2);
                Point2D b0 = bridge.OuterLoopPolygon2D[0];
                Point2D insetPt = new Point2D(b0.X * 0.96, b0.Y * 0.96);
                candidates.Add(bridge.PlaneOrigin + u2 * insetPt.X + u1 * insetPt.Y);
            }

            foreach (Vector3D pt in candidates)
            {
                Vector3D origin = pt + n * 0.002;
                if (model.Extension.SelectByRay(
                    origin.X, origin.Y, origin.Z,
                    rayDir.X, rayDir.Y, rayDir.Z,
                    0.002, (int)swSelectType_e.swSelFACES, false, 0, 0))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Executes a Cut-Extrude from sketchFeat into the solid body, trying both single-body
        /// and multi-body auto-select scopes and both extrusion directions so valid blind cuts
        /// never silently fail.
        /// </summary>
        private static IFeature ExecuteSingleDirectionCutIntoSolid(IModelDoc2 model, IFeature sketchFeat, double depth)
        {
            if (model == null || sketchFeat == null)
                return null;

            bool[] featScopeOpts = new[] { false, true };
            bool[] dirOpts = new[] { false, true };

            foreach (bool useScope in featScopeOpts)
            {
                foreach (bool dir in dirOpts)
                {
                    model.ClearSelection2(true);
                    sketchFeat.Select2(false, 0);
                    IFeature cut = model.FeatureManager.FeatureCut4(
                        true,  // Sd = true (Single direction)
                        false,
                        dir,
                        (int)swEndConditions_e.swEndCondBlind,
                        (int)swEndConditions_e.swEndCondBlind,
                        depth, 0.0,
                        false, false, false, false, 0.0, 0.0,
                        false, false, false, false, false,
                        useScope, // UseFeatScope
                        useScope, // UseAutoSelect
                        false, false, false,
                        (int)swStartConditions_e.swStartSketchPlane,
                        0.0, false, false);

                    if (cut != null)
                        return cut;
                }
            }

            // Final fallback: midplane/both-direction cut across depth
            model.ClearSelection2(true);
            sketchFeat.Select2(false, 0);
            return model.FeatureManager.FeatureCut4(
                false, // Sd = false
                false,
                false,
                (int)swEndConditions_e.swEndCondBlind,
                (int)swEndConditions_e.swEndCondBlind,
                depth, depth,
                false, false, false, false, 0.0, 0.0,
                false, false, false, false, false,
                false, false, false, false, false,
                (int)swStartConditions_e.swStartSketchPlane,
                0.0, false, false);
        }

        private static void DrawClosedPolygonInSketch(
            ISketchManager sketchMgr,
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            Vector3D planeOrigin,
            Vector3D xAxisU2,
            Vector3D yAxisU1,
            IList<Point2D> poly)
        {
            if (poly == null || poly.Count < 3)
                return;

            List<Point2D> mapped = new List<Point2D>(poly.Count);
            for (int i = 0; i < poly.Count; i++)
            {
                Point2D pt = MapBridge2DToSketch(mathUtil, modelToSketch, planeOrigin, xAxisU2, yAxisU1, poly[i]);
                if (mapped.Count == 0 || mapped[mapped.Count - 1].DistanceTo(pt) > 1e-5)
                {
                    mapped.Add(pt);
                }
            }

            if (mapped.Count > 1 && mapped[0].DistanceTo(mapped[mapped.Count - 1]) <= 1e-5)
            {
                mapped.RemoveAt(mapped.Count - 1);
            }

            if (mapped.Count < 3)
                return;

            for (int i = 0; i < mapped.Count; i++)
            {
                Point2D p1 = mapped[i];
                Point2D p2 = mapped[(i + 1) % mapped.Count];
                sketchMgr.CreateLine(p1.X, p1.Y, 0, p2.X, p2.Y, 0);
            }
        }

        private static Point2D MapBridge2DToSketch(
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            Vector3D planeOrigin,
            Vector3D xAxisU2,
            Vector3D yAxisU1,
            Point2D pt2d)
        {
            Vector3D pt3d = planeOrigin + xAxisU2 * pt2d.X + yAxisU1 * pt2d.Y;
            return MapPointToSketch2D(mathUtil, modelToSketch, pt3d);
        }

        private static Point2D MapPointToSketch2D(
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            Vector3D modelPt)
        {
            double[] coords = new[] { modelPt.X, modelPt.Y, modelPt.Z };
            IMathPoint mp = (IMathPoint)mathUtil.CreatePoint(coords);
            if (modelToSketch != null)
            {
                mp = (IMathPoint)mp.MultiplyTransform(modelToSketch);
            }
            double[] sCoords = (double[])mp.ArrayData;
            return new Point2D(sCoords[0], sCoords[1]);
        }

        private static IFeature CreatePerpendicularPlaneAtPoint(
            ISldWorks swApp,
            IModelDoc2 model,
            Vector3D origin3D,
            Vector3D normal3D,
            string planeName)
        {
            try
            {
                model.ClearSelection2(true);
                ISketchManager sketchMgr = model.SketchManager;
                sketchMgr.Insert3DSketch(true);
                IFeature guideSketchFeat = (IFeature)sketchMgr.ActiveSketch;
                sketchMgr.AddToDB = true;

                Vector3D endPt = origin3D + normal3D.Normalized() * 0.01;
                ISketchSegment lineSeg = sketchMgr.CreateLine(
                    origin3D.X, origin3D.Y, origin3D.Z,
                    endPt.X, endPt.Y, endPt.Z);
                ISketchLine skLine = (ISketchLine)lineSeg;
                ISketchPoint startPt = skLine != null ? (ISketchPoint)skLine.GetStartPoint2() : null;

                sketchMgr.AddToDB = false;
                sketchMgr.Insert3DSketch(true);

                model.ClearSelection2(true);
                if (lineSeg != null && startPt != null)
                {
                    lineSeg.Select4(false, null);
                    startPt.Select4(true, null);

                    IFeature refPlane = (IFeature)model.FeatureManager.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Perpendicular, 0.0,
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                        0, 0.0);

                    if (guideSketchFeat != null)
                    {
                        model.ClearSelection2(true);
                        guideSketchFeat.Select2(false, 0);
                        model.BlankSketch();
                    }

                    if (refPlane != null)
                    {
                        try { refPlane.Name = planeName; } catch { }
                        return refPlane;
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("CreatePerpendicularPlaneAtPoint error: " + ex);
            }

            return null;
        }

        public static void DeleteManagedTeardropFeatures(IModelDoc2 model, DfmConfig config)
        {
            if (model == null || config?.TeardropTargets == null) return;
            try
            {
                IModelDocExtension ext = model.Extension;
                foreach (TeardropHoleTarget t in config.TeardropTargets)
                {
                    DeleteFeatureByName(model, ext, t.ManagedCutFeatureName);
                    DeleteFeatureByName(model, ext, t.ManagedSketchName);
                    DeleteFeatureByName(model, ext, t.ManagedRefPlaneName);
                }
            }
            catch { }
        }

        public static void DeleteManagedBridgeFeatures(IModelDoc2 model, DfmConfig config)
        {
            if (model == null || config == null) return;
            try
            {
                IModelDocExtension ext = model.Extension;

                if (!string.IsNullOrEmpty(config.ManagedMacroFeatureName))
                {
                    DeleteFeatureByName(model, ext, config.ManagedMacroFeatureName + " Group");
                }

                if (config.BridgeTargets != null)
                {
                    foreach (CrisscrossBridgeTarget b in config.BridgeTargets)
                    {
                        if (b.ManagedCutNames != null)
                        {
                            for (int i = b.ManagedCutNames.Count - 1; i >= 0; i--)
                                DeleteFeatureByName(model, ext, b.ManagedCutNames[i]);
                        }
                        if (b.ManagedSketchNames != null)
                        {
                            for (int i = b.ManagedSketchNames.Count - 1; i >= 0; i--)
                                DeleteFeatureByName(model, ext, b.ManagedSketchNames[i]);
                        }

                        DeleteFeatureByName(model, ext, b.ManagedTier2CutName);
                        DeleteFeatureByName(model, ext, b.ManagedTier2SketchName);
                        DeleteFeatureByName(model, ext, b.ManagedTier1CutName);
                        DeleteFeatureByName(model, ext, b.ManagedTier1SketchName);
                        DeleteFeatureByName(model, ext, b.ManagedRefPlaneName);
                    }
                }
            }
            catch { }
        }

        private static void DeleteFeatureByName(IModelDoc2 model, IModelDocExtension ext, string featName)
        {
            if (string.IsNullOrEmpty(featName)) return;
            model.ClearSelection2(true);
            if (ext.SelectByID2(featName, "BODYFEATURE", 0, 0, 0, false, 0, null, 0) ||
                ext.SelectByID2(featName, "SKETCH", 0, 0, 0, false, 0, null, 0) ||
                ext.SelectByID2(featName, "PLANE", 0, 0, 0, false, 0, null, 0) ||
                ext.SelectByID2(featName, "FTRFOLDER", 0, 0, 0, false, 0, null, 0))
            {
                ext.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
            }
        }
    }
}
