using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksJoineryAddin.Data;
using SolidWorksJoineryAddin.MacroFeatures.Strategies;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    public static class GeometryBuilder
    {
        private static readonly Dictionary<JointType, IJointStrategy> _strategies = new Dictionary<JointType, IJointStrategy>
        {
            { JointType.TabAndSlot, new TabAndSlotStrategy() },
            { JointType.FingerJoint, new FingerJointStrategy() },
            { JointType.ThroughDado, new DadoStrategy() },
            { JointType.BlindDado, new DadoStrategy() },
            { JointType.Rabbet, new RabbetStrategy() }
        };

        public static IJointStrategy GetStrategy(JointType type)
        {
            if (_strategies.ContainsKey(type))
                return _strategies[type];
            return _strategies[JointType.TabAndSlot];
        }

        /// <summary>
        /// Calculates the exact 45-degree angle bisector center for a diagonal dogbone.
        /// Center C = V + (R / sqrt(2)) * (u1 + u2)
        /// </summary>
        public static Point2D CalculateDiagonalDogboneCenter(Point2D vertex, Point2D u1, Point2D u2, double radius)
        {
            double factor = radius / Math.Sqrt(2.0);
            return new Point2D(
                vertex.X + factor * (u1.X + u2.X),
                vertex.Y + factor * (u1.Y + u2.Y)
            );
        }

        /// <summary>
        /// Generates the 2D contour intervals for a given joint.
        /// </summary>
        public static void GenerateJointIntervals(
            JointDefinition joint,
            out List<Interval> tabIntervals,
            out List<Interval> slotIntervals)
        {
            var strategy = GetStrategy(joint.Type);
            strategy.ComputeIntervals(joint, out tabIntervals, out slotIntervals);
        }

        /// <summary>
        /// Computes a closed, watertight 2D slot contour for rectangle [s1, s2] x [t1, t2]
        /// with optional 45-degree diagonal dogbones or T-bone reliefs at all 4 internal corners.
        /// Handles adjacent corner overlap automatically when edge length < 2*R*sqrt(2).
        /// </summary>
        public static SlotContour2D ComputeSlotContourWithDogbones2D(
            double s1,
            double s2,
            double t1,
            double t2,
            double radius,
            bool enableDogbones,
            CornerReliefType reliefType)
        {
            SlotContour2D contour = new SlotContour2D();
            double width = s2 - s1;
            double height = t2 - t1;
            if (width <= 1e-7 || height <= 1e-7)
                return contour;

            if (!enableDogbones || reliefType == CornerReliefType.None || radius <= 1e-6)
            {
                Point2D pBL = new Point2D(s1, t1);
                Point2D pBR = new Point2D(s2, t1);
                Point2D pTR = new Point2D(s2, t2);
                Point2D pTL = new Point2D(s1, t2);

                contour.Lines.Add(new LineSegment2D(pBL, pBR));
                contour.Lines.Add(new LineSegment2D(pBR, pTR));
                contour.Lines.Add(new LineSegment2D(pTR, pTL));
                contour.Lines.Add(new LineSegment2D(pTL, pBL));
                return contour;
            }

            double r = Math.Min(radius, 0.5 * Math.Min(width, height));

            if (reliefType == CornerReliefType.TBone && width >= 4.0 * r + 1e-6)
            {
                // T-bone relief along the top & bottom edges at the four corners
                Point2D cBL = new Point2D(s1 + r, t1);
                Point2D cBR = new Point2D(s2 - r, t1);
                Point2D cTR = new Point2D(s2 - r, t2);
                Point2D cTL = new Point2D(s1 + r, t2);

                contour.Arcs.Add(new DogboneArc2D(cBL, r, new Point2D(s1, t1), new Point2D(s1 + 2.0 * r, t1), new Point2D(s1 + r, t1 - r)));
                contour.Lines.Add(new LineSegment2D(new Point2D(s1 + 2.0 * r, t1), new Point2D(s2 - 2.0 * r, t1)));
                contour.Arcs.Add(new DogboneArc2D(cBR, r, new Point2D(s2 - 2.0 * r, t1), new Point2D(s2, t1), new Point2D(s2 - r, t1 - r)));
                contour.Lines.Add(new LineSegment2D(new Point2D(s2, t1), new Point2D(s2, t2)));
                contour.Arcs.Add(new DogboneArc2D(cTR, r, new Point2D(s2, t2), new Point2D(s2 - 2.0 * r, t2), new Point2D(s2 - r, t2 + r)));
                contour.Lines.Add(new LineSegment2D(new Point2D(s2 - 2.0 * r, t2), new Point2D(s1 + 2.0 * r, t2)));
                contour.Arcs.Add(new DogboneArc2D(cTL, r, new Point2D(s1 + 2.0 * r, t2), new Point2D(s1, t2), new Point2D(s1 + r, t2 + r)));
                contour.Lines.Add(new LineSegment2D(new Point2D(s1, t2), new Point2D(s1, t1)));
                return contour;
            }

            // Diagonal 45-degree dogbone at each corner:
            // Center C = V + (r / sqrt(2)) * (u_s + u_t)
            double c = r / Math.Sqrt(2.0);
            double leg = 2.0 * c; // r * sqrt(2)

            Point2D vBL = new Point2D(s1, t1);
            Point2D vBR = new Point2D(s2, t1);
            Point2D vTR = new Point2D(s2, t2);
            Point2D vTL = new Point2D(s1, t2);

            Point2D centerBL = CalculateDiagonalDogboneCenter(vBL, new Point2D(1, 0), new Point2D(0, 1), r);
            Point2D centerBR = CalculateDiagonalDogboneCenter(vBR, new Point2D(-1, 0), new Point2D(0, 1), r);
            Point2D centerTR = CalculateDiagonalDogboneCenter(vTR, new Point2D(-1, 0), new Point2D(0, -1), r);
            Point2D centerTL = CalculateDiagonalDogboneCenter(vTL, new Point2D(1, 0), new Point2D(0, -1), r);

            bool hasStraightS = (2.0 * leg < width - 1e-7);
            bool hasStraightT = (2.0 * leg < height - 1e-7);

            Point2D botLeftEnd, botRightStart;
            Point2D topRightEnd, topLeftStart;
            if (hasStraightS)
            {
                botLeftEnd = new Point2D(s1 + leg, t1);
                botRightStart = new Point2D(s2 - leg, t1);
                topRightEnd = new Point2D(s2 - leg, t2);
                topLeftStart = new Point2D(s1 + leg, t2);
            }
            else
            {
                double sMid = 0.5 * (s1 + s2);
                double dy = Math.Sqrt(Math.Max(0.0, r * r - Math.Pow(0.5 * width - c, 2.0))) - c;
                botLeftEnd = botRightStart = new Point2D(sMid, t1 - dy);
                topRightEnd = topLeftStart = new Point2D(sMid, t2 + dy);
            }

            Point2D rightBotEnd, rightTopStart;
            Point2D leftTopEnd, leftBotStart;
            if (hasStraightT)
            {
                rightBotEnd = new Point2D(s2, t1 + leg);
                rightTopStart = new Point2D(s2, t2 - leg);
                leftTopEnd = new Point2D(s1, t2 - leg);
                leftBotStart = new Point2D(s1, t1 + leg);
            }
            else
            {
                double tMid = 0.5 * (t1 + t2);
                double dx = Math.Sqrt(Math.Max(0.0, r * r - Math.Pow(0.5 * height - c, 2.0))) - c;
                rightBotEnd = rightTopStart = new Point2D(s2 + dx, tMid);
                leftTopEnd = leftBotStart = new Point2D(s1 - dx, tMid);
            }

            // Corner arcs (in CCW order around the slot)
            contour.Arcs.Add(BuildCornerArc(centerBL, r, vBL, leftBotStart, botLeftEnd));
            if (hasStraightS)
                contour.Lines.Add(new LineSegment2D(botLeftEnd, botRightStart));

            contour.Arcs.Add(BuildCornerArc(centerBR, r, vBR, botRightStart, rightBotEnd));
            if (hasStraightT)
                contour.Lines.Add(new LineSegment2D(rightBotEnd, rightTopStart));

            contour.Arcs.Add(BuildCornerArc(centerTR, r, vTR, rightTopStart, topRightEnd));
            if (hasStraightS)
                contour.Lines.Add(new LineSegment2D(topRightEnd, topLeftStart));

            contour.Arcs.Add(BuildCornerArc(centerTL, r, vTL, topLeftStart, leftTopEnd));
            if (hasStraightT)
                contour.Lines.Add(new LineSegment2D(leftTopEnd, leftBotStart));

            return contour;
        }

        private static DogboneArc2D BuildCornerArc(Point2D center, double radius, Point2D cornerVertex, Point2D start, Point2D end)
        {
            double angCorner = Math.Atan2(cornerVertex.Y - center.Y, cornerVertex.X - center.X);
            double angStart = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double angEnd = Math.Atan2(end.Y - center.Y, end.X - center.X);

            double dStart = WrapAngleSigned(angStart - angCorner);
            double dEnd = WrapAngleSigned(angEnd - angCorner);
            double angMid = angCorner + 0.5 * (dStart + dEnd);

            Point2D mid = new Point2D(
                center.X + radius * Math.Cos(angMid),
                center.Y + radius * Math.Sin(angMid));

            return new DogboneArc2D(center, radius, start, end, mid);
        }

        private static double WrapAngleSigned(double angleRad)
        {
            while (angleRad > Math.PI) angleRad -= 2.0 * Math.PI;
            while (angleRad < -Math.PI) angleRad += 2.0 * Math.PI;
            return angleRad;
        }

        /// <summary>
        /// Deletes prior managed child features, resolves directional keying, and generates
        /// B-Rep cuts, bosses, sketches, and reference planes for every joint in the group.
        /// </summary>
        public static List<IFeature> ApplyJointGroupToModel(ISldWorks swApp, IModelDoc2 model, JointGroupDefinition group)
        {
            List<IFeature> createdFeatures = new List<IFeature>();
            if (swApp == null || model == null || group == null)
                return createdFeatures;

            PartEventCleanUpHandler.SuppressCleanup = true;
            try
            {
                DeleteManagedFeatures(model, group);
                KeyingEngine.ResolveKeying(group);

                if (group.Joints != null)
                {
                    for (int i = 0; i < group.Joints.Count; i++)
                    {
                        if (ApplyJointToModel(swApp, model, group.Joints[i], i + 1, out List<IFeature> jointFeats))
                        {
                            createdFeatures.AddRange(jointFeats);
                        }
                    }
                }

                model.ClearSelection2(true);
                model.EditRebuild3();
            }
            catch (Exception ex)
            {
                SwAddin.Log("ApplyJointGroupToModel error: " + ex);
            }
            finally
            {
                PartEventCleanUpHandler.SuppressCleanup = false;
            }

            return createdFeatures;
        }

        /// <summary>
        /// Backwards-compatible helper overload.
        /// </summary>
        public static bool ApplyJointToModel(ISldWorks swApp, IModelDoc2 model, JointDefinition joint)
        {
            return ApplyJointToModel(swApp, model, joint, 1, out _);
        }

        public static bool ApplyJointToModel(
            ISldWorks swApp,
            IModelDoc2 model,
            JointDefinition joint,
            int index,
            out List<IFeature> createdFeatures)
        {
            createdFeatures = new List<IFeature>();
            if (swApp == null || model == null || joint == null)
                return false;

            try
            {
                GenerateJointIntervals(joint, out List<Interval> tabIntervals, out List<Interval> slotIntervals);

                IBody2 sourceBody = JoineryTopologyScanner.ResolvePersistReference(model, joint.SourceBodyPersistId) as IBody2;
                IBody2 targetBody = JoineryTopologyScanner.ResolvePersistReference(model, joint.TargetBodyPersistId) as IBody2;
                IFace2 sourceFace = JoineryTopologyScanner.ResolvePersistReference(model, joint.SourceFacePersistId) as IFace2;
                IFace2 targetFace = JoineryTopologyScanner.ResolvePersistReference(model, joint.TargetFacePersistId) as IFace2;

                if (sourceBody == null && sourceFace != null)
                    sourceBody = sourceFace.GetBody() as IBody2;
                if (targetBody == null && targetFace != null)
                    targetBody = targetFace.GetBody() as IBody2;

                // Create a coincident Reference Plane on the contact interface so subsequent cuts/bosses never invalidate the sketch plane
                IFeature contactPlaneFeat = CreateContactReferencePlane(swApp, model, joint, targetFace, sourceFace, index);
                if (contactPlaneFeat != null)
                {
                    joint.ManagedRefPlaneName = contactPlaneFeat.Name;
                    createdFeatures.Add(contactPlaneFeat);
                }

                ISketchManager sketchMgr = model.SketchManager;
                IMathUtility mathUtil = (IMathUtility)swApp.GetMathUtility();

                // Determine cut/extrusion depth and thickness band
                bool isThrough = (joint.Type == JointType.TabAndSlot || joint.Type == JointType.FingerJoint);
                double depth = isThrough
                    ? Math.Max(0.001, joint.TargetBodyThickness)
                    : Math.Max(0.0005, joint.DadoDepth);

                double halfT = 0.5 * Math.Max(0.0005, joint.Thickness);
                double tMinBase = -halfT;
                double tMaxBase = (joint.Type == JointType.Rabbet) ? 0.0 : halfT;

                // For FingerJoint, Body B receives cuts matching Body A's fingers (tabIntervals)
                List<Interval> targetCutIntervals = (joint.Type == JointType.FingerJoint) ? tabIntervals : slotIntervals;

                // --- STEP A: Target Body B Slot / Dado Cut ---
                if (SelectSketchPlane(model, contactPlaneFeat, targetFace, joint))
                {
                    sketchMgr.InsertSketch(true);
                    ISketch slotSketch = sketchMgr.ActiveSketch;
                    if (slotSketch != null)
                    {
                        IMathTransform xform = slotSketch.ModelToSketchTransform;
                        bool alignsWithPlusZ = DoesSketchZAlignWithDirection(mathUtil, xform, joint.CutDirectionUnit);
                        sketchMgr.AddToDB = true;

                        foreach (Interval iv in targetCutIntervals)
                        {
                            double c = joint.SignedClearance;
                            double s1 = iv.Start - c;
                            double s2 = iv.End + c;
                            double t1 = tMinBase - c;
                            double t2 = tMaxBase + c;

                            bool applyCornerRelief = joint.EnableDogbones && joint.Type != JointType.ThroughDado && joint.Type != JointType.Rabbet;

                            // Through-dado & rabbet runout extension beyond workpiece bounds
                            if (joint.EnableDogbones && (joint.Type == JointType.ThroughDado || joint.Type == JointType.Rabbet))
                            {
                                double runout = joint.ToolBitRadius + Math.Abs(c);
                                s1 -= runout;
                                s2 += runout;
                            }

                            if (s2 <= s1 + 1e-6)
                            {
                                double mid = 0.5 * (iv.Start + iv.End);
                                s1 = mid - 0.0005;
                                s2 = mid + 0.0005;
                            }
                            if (t2 <= t1 + 1e-6)
                            {
                                double tMid = 0.5 * (tMinBase + tMaxBase);
                                t1 = tMid - 0.00025;
                                t2 = tMid + 0.00025;
                            }

                            SlotContour2D contour = ComputeSlotContourWithDogbones2D(
                                s1, s2, t1, t2,
                                joint.ToolBitRadius,
                                applyCornerRelief,
                                joint.ReliefType);

                            DrawContourInSketch(sketchMgr, mathUtil, xform, joint, contour);
                        }

                        sketchMgr.AddToDB = false;
                        FullyDefineGeneratedSketch(model);
                        IFeature slotSketchFeat = (IFeature)slotSketch;
                        sketchMgr.InsertSketch(true);

                        if (slotSketchFeat != null)
                        {
                            try { slotSketchFeat.Name = string.Format("JoinerySlotSketch_{0}_{1}", joint.JointId, index); } catch { }
                            joint.ManagedSlotSketchName = slotSketchFeat.Name;
                            createdFeatures.Add(slotSketchFeat);

                            IFeature slotCutFeat = ExecuteScopedCut(model, slotSketchFeat, targetBody, depth, alignsWithPlusZ);
                            if (slotCutFeat != null)
                            {
                                try { slotCutFeat.Name = string.Format("JoinerySlotCut_{0}_{1}", joint.JointId, index); } catch { }
                                joint.ManagedSlotCutName = slotCutFeat.Name;
                                createdFeatures.Add(slotCutFeat);
                            }
                        }
                    }
                }

                // --- STEP B: Source Body A Tab / Finger / Tongue Boss ---
                if (tabIntervals != null && tabIntervals.Count > 0 &&
                    (joint.Type == JointType.TabAndSlot ||
                     joint.Type == JointType.FingerJoint ||
                     joint.Type == JointType.Rabbet ||
                     joint.Type == JointType.DadoTabCombo))
                {
                    if (SelectSketchPlane(model, contactPlaneFeat, sourceFace, joint))
                    {
                        sketchMgr.InsertSketch(true);
                        ISketch tabSketch = sketchMgr.ActiveSketch;
                        if (tabSketch != null)
                        {
                            IMathTransform xform = tabSketch.ModelToSketchTransform;
                            bool alignsWithPlusZ = DoesSketchZAlignWithDirection(mathUtil, xform, joint.CutDirectionUnit);
                            sketchMgr.AddToDB = true;

                            foreach (Interval iv in tabIntervals)
                            {
                                SlotContour2D tabRect = ComputeSlotContourWithDogbones2D(
                                    iv.Start, iv.End, tMinBase, tMaxBase,
                                    0.0, false, CornerReliefType.None);
                                DrawContourInSketch(sketchMgr, mathUtil, xform, joint, tabRect);
                            }

                            sketchMgr.AddToDB = false;
                            FullyDefineGeneratedSketch(model);
                            IFeature tabSketchFeat = (IFeature)tabSketch;
                            sketchMgr.InsertSketch(true);

                            if (tabSketchFeat != null)
                            {
                                try { tabSketchFeat.Name = string.Format("JoineryTabSketch_{0}_{1}", joint.JointId, index); } catch { }
                                joint.ManagedTabSketchName = tabSketchFeat.Name;
                                createdFeatures.Add(tabSketchFeat);

                                IFeature tabBossFeat = ExecuteScopedBossMerge(model, tabSketchFeat, sourceBody, depth, !alignsWithPlusZ);
                                if (tabBossFeat != null)
                                {
                                    try { tabBossFeat.Name = string.Format("JoineryTabBoss_{0}_{1}", joint.JointId, index); } catch { }
                                    joint.ManagedTabBossName = tabBossFeat.Name;
                                    createdFeatures.Add(tabBossFeat);
                                }
                            }
                        }
                    }
                }

                if (contactPlaneFeat != null)
                {
                    model.ClearSelection2(true);
                    contactPlaneFeat.Select2(false, 0);
                    model.BlankRefGeom();
                }

                return createdFeatures.Count > 0;
            }
            catch (Exception ex)
            {
                SwAddin.Log("ApplyJointToModel error: " + ex);
                return false;
            }
        }

        private static IFeature CreateContactReferencePlane(
            ISldWorks swApp,
            IModelDoc2 model,
            JointDefinition joint,
            IFace2 targetFace,
            IFace2 sourceFace,
            int index)
        {
            try
            {
                IFace2 refFace = targetFace ?? sourceFace;
                if (refFace is IEntity ent)
                {
                    model.ClearSelection2(true);
                    if (ent.Select4(false, null))
                    {
                        IFeature plane = (IFeature)model.FeatureManager.InsertRefPlane(
                            (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                            0, 0.0, 0, 0.0);
                        if (plane != null)
                        {
                            try { plane.Name = string.Format("JoineryPlane_{0}_{1}", joint.JointId, index); } catch { }
                            return plane;
                        }
                    }
                }

                // Fallback 3D guide line perpendicular plane at Origin
                model.ClearSelection2(true);
                ISketchManager sketchMgr = model.SketchManager;
                sketchMgr.Insert3DSketch(true);
                IFeature guideSketch = (IFeature)sketchMgr.ActiveSketch;
                sketchMgr.AddToDB = true;

                Vector3D endPt = joint.Origin + joint.CutDirectionUnit.Normalized() * 0.01;
                ISketchSegment lineSeg = sketchMgr.CreateLine(
                    joint.Origin.X, joint.Origin.Y, joint.Origin.Z,
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

                    if (guideSketch != null)
                    {
                        model.ClearSelection2(true);
                        guideSketch.Select2(false, 0);
                        model.BlankSketch();
                    }

                    if (refPlane != null)
                    {
                        try { refPlane.Name = string.Format("JoineryPlane_{0}_{1}", joint.JointId, index); } catch { }
                        return refPlane;
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("CreateContactReferencePlane error: " + ex);
            }
            return null;
        }

        private static bool SelectSketchPlane(IModelDoc2 model, IFeature planeFeat, IFace2 fallbackFace, JointDefinition joint)
        {
            model.ClearSelection2(true);
            if (planeFeat != null && planeFeat.Select2(false, 0))
                return true;

            if (fallbackFace is IEntity ent && ent.Select4(false, null))
                return true;

            Vector3D mid = joint.Origin + joint.SpanUnit * (0.5 * joint.SpanLength);
            return model.Extension.SelectByRay(
                mid.X, mid.Y, mid.Z,
                joint.CutDirectionUnit.X, joint.CutDirectionUnit.Y, joint.CutDirectionUnit.Z,
                0.002, (int)swSelectType_e.swSelFACES, false, 0, 0);
        }

        private static void DrawContourInSketch(
            ISketchManager sketchMgr,
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            JointDefinition joint,
            SlotContour2D contour)
        {
            if (contour == null) return;

            foreach (LineSegment2D line in contour.Lines)
            {
                Point2D p1 = MapJoint2DToSketch(mathUtil, modelToSketch, joint, line.Start);
                Point2D p2 = MapJoint2DToSketch(mathUtil, modelToSketch, joint, line.End);
                sketchMgr.CreateLine(p1.X, p1.Y, 0, p2.X, p2.Y, 0);
            }

            foreach (DogboneArc2D arc in contour.Arcs)
            {
                Point2D pStart = MapJoint2DToSketch(mathUtil, modelToSketch, joint, arc.Start);
                Point2D pEnd = MapJoint2DToSketch(mathUtil, modelToSketch, joint, arc.End);
                Point2D pMid = MapJoint2DToSketch(mathUtil, modelToSketch, joint, arc.Mid);
                sketchMgr.Create3PointArc(pStart.X, pStart.Y, 0, pEnd.X, pEnd.Y, 0, pMid.X, pMid.Y, 0);
            }
        }

        private static Point2D MapJoint2DToSketch(
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            JointDefinition joint,
            Point2D st)
        {
            Vector3D pt3d = joint.Origin + joint.SpanUnit * st.X + joint.ThicknessUnit * st.Y;
            double[] coords = new[] { pt3d.X, pt3d.Y, pt3d.Z };
            IMathPoint mp = (IMathPoint)mathUtil.CreatePoint(coords);
            if (modelToSketch != null)
            {
                mp = (IMathPoint)mp.MultiplyTransform(modelToSketch);
            }
            double[] sCoords = (double[])mp.ArrayData;
            return new Point2D(sCoords[0], sCoords[1]);
        }

        private static bool DoesSketchZAlignWithDirection(
            IMathUtility mathUtil,
            IMathTransform modelToSketch,
            Vector3D targetDir)
        {
            if (mathUtil == null || modelToSketch == null)
                return false;
            try
            {
                IMathTransform sketchToModel = (IMathTransform)modelToSketch.Inverse();
                IMathVector localZ = (IMathVector)mathUtil.CreateVector(new double[] { 0.0, 0.0, 1.0 });
                IMathVector modelZ = (IMathVector)localZ.MultiplyTransform(sketchToModel);
                double[] zArr = (double[])modelZ.ArrayData;
                Vector3D sketchZ3D = new Vector3D(zArr[0], zArr[1], zArr[2]).Normalized();
                return sketchZ3D.Dot(targetDir) >= 0.0;
            }
            catch
            {
                return false;
            }
        }

        private static IFeature ExecuteScopedCut(
            IModelDoc2 model,
            IFeature sketchFeat,
            IBody2 targetBody,
            double depth,
            bool preferredDir)
        {
            if (model == null || sketchFeat == null) return null;

            bool[] dirOpts = new[] { preferredDir, !preferredDir };
            foreach (bool dir in dirOpts)
            {
                model.ClearSelection2(true);
                sketchFeat.Select2(false, 0);

                IFeature cut = model.FeatureManager.FeatureCut4(
                    true,  // Sd = true
                    false,
                    dir,
                    (int)swEndConditions_e.swEndCondBlind,
                    (int)swEndConditions_e.swEndCondBlind,
                    depth, 0.0,
                    false, false, false, false, 0.0, 0.0,
                    false, false, false, false, false,
                    true,  // UseFeatScope
                    true,  // UseAutoSelect first so cut never returns null
                    false, false, false,
                    (int)swStartConditions_e.swStartSketchPlane,
                    0.0, false, false);

                if (cut != null)
                {
                    RestrictFeatureScopeToBody(model, cut, targetBody, setMerge: false);
                    return cut;
                }
            }

            return null;
        }

        private static IFeature ExecuteScopedBossMerge(
            IModelDoc2 model,
            IFeature sketchFeat,
            IBody2 sourceBody,
            double depth,
            bool preferredDir)
        {
            if (model == null || sketchFeat == null) return null;

            bool[] dirOpts = new[] { preferredDir, !preferredDir };
            foreach (bool dir in dirOpts)
            {
                model.ClearSelection2(true);
                sketchFeat.Select2(false, 0);

                // Create with Merge = false first so SolidWorks never merges sourceBody and targetBody together
                IFeature boss = model.FeatureManager.FeatureExtrusion3(
                    true,  // Sd = true
                    false,
                    dir,
                    (int)swEndConditions_e.swEndCondBlind,
                    (int)swEndConditions_e.swEndCondBlind,
                    depth, 0.0,
                    false, false, false, false, 0.0, 0.0,
                    false, false, false, false,
                    false, // Merge = false initially
                    false, // UseFeatScope
                    false, // UseAutoSelect
                    (int)swStartConditions_e.swStartSketchPlane,
                    0.0, false);

                if (boss != null)
                {
                    if (sourceBody != null)
                    {
                        RestrictFeatureScopeToBody(model, boss, sourceBody, setMerge: true);
                    }
                    return boss;
                }
            }

            return null;
        }

        private static void RestrictFeatureScopeToBody(IModelDoc2 model, IFeature feat, IBody2 body, bool setMerge)
        {
            if (model == null || feat == null || body == null) return;
            try
            {
                IExtrudeFeatureData2 data = feat.GetDefinition() as IExtrudeFeatureData2;
                if (data == null) return;

                if (data.AccessSelections(model, null))
                {
                    if (setMerge) data.Merge = true;
                    data.FeatureScope = true;
                    data.AutoSelect = false;
                    data.FeatureScopeBodies = new object[] { (Body2)body };
                    if (!feat.ModifyDefinition(data, model, null))
                    {
                        data.ReleaseSelectionAccess();
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("RestrictFeatureScopeToBody warning: " + ex.Message);
            }
        }

        public static void DeleteManagedFeatures(IModelDoc2 model, JointGroupDefinition group)
        {
            if (model == null || group?.Joints == null) return;
            try
            {
                IModelDocExtension ext = model.Extension;
                foreach (JointDefinition j in group.Joints)
                {
                    DeleteFeatureByName(model, ext, j.ManagedTabBossName);
                    DeleteFeatureByName(model, ext, j.ManagedTabSketchName);
                    DeleteFeatureByName(model, ext, j.ManagedSlotCutName);
                    DeleteFeatureByName(model, ext, j.ManagedSlotSketchName);
                    DeleteFeatureByName(model, ext, j.ManagedRefPlaneName);
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
                ext.SelectByID2(featName, "PLANE", 0, 0, 0, false, 0, null, 0))
            {
                ext.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
            }
        }

        private static void FullyDefineGeneratedSketch(IModelDoc2 model)
        {
            if (model == null) return;
            ISketchManager sm = model.SketchManager;
            ISketch sk = sm != null ? sm.ActiveSketch : null;
            if (sk == null) return;

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }
            try
            {
                if (prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                if (sk.GetConstrainedStatus() == (int)swConstrainedStatus_e.swFullyConstrained)
                    return;

                int allRelMask =
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Equal |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Tangent |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Perpendicular |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Colinear |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Concentric |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Parallel |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Midpoint |
                    (int)swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident;

                try
                {
                    sm.FullyDefineSketch(
                        true, true, allRelMask, true,
                        (int)swAutodimScheme_e.swAutodimSchemeBaseline, null,
                        (int)swAutodimScheme_e.swAutodimSchemeBaseline, null,
                        (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementAbove,
                        (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementRight);
                }
                catch { }

                if (sk.GetConstrainedStatus() == (int)swConstrainedStatus_e.swUnderConstrained)
                {
                    try
                    {
                        model.ClearSelection2(true);
                        int selCount = 0;
                        object[] segs = sk.GetSketchSegments() as object[];
                        if (segs != null)
                        {
                            foreach (object o in segs)
                            {
                                if (o is ISketchSegment seg && seg.Select4(selCount > 0, null))
                                    selCount++;
                            }
                        }
                        if (selCount > 0)
                            model.SketchAddConstraints("sgFIXED");
                        model.ClearSelection2(true);
                    }
                    catch { }
                }
            }
            finally
            {
                if (prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }
    }
}
