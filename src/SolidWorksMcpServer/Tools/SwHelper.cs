using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;

namespace SolidWorksMcpServer.Tools
{
    /// <summary>
    /// Built-in helper utilities exposed directly inside sw_execute_csharp scripts (and internal tools).
    /// Prevents common SOLIDWORKS COM traps (swUserDecision, EditDelete, ForceRebuild3 loops, raw PlaneParams normals).
    /// </summary>
    public static class SwHelper
    {
        [ThreadStatic]
        private static ISldWorks _ctxApp;

        [ThreadStatic]
        private static IModelDoc2 _ctxModel;

        [ThreadStatic]
        private static Action<string> _ctxLog;

        internal static void BindSession(ISldWorks app, IModelDoc2 model, Action<string> log)
        {
            _ctxApp = app;
            _ctxModel = model;
            _ctxLog = log;
        }

        internal static void ClearSession()
        {
            _ctxApp = null;
            _ctxModel = null;
            _ctxLog = null;
        }

        public static IModelDoc2 Activate(string docName)
        {
            return Activate(_ctxApp, docName);
        }

        /// <summary>
        /// Temporarily suppresses FeatureManager tree updates AND OpenGL viewport repaints
        /// (EnableFeatureTree = false, EnableGraphicsUpdate = false) to speed up bulk assembly
        /// edits and prevent SWCEFComWrapper (libcef) UI notification storms in SOLIDWORKS 2026.
        /// Always wrap in a using block and dispose before calling EditRebuild3().
        /// </summary>
        public sealed class UiUpdateScope : IDisposable
        {
            private readonly IFeatureManager _fm;
            private readonly bool _prevTree;
            private readonly bool _hadTree;
            private readonly IModelView _view;
            private readonly bool _prevGfx;
            private readonly bool _hadGfx;
            private bool _disposed;

            internal UiUpdateScope(IModelDoc2 doc)
            {
                if (doc == null) return;
                try
                {
                    _fm = doc.FeatureManager;
                    if (_fm != null)
                    {
                        _prevTree = _fm.EnableFeatureTree;
                        _hadTree = true;
                        if (_prevTree) _fm.EnableFeatureTree = false;
                    }
                }
                catch { }

                try
                {
                    _view = doc.ActiveView as IModelView;
                    if (_view != null)
                    {
                        _prevGfx = _view.EnableGraphicsUpdate;
                        _hadGfx = true;
                        if (_prevGfx) _view.EnableGraphicsUpdate = false;
                    }
                }
                catch { }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_hadTree && _fm != null)
                {
                    try { _fm.EnableFeatureTree = _prevTree; } catch { }
                }
                if (_hadGfx && _view != null)
                {
                    try { _view.EnableGraphicsUpdate = _prevGfx; } catch { }
                }
            }
        }

        public static IDisposable SuppressUiUpdates(IModelDoc2 doc = null)
        {
            return new UiUpdateScope(doc ?? _ctxModel);
        }

        /// <summary>
        /// Selects all matching components with append=true and deletes them in ONE
        /// DeleteSelection2(0) call inside SuppressUiUpdates (avoids per-item rebuild/repaint storms).
        /// </summary>
        public static int BatchDeleteComponents(IModelDoc2 doc, IEnumerable<string> componentNames, bool rebuild = false)
        {
            doc = doc ?? _ctxModel;
            if (!(doc is IAssemblyDoc assy) || componentNames == null) return 0;

            int selected = 0;
            bool ok = false;
            using (SuppressUiUpdates(doc))
            {
                doc.ClearSelection2(true);
                foreach (string name in componentNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    IComponent2 comp = AssemblyGeometryHelper.FindComponentByName(assy, name.Trim());
                    if (comp != null && comp.Select4(selected > 0, null, false))
                        selected++;
                }
                if (selected > 0)
                    ok = doc.Extension.DeleteSelection2(0);
                doc.ClearSelection2(true);
            }

            if (rebuild && ok && selected > 0)
            {
                try { doc.EditRebuild3(); } catch { }
            }
            return ok ? selected : 0;
        }

        /// <summary>
        /// Folder-safe batch deletion of named mates in ONE DeleteSelection2(0) call inside SuppressUiUpdates.
        /// </summary>
        public static int BatchDeleteMates(IModelDoc2 doc, IEnumerable<string> mateNames, bool rebuild = false)
        {
            doc = doc ?? _ctxModel;
            if (doc == null || mateNames == null) return 0;

            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mn in mateNames)
            {
                if (!string.IsNullOrWhiteSpace(mn))
                    targets.Add(mn.Trim());
            }
            if (targets.Count == 0) return 0;

            List<IFeature> matched = new List<IFeature>();
            CollectMatesRecursive(doc, (feat, mate) => targets.Contains(feat.Name ?? string.Empty), matched);
            return DeleteMateFeaturesBatch(doc, matched, rebuild);
        }

        /// <summary>
        /// Folder-safe batch deletion of all mates referencing any of the specified components
        /// in ONE DeleteSelection2(0) call inside SuppressUiUpdates.
        /// </summary>
        public static int BatchDeleteMatesForComponents(
            IModelDoc2 doc,
            IEnumerable<string> componentNames,
            bool topLevelOnly = true,
            bool rebuild = false)
        {
            doc = doc ?? _ctxModel;
            if (!(doc is IAssemblyDoc) || componentNames == null) return 0;

            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string cn in componentNames)
            {
                if (!string.IsNullOrWhiteSpace(cn))
                    targets.Add(cn.Trim());
            }
            if (targets.Count == 0) return 0;

            List<IFeature> matched = new List<IFeature>();
            CollectMatesRecursive(doc, (feat, mate) =>
            {
                if (mate == null) return false;
                int entCount = 0;
                try { entCount = mate.GetMateEntityCount(); } catch { return false; }
                for (int k = 0; k < entCount; k++)
                {
                    IMateEntity2 ent = null;
                    try { ent = mate.MateEntity(k); } catch { }
                    IComponent2 rc = null;
                    try { rc = ent != null ? ent.ReferenceComponent : null; } catch { }
                    string rcName = rc != null ? (rc.Name2 ?? string.Empty) : string.Empty;
                    if (string.IsNullOrEmpty(rcName)) continue;
                    if (topLevelOnly && rcName.IndexOf('/') >= 0) continue;
                    if (targets.Contains(rcName)) return true;
                }
                return false;
            }, matched);

            return DeleteMateFeaturesBatch(doc, matched, rebuild);
        }

        /// <summary>
        /// Returns the names of all currently visible 2D/3D sketches (including absorbed sub-feature sketches)
        /// so callers can preserve pre-existing sketch visibility while hiding newly created sketches.
        /// </summary>
        public static HashSet<string> GetVisibleSketchNames(IModelDoc2 doc = null)
        {
            doc = doc ?? _ctxModel;
            HashSet<string> visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return visible;

            foreach (IFeature skFeat in EnumerateSketchFeatures(doc))
            {
                try
                {
                    if (skFeat.Visible == (int)swVisibilityState_e.swVisibilityStateShown && !string.IsNullOrEmpty(skFeat.Name))
                        visible.Add(skFeat.Name);
                }
                catch { }
            }
            return visible;
        }

        /// <summary>
        /// Hides specified 2D/3D sketches (or all currently visible sketches when sketchNames is null)
        /// using BlankSketch(), optionally preserving any names in preserveVisibleNames.
        /// </summary>
        public static int HideSketches(
            IModelDoc2 doc = null,
            IEnumerable<string> sketchNames = null,
            ISet<string> preserveVisibleNames = null)
        {
            doc = doc ?? _ctxModel;
            if (doc == null) return 0;

            HashSet<string> targetSet = null;
            if (sketchNames != null)
            {
                targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in sketchNames)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                        targetSet.Add(s.Trim());
                }
                if (targetSet.Count == 0) return 0;
            }

            List<IFeature> toHide = new List<IFeature>();
            foreach (IFeature skFeat in EnumerateSketchFeatures(doc))
            {
                string name = skFeat.Name ?? string.Empty;
                if (preserveVisibleNames != null && preserveVisibleNames.Contains(name))
                    continue;
                if (targetSet != null)
                {
                    if (!targetSet.Contains(name)) continue;
                }
                else
                {
                    bool isShown = false;
                    try { isShown = skFeat.Visible == (int)swVisibilityState_e.swVisibilityStateShown; } catch { }
                    if (!isShown) continue;
                }
                toHide.Add(skFeat);
            }

            if (toHide.Count == 0) return 0;

            int hidden = 0;
            doc.ClearSelection2(true);
            foreach (IFeature skFeat in toHide)
            {
                try
                {
                    if (skFeat.Select2(hidden > 0, 0))
                        hidden++;
                }
                catch { }
            }
            if (hidden > 0)
            {
                try { doc.BlankSketch(); } catch { }
            }
            doc.ClearSelection2(true);
            return hidden;
        }

        private static List<IFeature> EnumerateSketchFeatures(IModelDoc2 doc)
        {
            List<IFeature> result = new List<IFeature>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                CollectSketchFeatureIfMatch(f, seen, result);
                IFeature sub = f.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    CollectSketchFeatureIfMatch(sub, seen, result);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return result;
        }

        private static void CollectSketchFeatureIfMatch(IFeature feat, HashSet<string> seen, List<IFeature> result)
        {
            if (feat == null) return;
            string t = feat.GetTypeName2() ?? string.Empty;
            if (!string.Equals(t, "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(t, "3DProfileFeature", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string name = feat.Name ?? string.Empty;
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                result.Add(feat);
        }

        /// <summary>
        /// Unpins the SOLIDWORKS Task Pane and optionally unloads the CEF-heavy AURA add-in
        /// (SldAuraEx.dll) at runtime to reduce SWCEFComWrapper background message traffic.
        /// </summary>
        public static Dictionary<string, object> MinimizeCefUiNoise(ISldWorks swApp = null, bool unloadAura = true)
        {
            ISldWorks app = swApp ?? _ctxApp;
            bool taskPaneUnpinned = false;
            bool auraUnloaded = false;
            string auraStatus = "skipped";

            if (app != null)
            {
                try
                {
                    if (app.TaskPaneIsPinned)
                    {
                        app.TaskPaneIsPinned = false;
                        taskPaneUnpinned = true;
                    }
                }
                catch { }

                if (unloadAura)
                {
                    string[] auraCandidates =
                    {
                        @"C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\win_b64\code\bin\SWAura\sldAuraEx.dll",
                        @"C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\win_b64\code\bin\SWAura\SldAuraEx.DLL"
                    };
                    string foundPath = null;
                    foreach (string c in auraCandidates)
                    {
                        if (File.Exists(c)) { foundPath = c; break; }
                    }
                    if (!string.IsNullOrEmpty(foundPath))
                    {
                        try
                        {
                            int rc = app.UnloadAddIn(foundPath);
                            auraUnloaded = rc == 0 || rc == 1;
                            auraStatus = "UnloadAddIn_rc_" + rc.ToString(CultureInfo.InvariantCulture);
                        }
                        catch (Exception ex)
                        {
                            auraStatus = "error: " + ex.Message;
                        }
                    }
                    else
                    {
                        auraStatus = "not_installed";
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "task_pane_unpinned", taskPaneUnpinned },
                { "aura_unloaded", auraUnloaded },
                { "aura_status", auraStatus }
            };
        }

        private static int DeleteMateFeaturesBatch(IModelDoc2 doc, List<IFeature> mates, bool rebuild)
        {
            if (doc == null || mates == null || mates.Count == 0) return 0;
            int selected = 0;
            bool ok = false;
            using (SuppressUiUpdates(doc))
            {
                doc.ClearSelection2(true);
                foreach (IFeature mf in mates)
                {
                    if (mf != null && mf.Select2(selected > 0, 0))
                        selected++;
                }
                if (selected > 0)
                    ok = doc.Extension.DeleteSelection2(0);
                doc.ClearSelection2(true);
            }
            if (rebuild && ok && selected > 0)
            {
                try { doc.EditRebuild3(); } catch { }
            }
            return ok ? selected : 0;
        }

        private static void CollectMatesRecursive(IModelDoc2 doc, Func<IFeature, IMate2, bool> predicate, List<IFeature> results)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.GetTypeName2(), "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    IFeature sub = f.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        VisitMateOrFolder(sub, predicate, seen, results);
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                f = f.GetNextFeature() as IFeature;
            }
        }

        private static void VisitMateOrFolder(IFeature feat, Func<IFeature, IMate2, bool> predicate, HashSet<string> seen, List<IFeature> results)
        {
            if (feat == null) return;
            string fType = feat.GetTypeName2() ?? string.Empty;
            string fName = feat.Name ?? string.Empty;
            if (fName.EndsWith("___EndTag___", StringComparison.OrdinalIgnoreCase)) return;

            if (string.Equals(fType, "FtrFolder", StringComparison.OrdinalIgnoreCase))
            {
                IFeatureFolder folder = null;
                try { folder = feat.GetSpecificFeature2() as IFeatureFolder; } catch { }
                if (folder != null)
                {
                    object[] folderFeats = null;
                    try { folderFeats = folder.GetFeatures() as object[]; } catch { }
                    if (folderFeats != null)
                    {
                        foreach (object o in folderFeats)
                            if (o is IFeature child) VisitMateOrFolder(child, predicate, seen, results);
                    }
                }
                IFeature sub = feat.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    VisitMateOrFolder(sub, predicate, seen, results);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                return;
            }

            IMate2 mate = null;
            try { mate = feat.GetSpecificFeature2() as IMate2; } catch { }
            if (mate == null) return;
            if (!seen.Add(fName)) return;
            if (predicate(feat, mate))
                results.Add(feat);
        }

        public static IModelDoc2 Activate(ISldWorks swApp, string docName)
        {
            ISldWorks app = swApp ?? _ctxApp;
            if (app == null)
                throw new InvalidOperationException("No active ISldWorks session is bound to SwHelper.Activate.");

            if (string.IsNullOrWhiteSpace(docName))
            {
                IModelDoc2 act = app.ActiveDoc as IModelDoc2;
                if (act == null) throw new InvalidOperationException("No active SOLIDWORKS document.");
                return act;
            }

            IModelDoc2 active = app.ActiveDoc as IModelDoc2;
            if (active != null && SwConnectionManager.MatchesDocumentIdentifier(active, docName))
                return active;

            object[] docs = app.GetDocuments() as object[];
            if (docs != null)
            {
                foreach (object dObj in docs)
                {
                    if (dObj is IModelDoc2 candidate && SwConnectionManager.MatchesDocumentIdentifier(candidate, docName))
                    {
                        SwConnectionManager.ActivateDocumentSafe(app, candidate);
                        return candidate;
                    }
                }
            }

            if (File.Exists(docName))
            {
                SwConnectionManager mgr = new SwConnectionManager();
                IModelDoc2 opened = mgr.OpenOrImport(app, docName, out _);
                if (opened != null)
                {
                    SwConnectionManager.ActivateDocumentSafe(app, opened);
                    return opened;
                }
            }

            throw new InvalidOperationException("Could not find or open SOLIDWORKS document matching: " + docName);
        }

        public static IFeature MakeOffsetPlane(IModelDoc2 doc, string basePlane, double signedOffsetMeters, string name = null)
        {
            if (doc == null) doc = _ctxModel;
            if (doc == null) throw new ArgumentNullException("doc");
            if (string.IsNullOrWhiteSpace(basePlane)) throw new ArgumentNullException("basePlane");

            TryGetPlaneOriginAndNormal(doc, basePlane, out double[] baseOrigin, out double[] baseNormal);

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, basePlane, false, 0))
                throw new InvalidOperationException("Could not select base plane or planar face: " + basePlane);

            IFeature feat;
            if (Math.Abs(signedOffsetMeters) < 1e-9)
            {
                feat = doc.FeatureManager.InsertRefPlane(
                    (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident,
                    0.0, 0, 0.0, 0, 0.0) as IFeature;
            }
            else
            {
                double absOffset = Math.Abs(signedOffsetMeters);
                int cBase = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                int cFlip = signedOffsetMeters < 0
                    ? (cBase | (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip)
                    : cBase;

                feat = doc.FeatureManager.InsertRefPlane(cFlip, absOffset, 0, 0.0, 0, 0.0) as IFeature;
                if (feat == null)
                    throw new InvalidOperationException("InsertRefPlane returned null for base plane: " + basePlane);

                if (TryGetRefPlaneTransform(feat, out double[] newOrigin, out _))
                {
                    double delta = (newOrigin[0] - baseOrigin[0]) * baseNormal[0] +
                                   (newOrigin[1] - baseOrigin[1]) * baseNormal[1] +
                                   (newOrigin[2] - baseOrigin[2]) * baseNormal[2];
                    if (Math.Abs(delta - signedOffsetMeters) > 1e-5 && (delta * signedOffsetMeters < 0))
                    {
                        doc.ClearSelection2(true);
                        feat.Select2(false, 0);
                        doc.Extension.DeleteSelection2(0);
                        doc.ClearSelection2(true);

                        if (!SwConnectionManager.TrySelectByToken(doc, basePlane, false, 0))
                            throw new InvalidOperationException("Could not reselect base plane after sign check: " + basePlane);

                        int altConstraint = cFlip == cBase
                            ? (cBase | (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip)
                            : cBase;
                        feat = doc.FeatureManager.InsertRefPlane(altConstraint, absOffset, 0, 0.0, 0, 0.0) as IFeature;
                    }
                }
            }

            if (feat == null)
                throw new InvalidOperationException("Failed to create offset reference plane from: " + basePlane);

            if (!string.IsNullOrWhiteSpace(name))
            {
                try { feat.Name = name.Trim(); } catch { }
            }

            try
            {
                doc.ClearSelection2(true);
                feat.Select2(false, 0);
                doc.BlankRefGeom();
                doc.ClearSelection2(true);
            }
            catch { }

            return feat;
        }

        public static IEntity FindPlanarFace(
            IComponent2 comp,
            double[] worldPointMm,
            double[] worldOutwardNormal,
            double minAreaMm2 = 0.0,
            double tolMm = 1.5)
        {
            if (comp == null || worldPointMm == null || worldPointMm.Length < 3 ||
                worldOutwardNormal == null || worldOutwardNormal.Length < 3)
                return null;
            if (!(comp.GetModelDoc2() is IPartDoc part))
                return null;

            double[] m = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : null;
            double[] inv = AssemblyGeometryHelper.InvertTransform16(m);
            double[] ptM = { worldPointMm[0] * 0.001, worldPointMm[1] * 0.001, worldPointMm[2] * 0.001 };
            double[] localPtM = AssemblyGeometryHelper.TransformPointFast(inv, ptM[0], ptM[1], ptM[2]);
            double[] norm = AssemblyGeometryHelper.Normalize(worldOutwardNormal);
            double[] localNorm = AssemblyGeometryHelper.TransformVectorFast(inv, norm[0], norm[1], norm[2]);

            IFace2 face = FindPartPlanarFace(
                part,
                new[] { localPtM[0] * 1000.0, localPtM[1] * 1000.0, localPtM[2] * 1000.0 },
                localNorm,
                minAreaMm2,
                tolMm);
            return face != null ? comp.GetCorrespondingEntity(face) as IEntity : null;
        }

        public static IFace2 FindPartPlanarFace(
            IPartDoc part,
            double[] localPointMm,
            double[] localOutwardNormal,
            double minAreaMm2 = 0.0,
            double tolMm = 1.5)
        {
            if (part == null || localPointMm == null || localPointMm.Length < 3 ||
                localOutwardNormal == null || localOutwardNormal.Length < 3)
                return null;

            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return null;

            double[] targetPtM = { localPointMm[0] * 0.001, localPointMm[1] * 0.001, localPointMm[2] * 0.001 };
            double[] targetNorm = AssemblyGeometryHelper.Normalize(localOutwardNormal);
            double minAreaM2 = Math.Max(0.0, minAreaMm2) * 1e-6;
            double tolM = Math.Max(0.0001, tolMm * 0.001);

            IFace2 bestContained = null;
            double bestContainedDist = double.MaxValue;
            double bestContainedArea = -1.0;

            IFace2 bestAny = null;
            double bestAnyDist = double.MaxValue;
            double bestAnyArea = -1.0;

            foreach (object bObj in bodies)
            {
                if (!(bObj is IBody2 body)) continue;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    if (!(fObj is IFace2 face)) continue;
                    ISurface surf = face.GetSurface() as ISurface;
                    if (surf == null || !surf.IsPlane()) continue;
                    double area = face.GetArea();
                    if (area < minAreaM2) continue;

                    if (!AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(face, surf, out double[] ln, out double[] lp))
                        continue;

                    if (AssemblyGeometryHelper.Dot(ln, targetNorm) < 0.98) continue;

                    double pDist = Math.Abs(
                        (targetPtM[0] - lp[0]) * ln[0] +
                        (targetPtM[1] - lp[1]) * ln[1] +
                        (targetPtM[2] - lp[2]) * ln[2]);
                    if (pDist > tolM) continue;

                    double[] fBox = face.GetBox() as double[];
                    bool contained = fBox != null && fBox.Length >= 6 &&
                        targetPtM[0] >= fBox[0] - tolM && targetPtM[0] <= fBox[3] + tolM &&
                        targetPtM[1] >= fBox[1] - tolM && targetPtM[1] <= fBox[4] + tolM &&
                        targetPtM[2] >= fBox[2] - tolM && targetPtM[2] <= fBox[5] + tolM;

                    if (contained)
                    {
                        if (pDist < bestContainedDist - 0.00002 ||
                            (Math.Abs(pDist - bestContainedDist) <= 0.00002 && area > bestContainedArea))
                        {
                            bestContainedDist = pDist;
                            bestContainedArea = area;
                            bestContained = face;
                        }
                    }

                    if (pDist < bestAnyDist - 0.00002 ||
                        (Math.Abs(pDist - bestAnyDist) <= 0.00002 && area > bestAnyArea))
                    {
                        bestAnyDist = pDist;
                        bestAnyArea = area;
                        bestAny = face;
                    }
                }
            }

            return bestContained ?? bestAny;
        }

        public static IEntity FindCylindricalFace(
            IComponent2 comp,
            double[] worldPointMm,
            double[] worldAxis,
            double radiusMm,
            double radiusTolMm = 0.5)
        {
            if (comp == null || worldPointMm == null || worldPointMm.Length < 3 ||
                worldAxis == null || worldAxis.Length < 3)
                return null;
            if (!(comp.GetModelDoc2() is IPartDoc part))
                return null;

            double[] cMat = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : null;
            double[] ptM = { worldPointMm[0] * 0.001, worldPointMm[1] * 0.001, worldPointMm[2] * 0.001 };
            double[] axisU = AssemblyGeometryHelper.Normalize(worldAxis);
            double targetRadM = radiusMm * 0.001;
            double radTolM = Math.Max(0.00005, radiusTolMm * 0.001);

            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null) return null;

            IFace2 best = null;
            double bestScore = double.MaxValue;

            foreach (object bObj in bodies)
            {
                if (!(bObj is IBody2 body)) continue;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    if (!(fObj is IFace2 face)) continue;
                    ISurface surf = face.GetSurface() as ISurface;
                    if (surf == null || !surf.IsCylinder()) continue;

                    double[] cp = surf.CylinderParams as double[];
                    if (cp == null || cp.Length < 7) continue;

                    double rad = cp[6];
                    if (targetRadM > 0.0 && Math.Abs(rad - targetRadM) > radTolM) continue;

                    double[] gAxis = AssemblyGeometryHelper.TransformVectorFast(cMat, cp[3], cp[4], cp[5]);
                    if (Math.Abs(AssemblyGeometryHelper.Dot(gAxis, axisU)) < 0.98) continue;

                    double[] gOrig = AssemblyGeometryHelper.TransformPointFast(cMat, cp[0], cp[1], cp[2]);
                    double dx = ptM[0] - gOrig[0], dy = ptM[1] - gOrig[1], dz = ptM[2] - gOrig[2];
                    double proj = dx * gAxis[0] + dy * gAxis[1] + dz * gAxis[2];
                    double px = dx - proj * gAxis[0], py = dy - proj * gAxis[1], pz = dz - proj * gAxis[2];
                    double perp = Math.Sqrt(px * px + py * py + pz * pz);

                    double[] fBox = face.GetBox() as double[];
                    double axGap = 0.0;
                    if (fBox != null && fBox.Length >= 6)
                    {
                        AssemblyGeometryHelper.TransformAabbFast(cMat, fBox, out double[] bMin, out double[] bMax);
                        double pad = 0.005;
                        if (ptM[0] < bMin[0] - pad) axGap += bMin[0] - ptM[0];
                        else if (ptM[0] > bMax[0] + pad) axGap += ptM[0] - bMax[0];
                        if (ptM[1] < bMin[1] - pad) axGap += bMin[1] - ptM[1];
                        else if (ptM[1] > bMax[1] + pad) axGap += ptM[1] - bMax[1];
                        if (ptM[2] < bMin[2] - pad) axGap += bMin[2] - ptM[2];
                        else if (ptM[2] > bMax[2] + pad) axGap += ptM[2] - bMax[2];
                    }

                    double score = perp * 100.0 + axGap * 10.0 + Math.Abs(rad - targetRadM);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = face;
                    }
                }
            }

            return best != null ? comp.GetCorrespondingEntity(best) as IEntity : null;
        }

        public static int MoveComponentsToFolder(IAssemblyDoc assy, IEnumerable<IComponent2> comps, string folderName = "McMaster Hardware")
        {
            if (assy == null || comps == null) return 0;
            List<IComponent2> list = new List<IComponent2>();
            foreach (IComponent2 c in comps)
                if (c != null) list.Add(c);
            return AssemblyGeometryHelper.MoveComponentsToFolder(assy as IModelDoc2, assy, list, folderName);
        }

        public static int MoveComponentsToFolder(IAssemblyDoc assy, IEnumerable<string> compNames, string folderName = "McMaster Hardware")
        {
            if (assy == null || compNames == null) return 0;
            List<IComponent2> list = AssemblyGeometryHelper.ResolveComponentsMatching(assy, compNames, includeSuppressed: true);
            return AssemblyGeometryHelper.MoveComponentsToFolder(assy as IModelDoc2, assy, list, folderName);
        }

        public static int MoveComponentsToFolder(IAssemblyDoc assy, object[] comps, string folderName = "McMaster Hardware")
        {
            if (assy == null || comps == null) return 0;
            List<IComponent2> list = new List<IComponent2>();
            foreach (object o in comps)
                if (o is IComponent2 c) list.Add(c);
            return AssemblyGeometryHelper.MoveComponentsToFolder(assy as IModelDoc2, assy, list, folderName);
        }

        public static List<IMate2> MateByPrincipalPlanes(
            IAssemblyDoc assy,
            IComponent2 comp,
            IComponent2 targetRefComponentOrRoot = null,
            IMathTransform transform = null)
        {
            if (assy == null) throw new ArgumentNullException("assy");
            if (comp == null) throw new ArgumentNullException("comp");

            IModelDoc2 assyDoc = assy as IModelDoc2;
            if (comp.IsFixed())
            {
                assyDoc.ClearSelection2(true);
                comp.Select4(false, null, false);
                assy.UnfixComponent();
                assyDoc.ClearSelection2(true);
            }

            if (transform != null)
            {
                MathTransform concreteXf = transform as MathTransform;
                if (concreteXf != null && !comp.SetTransformAndSolve3(concreteXf, false))
                    comp.Transform2 = concreteXf;
            }

            double[] compMat = comp.Transform2 != null ? comp.Transform2.ArrayData as double[] : AssemblyGeometryHelper.IdentityTransform16();
            double[] expectedOrigin = { compMat[9], compMat[10], compMat[11] };

            double[] refMat = (targetRefComponentOrRoot != null && targetRefComponentOrRoot.Transform2 != null)
                ? targetRefComponentOrRoot.Transform2.ArrayData as double[]
                : AssemblyGeometryHelper.IdentityTransform16();

            IModelDoc2 compDoc = comp.GetModelDoc2() as IModelDoc2;
            if (compDoc == null)
                throw new InvalidOperationException("Cannot mate principal planes of lightweight/unloaded component: " + comp.Name2);

            IModelDoc2 refDoc = targetRefComponentOrRoot != null
                ? (targetRefComponentOrRoot.GetModelDoc2() as IModelDoc2 ?? assyDoc)
                : assyDoc;

            string[] stdNames = { "Front Plane", "Top Plane", "Right Plane" };
            List<PlaneSpec> compPlanes = ReadPrincipalPlanes(compDoc, comp, compMat, stdNames);
            List<PlaneSpec> refPlanes = ReadPrincipalPlanes(refDoc, targetRefComponentOrRoot, refMat, stdNames);

            ISelectionMgr selMgr = assyDoc.SelectionManager as ISelectionMgr;
            SelectData sd = selMgr != null ? selMgr.CreateSelectData() as SelectData : null;
            if (sd != null) sd.Mark = 1;

            List<IMate2> createdMates = new List<IMate2>();
            List<DistCheckItem> distChecks = new List<DistCheckItem>();
            HashSet<int> usedRef = new HashSet<int>();

            foreach (PlaneSpec cp in compPlanes)
            {
                int matchIdx = -1;
                double bestDot = 0.0;
                for (int j = 0; j < refPlanes.Count; j++)
                {
                    if (usedRef.Contains(j)) continue;
                    double d = Math.Abs(AssemblyGeometryHelper.Dot(cp.WorldNormal, refPlanes[j].WorldNormal));
                    if (d > bestDot)
                    {
                        bestDot = d;
                        matchIdx = j;
                    }
                }

                if (matchIdx < 0 || bestDot < 0.999)
                {
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        "Component '{0}' is not orthogonal to target reference frame (best plane dot={1:F4}); MateByPrincipalPlanes requires axis-aligned orthogonality.",
                        comp.Name2, bestDot));
                }

                usedRef.Add(matchIdx);
                PlaneSpec rp = refPlanes[matchIdx];

                double dot = AssemblyGeometryHelper.Dot(cp.WorldNormal, rp.WorldNormal);
                int align = dot >= 0
                    ? (int)swMateAlign_e.swMateAlignALIGNED
                    : (int)swMateAlign_e.swMateAlignANTI_ALIGNED;

                double[] diff =
                {
                    cp.WorldOrigin[0] - rp.WorldOrigin[0],
                    cp.WorldOrigin[1] - rp.WorldOrigin[1],
                    cp.WorldOrigin[2] - rp.WorldOrigin[2]
                };
                double signedDist = AssemblyGeometryHelper.Dot(diff, rp.WorldNormal);

                int mateType = Math.Abs(signedDist) < 1e-6
                    ? (int)swMateType_e.swMateCOINCIDENT
                    : (int)swMateType_e.swMateDISTANCE;
                bool flip = signedDist < 0.0;
                double distAbs = Math.Abs(signedDist);

                assyDoc.ClearSelection2(true);
                bool s1 = cp.Feature.Select2(false, 1);
                bool s2 = rp.Feature.Select2(true, 1);
                if (!s1 || !s2)
                    throw new InvalidOperationException(string.Format("Failed to select planes '{0}' / '{1}' for MateByPrincipalPlanes.", cp.Name, rp.Name));

                int mErr;
                IMate2 mate = assy.AddMate5(
                    mateType,
                    align,
                    flip,
                    distAbs, distAbs, distAbs,
                    1.0, 1.0,
                    0.0, 0.0, 0.0,
                    false,
                    false,
                    0,
                    out mErr);
                assyDoc.ClearSelection2(true);

                if (mate == null || mErr != (int)swAddMateError_e.swAddMateError_NoError)
                {
                    throw new InvalidOperationException(string.Format(
                        "AddMate5 failed for principal plane pair '{0}' <-> '{1}' with error code {2}.",
                        cp.Name, rp.Name, mErr));
                }

                createdMates.Add(mate);
                if (mateType == (int)swMateType_e.swMateDISTANCE)
                {
                    distChecks.Add(new DistCheckItem
                    {
                        Mate = mate,
                        RefNormal = rp.WorldNormal,
                        AbsDist = distAbs
                    });
                }
            }

            // One fast EditRebuild3 solve and analytical sign-flip verification (never ForceRebuild3)
            assyDoc.EditRebuild3();

            if (distChecks.Count > 0 && comp.Transform2 != null)
            {
                double[] actualMat = comp.Transform2.ArrayData as double[];
                if (actualMat != null && actualMat.Length >= 12)
                {
                    bool flippedAny = false;
                    foreach (DistCheckItem dc in distChecks)
                    {
                        double errAlong =
                            (actualMat[9] - expectedOrigin[0]) * dc.RefNormal[0] +
                            (actualMat[10] - expectedOrigin[1]) * dc.RefNormal[1] +
                            (actualMat[11] - expectedOrigin[2]) * dc.RefNormal[2];
                        if (Math.Abs(errAlong) > Math.Max(0.00005, 0.5 * dc.AbsDist))
                        {
                            IFeature mateFeat = dc.Mate as IFeature;
                            IDistanceMateFeatureData dData = mateFeat != null ? mateFeat.GetDefinition() as IDistanceMateFeatureData : null;
                            if (dData != null)
                            {
                                dData.FlipDimension = !dData.FlipDimension;
                                if (!mateFeat.ModifyDefinition(dData, assyDoc, null) && dc.Mate.CanBeFlipped)
                                    dc.Mate.Flipped = !dc.Mate.Flipped;
                                flippedAny = true;
                            }
                            else if (dc.Mate.CanBeFlipped)
                            {
                                dc.Mate.Flipped = !dc.Mate.Flipped;
                                flippedAny = true;
                            }
                        }
                    }
                    if (flippedAny)
                        assyDoc.EditRebuild3();
                }
            }

            return createdMates;
        }

        private class PlaneSpec
        {
            public string Name;
            public IFeature Feature;
            public double[] WorldOrigin;
            public double[] WorldNormal;
        }

        private class DistCheckItem
        {
            public IMate2 Mate;
            public double[] RefNormal;
            public double AbsDist;
        }

        private static List<PlaneSpec> ReadPrincipalPlanes(IModelDoc2 doc, IComponent2 comp, double[] worldMat, string[] stdNames)
        {
            List<PlaneSpec> list = new List<PlaneSpec>(3);
            double[][] defaultNorms =
            {
                new[] { 0.0, 0.0, 1.0 },
                new[] { 0.0, 1.0, 0.0 },
                new[] { 1.0, 0.0, 0.0 }
            };

            List<IFeature> allRefPlanes = null;
            List<IFeature> allCompRefPlanes = null;
            for (int i = 0; i < 3; i++)
            {
                IFeature compFeat = comp != null ? comp.FeatureByName(stdNames[i]) : null;
                IFeature partFeat = FindRefPlaneFeatureByName(doc, stdNames[i]);
                if (partFeat == null)
                {
                    if (allRefPlanes == null) allRefPlanes = CollectFirstThreeRefPlanes(doc);
                    if (i < allRefPlanes.Count) partFeat = allRefPlanes[i];
                }
                if (comp != null && compFeat == null)
                {
                    if (allCompRefPlanes == null) allCompRefPlanes = CollectFirstThreeCompRefPlanes(comp);
                    if (i < allCompRefPlanes.Count) compFeat = allCompRefPlanes[i];
                }
                if (comp == null && compFeat == null) compFeat = partFeat;
                if (compFeat == null)
                    throw new InvalidOperationException("Could not locate standard reference plane '" + stdNames[i] + "' on " + (comp != null ? comp.Name2 : doc.GetTitle()));

                double[] localOrigin = { 0.0, 0.0, 0.0 };
                double[] localNormal = defaultNorms[i];
                if (partFeat != null && TryGetRefPlaneTransform(partFeat, out double[] ro, out double[] rn))
                {
                    localOrigin = ro;
                    localNormal = rn;
                }

                list.Add(new PlaneSpec
                {
                    Name = compFeat.Name,
                    Feature = compFeat,
                    WorldOrigin = AssemblyGeometryHelper.TransformPointFast(worldMat, localOrigin[0], localOrigin[1], localOrigin[2]),
                    WorldNormal = AssemblyGeometryHelper.TransformVectorFast(worldMat, localNormal[0], localNormal[1], localNormal[2])
                });
            }
            return list;
        }

        private static List<IFeature> CollectFirstThreeCompRefPlanes(IComponent2 comp)
        {
            List<IFeature> list = new List<IFeature>(3);
            if (comp == null) return list;
            IFeature f = comp.FirstFeature() as IFeature;
            int guard = 0;
            while (f != null && guard++ < 30 && list.Count < 3)
            {
                if (string.Equals(f.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase))
                    list.Add(f);
                f = f.GetNextFeature() as IFeature;
            }
            return list;
        }

        private static IFeature FindRefPlaneFeatureByName(IModelDoc2 doc, string name)
        {
            if (doc == null || string.IsNullOrWhiteSpace(name)) return null;
            IFeature f = doc.FirstFeature() as IFeature;
            int guard = 0;
            while (f != null && guard++ < 50)
            {
                if (string.Equals(f.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static List<IFeature> CollectFirstThreeRefPlanes(IModelDoc2 doc)
        {
            List<IFeature> list = new List<IFeature>(3);
            IFeature f = doc.FirstFeature() as IFeature;
            int guard = 0;
            while (f != null && guard++ < 30 && list.Count < 3)
            {
                if (string.Equals(f.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase))
                    list.Add(f);
                f = f.GetNextFeature() as IFeature;
            }
            return list;
        }

        private static void TryGetPlaneOriginAndNormal(IModelDoc2 doc, string selector, out double[] origin, out double[] normal)
        {
            origin = new[] { 0.0, 0.0, 0.0 };
            normal = new[] { 0.0, 0.0, 1.0 };

            string clean = (selector ?? "").Trim();
            if (clean.StartsWith("face:", StringComparison.OrdinalIgnoreCase))
            {
                IFace2 f = SwConnectionManager.FindFaceByToken(doc, clean);
                if (f != null && AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(f, null, out double[] fn, out double[] fp))
                {
                    origin = fp;
                    normal = fn;
                    return;
                }
            }

            IFeature rf = FindRefPlaneFeatureByName(doc, clean);
            if (rf != null && TryGetRefPlaneTransform(rf, out double[] ro, out double[] rn))
            {
                origin = ro;
                normal = rn;
                return;
            }

            string up = clean.ToUpperInvariant();
            if (up.Contains("TOP")) normal = new[] { 0.0, 1.0, 0.0 };
            else if (up.Contains("RIGHT")) normal = new[] { 1.0, 0.0, 0.0 };
            else normal = new[] { 0.0, 0.0, 1.0 };
        }

        private static bool TryGetRefPlaneTransform(IFeature planeFeat, out double[] origin, out double[] normal)
        {
            origin = null;
            normal = null;
            if (planeFeat == null) return false;
            IRefPlane rp = planeFeat.GetSpecificFeature2() as IRefPlane;
            if (rp == null) return false;
            MathTransform xf = rp.Transform;
            double[] d = xf != null ? xf.ArrayData as double[] : null;
            if (d == null || d.Length < 12) return false;
            origin = new[] { d[9], d[10], d[11] };
            normal = AssemblyGeometryHelper.Normalize(new[] { d[6], d[7], d[8] });
            return true;
        }

        public static List<IComponent2> TopComponents(
            IAssemblyDoc assy = null,
            string prefix = null,
            bool includeSuppressed = false)
        {
            assy = assy ?? (_ctxModel as IAssemblyDoc);
            List<IComponent2> list = new List<IComponent2>();
            if (assy == null) return list;

            object[] raw = assy.GetComponents(true) as object[];
            if (raw == null) return list;

            foreach (object co in raw)
            {
                if (!(co is IComponent2 c)) continue;
                if (!includeSuppressed && c.IsSuppressed()) continue;
                if (!string.IsNullOrEmpty(prefix))
                {
                    string n = c.Name2 ?? string.Empty;
                    if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                }
                list.Add(c);
            }
            return list;
        }

        public static MathTransform MakeTransform(
            double[] ex,
            double[] ey,
            double[] ez,
            double[] originMm)
        {
            return MakeTransform(_ctxApp, ex, ey, ez, originMm);
        }

        public static MathTransform MakeTransform(
            ISldWorks app,
            double[] ex,
            double[] ey,
            double[] ez,
            double[] originMm)
        {
            app = app ?? _ctxApp;
            if (app == null) throw new InvalidOperationException("No active ISldWorks instance for MakeTransform.");
            IMathUtility mu = app.GetMathUtility() as IMathUtility;
            if (mu == null) throw new InvalidOperationException("Failed to obtain IMathUtility.");

            if (ex != null && ey != null && ez == null)
                ez = AssemblyGeometryHelper.Cross(ex, ey);
            else if (ex != null && ez != null && ey == null)
                ey = AssemblyGeometryHelper.Cross(ez, ex);
            else if (ey != null && ez != null && ex == null)
                ex = AssemblyGeometryHelper.Cross(ey, ez);

            double[] ux = AssemblyGeometryHelper.Normalize(ex ?? new[] { 1.0, 0.0, 0.0 });
            double[] uz = AssemblyGeometryHelper.Normalize(AssemblyGeometryHelper.Cross(ux, ey ?? new[] { 0.0, 1.0, 0.0 }));
            double[] uy = AssemblyGeometryHelper.Normalize(AssemblyGeometryHelper.Cross(uz, ux));
            double oxM = originMm != null && originMm.Length > 0 ? originMm[0] * 0.001 : 0.0;
            double oyM = originMm != null && originMm.Length > 1 ? originMm[1] * 0.001 : 0.0;
            double ozM = originMm != null && originMm.Length > 2 ? originMm[2] * 0.001 : 0.0;

            return mu.CreateTransform(new[]
            {
                ux[0], ux[1], ux[2],
                uy[0], uy[1], uy[2],
                uz[0], uz[1], uz[2],
                oxM, oyM, ozM,
                1.0, 0.0, 0.0, 0.0
            }) as MathTransform;
        }

        public static IMate2 MateEntities(
            IAssemblyDoc assy,
            IEntity entA,
            IEntity entB,
            int mateType,
            int align = (int)swMateAlign_e.swMateAlignCLOSEST,
            double distMm = 0.0,
            double angleDeg = 0.0,
            bool flip = false,
            bool lockRotation = false)
        {
            assy = assy ?? (_ctxModel as IAssemblyDoc);
            IModelDoc2 doc = assy as IModelDoc2 ?? _ctxModel;
            if (assy == null || doc == null)
                throw new InvalidOperationException("MateEntities requires an active assembly.");
            if (entA == null || entB == null)
                throw new InvalidOperationException("MateEntities requires non-null entA and entB.");

            ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
            SelectData sd = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
            if (sd != null) sd.Mark = 1;

            doc.ClearSelection2(true);
            if (!entA.Select4(false, sd) || !entB.Select4(true, sd))
            {
                doc.ClearSelection2(true);
                throw new InvalidOperationException("MateEntities failed to select one or both entities.");
            }

            double distM = distMm * 0.001;
            double angRad = angleDeg * Math.PI / 180.0;
            int errCode;
            IMate2 mate = assy.AddMate5(
                mateType, align, flip,
                distM, distM, distM,
                1.0, 1.0,
                angRad, angRad, angRad,
                false, lockRotation, 0, out errCode);
            doc.ClearSelection2(true);

            if (mate == null || errCode != (int)swAddMateError_e.swAddMateError_NoError)
            {
                throw new InvalidOperationException(string.Format(
                    "MateEntities AddMate5 failed (error_code={0}, mate_type={1}).", errCode, mateType));
            }
            return mate;
        }

        /// <summary>
        /// Safely draws a closed 2D straight slot using 2 lines and 2 3-point arcs (or a circle when
        /// center-to-center distance collapses to zero). Avoids ISketchManager.CreateSketchSlot, which
        /// hard-crashes SOLIDWORKS 2026 (0x800706BE) whenever center-to-center length is less than slot width.
        /// </summary>
        public static int CreateSlot2D(
            ISketchManager sm,
            double x1,
            double y1,
            double x2,
            double y2,
            double widthM,
            bool fullLength = false,
            bool construction = false)
        {
            if (sm == null)
                throw new ArgumentNullException("sm");
            if (widthM <= 1e-7)
                throw new ArgumentOutOfRangeException("widthM", "Slot width must be positive (in meters).");

            double r = 0.5 * widthM;
            double dx = x2 - x1;
            double dy = y2 - y1;
            double rawLen = Math.Sqrt(dx * dx + dy * dy);

            double c1x, c1y, c2x, c2y, centerLen;
            if (fullLength)
            {
                if (rawLen <= widthM + 1e-7)
                {
                    c1x = c2x = 0.5 * (x1 + x2);
                    c1y = c2y = 0.5 * (y1 + y2);
                    centerLen = 0.0;
                }
                else
                {
                    double uxF = dx / rawLen;
                    double uyF = dy / rawLen;
                    c1x = x1 + uxF * r;
                    c1y = y1 + uyF * r;
                    c2x = x2 - uxF * r;
                    c2y = y2 - uyF * r;
                    centerLen = rawLen - widthM;
                }
            }
            else
            {
                c1x = x1;
                c1y = y1;
                c2x = x2;
                c2y = y2;
                centerLen = rawLen;
            }

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }
            if (!prevDb)
            {
                try { sm.AddToDB = true; } catch { }
            }

            try
            {
                if (centerLen <= 1e-7)
                {
                    ISketchSegment circ = sm.CreateCircleByRadius(0.5 * (c1x + c2x), 0.5 * (c1y + c2y), 0.0, r);
                    if (circ != null && construction) circ.ConstructionGeometry = true;
                    return circ != null ? 1 : 0;
                }

                double ux = (c2x - c1x) / centerLen;
                double uy = (c2y - c1y) / centerLen;
                double nx = -uy;
                double ny = ux;

                double p1MinusX = c1x - r * nx;
                double p1MinusY = c1y - r * ny;
                double p2MinusX = c2x - r * nx;
                double p2MinusY = c2y - r * ny;
                double p2PlusX = c2x + r * nx;
                double p2PlusY = c2y + r * ny;
                double p1PlusX = c1x + r * nx;
                double p1PlusY = c1y + r * ny;

                double m2X = c2x + r * ux;
                double m2Y = c2y + r * uy;
                double m1X = c1x - r * ux;
                double m1Y = c1y - r * uy;

                ISketchSegment[] segs = new ISketchSegment[4];
                segs[0] = sm.CreateLine(p1MinusX, p1MinusY, 0.0, p2MinusX, p2MinusY, 0.0);
                segs[1] = sm.Create3PointArc(p2MinusX, p2MinusY, 0.0, p2PlusX, p2PlusY, 0.0, m2X, m2Y, 0.0);
                segs[2] = sm.CreateLine(p2PlusX, p2PlusY, 0.0, p1PlusX, p1PlusY, 0.0);
                segs[3] = sm.Create3PointArc(p1PlusX, p1PlusY, 0.0, p1MinusX, p1MinusY, 0.0, m1X, m1Y, 0.0);

                int count = 0;
                foreach (ISketchSegment seg in segs)
                {
                    if (seg != null)
                    {
                        if (construction) seg.ConstructionGeometry = true;
                        count++;
                    }
                }
                return count;
            }
            finally
            {
                if (!prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }
            }
        }

        public static string GetConstrainedStatusLabel(int status)
        {
            switch (status)
            {
                case (int)swConstrainedStatus_e.swFullyConstrained:
                    return "FullyConstrained";
                case (int)swConstrainedStatus_e.swUnderConstrained:
                    return "UnderConstrained";
                case (int)swConstrainedStatus_e.swOverConstrained:
                    return "OverConstrained";
                case (int)swConstrainedStatus_e.swNoSolution:
                    return "NoSolution";
                case (int)swConstrainedStatus_e.swInvalidSolution:
                    return "InvalidSolution";
                case (int)swConstrainedStatus_e.swAutosolveOff:
                    return "AutosolveOff";
                default:
                    return "Unknown";
            }
        }

        /// <summary>
        /// Automatically runs SOLIDWORKS FullyDefineSketch on the active 2D sketch (anchored to Origin),
        /// disabling AddToDB while solving and optionally applying sgFIXED as a last resort for
        /// non-analytic entities (splines/text) so no unintended (-) under-defined flags remain.
        /// </summary>
        public static int FullyDefineActiveSketch(
            IModelDoc2 doc = null,
            bool useRelations = true,
            bool useDimensions = true,
            bool fixRemainingSplines = true)
        {
            doc = doc ?? _ctxModel;
            if (doc == null) return -1;

            ISketchManager sm = doc.SketchManager;
            ISketch activeSketch = sm != null ? sm.ActiveSketch : null;
            if (activeSketch == null) return -1;

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }

            try
            {
                if (prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                int status = activeSketch.GetConstrainedStatus();
                if (status != (int)swConstrainedStatus_e.swFullyConstrained)
                {
                    object datum = null;
                    try
                    {
                        doc.ClearSelection2(true);
                        if (SwConnectionManager.TrySelectOriginPoint(doc, false, 6))
                        {
                            ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                            if (selMgr != null && selMgr.GetSelectedObjectCount2(-1) > 0)
                                datum = selMgr.GetSelectedObject6(1, -1);
                        }
                        doc.ClearSelection2(true);
                    }
                    catch { datum = null; }

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
                            true,
                            useRelations,
                            useRelations ? allRelMask : 0,
                            useDimensions,
                            (int)swAutodimScheme_e.swAutodimSchemeBaseline,
                            datum,
                            (int)swAutodimScheme_e.swAutodimSchemeBaseline,
                            datum,
                            (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementAbove,
                            (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementRight);
                    }
                    catch { }

                    doc.ClearSelection2(true);
                    status = activeSketch.GetConstrainedStatus();

                    if (status != (int)swConstrainedStatus_e.swFullyConstrained && datum != null)
                    {
                        try
                        {
                            sm.FullyDefineSketch(
                                true,
                                useRelations,
                                useRelations ? allRelMask : 0,
                                useDimensions,
                                (int)swAutodimScheme_e.swAutodimSchemeBaseline,
                                null,
                                (int)swAutodimScheme_e.swAutodimSchemeBaseline,
                                null,
                                (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementAbove,
                                (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementRight);
                        }
                        catch { }

                        doc.ClearSelection2(true);
                        status = activeSketch.GetConstrainedStatus();
                    }

                    if (status == (int)swConstrainedStatus_e.swUnderConstrained && fixRemainingSplines)
                    {
                        try
                        {
                            doc.ClearSelection2(true);
                            int selCount = 0;
                            object[] segs = activeSketch.GetSketchSegments() as object[];
                            if (segs != null)
                            {
                                foreach (object sObj in segs)
                                {
                                    if (sObj is ISketchSegment seg)
                                    {
                                        if (seg.Select4(selCount > 0, null)) selCount++;
                                    }
                                }
                            }
                            if (selCount > 0)
                            {
                                doc.SketchAddConstraints("sgFIXED");
                                doc.ClearSelection2(true);
                                status = activeSketch.GetConstrainedStatus();
                            }

                            if (status == (int)swConstrainedStatus_e.swUnderConstrained)
                            {
                                object[] pts = activeSketch.GetSketchPoints2() as object[];
                                selCount = 0;
                                if (pts != null)
                                {
                                    foreach (object pObj in pts)
                                    {
                                        if (pObj is ISketchPoint pt)
                                        {
                                            if (pt.Select4(selCount > 0, null)) selCount++;
                                        }
                                    }
                                }
                                if (selCount > 0)
                                {
                                    doc.SketchAddConstraints("sgFIXED");
                                    doc.ClearSelection2(true);
                                    status = activeSketch.GetConstrainedStatus();
                                }
                            }
                        }
                        catch { }
                    }
                }

                return status;
            }
            finally
            {
                if (prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        public static bool SelectSketchObject(IModelDoc2 doc, object entity, bool append = false, int mark = 0)
        {
            if (doc == null || entity == null) return false;

            ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
            SelectData sd = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
            if (sd != null) sd.Mark = mark;

            if (entity is ISketchSegment seg)
                return seg.Select4(append, sd);
            if (entity is ISketchPoint pt)
                return pt.Select4(append, sd);
            if (entity is IEntity ent)
                return ent.Select4(append, sd);
            if (entity is IFeature feat)
                return feat.Select2(append, mark);

            if (entity is string tok)
            {
                string clean = tok.Trim();
                if (string.Equals(clean, "Origin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clean, "Point1@Origin", StringComparison.OrdinalIgnoreCase))
                {
                    if (SwConnectionManager.TrySelectOriginPoint(doc, append, mark))
                        return true;
                }
                if (doc is IAssemblyDoc assy)
                {
                    if (AssemblyGeometryHelper.TrySelectAssemblyTarget(doc, assy, clean, append, mark))
                        return true;
                }
                if (SwConnectionManager.TrySelectByToken(doc, clean, append, mark))
                    return true;

                string[] extraTypes = { "SKETCHPOINT", "SKETCHSEGMENT", "EXTSKETCHPOINT", "EXTSKETCHSEGMENT", "PLANE", "AXIS", "FACE", "EDGE", "" };
                foreach (string t in extraTypes)
                {
                    if (doc.Extension.SelectByID2(clean, t, 0, 0, 0, append, mark, null, 0))
                        return true;
                }
            }

            return false;
        }

        public static string NormalizeSketchRelationName(string relation)
        {
            if (string.IsNullOrWhiteSpace(relation)) return string.Empty;
            string r = relation.Trim();
            if (r.StartsWith("sg", StringComparison.OrdinalIgnoreCase))
                return r;

            switch (r.ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
            {
                case "coincident": return "sgCOINCIDENT";
                case "concentric": return "sgCONCENTRIC";
                case "coradial": return "sgCORADIAL";
                case "colinear":
                case "collinear": return "sgCOLINEAR";
                case "parallel": return "sgPARALLEL";
                case "perpendicular": return "sgPERPENDICULAR";
                case "tangent": return "sgTANGENT";
                case "equal":
                case "same_length":
                case "equal_length":
                case "equal_radius": return "sgSAMELENGTH";
                case "symmetric":
                case "symmetry": return "sgSYMMETRIC";
                case "midpoint":
                case "at_middle": return "sgATMIDDLE";
                case "horizontal":
                case "horiz": return "sgHORIZ2D";
                case "vertical":
                case "vert": return "sgVERT2D";
                case "fix":
                case "fixed": return "sgFIXED";
                default: return r;
            }
        }

        public static string AddSketchDimension(
            IModelDoc2 doc,
            object entityA,
            object entityB = null,
            string dimType = "linear",
            double? valueMetersOrRad = null,
            string name = null,
            string expression = null,
            double textX = 0.015,
            double textY = 0.015)
        {
            doc = doc ?? _ctxModel;
            if (doc == null || entityA == null) return null;

            ISketchManager sm = doc.SketchManager;
            ISketch activeSketch = sm != null ? sm.ActiveSketch : null;
            bool prevDb = false;
            try { if (sm != null) prevDb = sm.AddToDB; } catch { }

            try
            {
                if (sm != null && prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                doc.ClearSelection2(true);
                if (!SelectSketchObject(doc, entityA, false, 0))
                {
                    doc.ClearSelection2(true);
                    return null;
                }
                if (entityB != null && !SelectSketchObject(doc, entityB, true, 0))
                {
                    doc.ClearSelection2(true);
                    return null;
                }

                string kind = (dimType ?? "linear").Trim().ToLowerInvariant();
                object rawDisp = null;
                if (kind == "horizontal" || kind == "horiz")
                    rawDisp = doc.AddHorizontalDimension2(textX, textY, 0.0);
                else if (kind == "vertical" || kind == "vert")
                    rawDisp = doc.AddVerticalDimension2(textX, textY, 0.0);
                else if (kind == "diameter" || kind == "dia")
                    rawDisp = doc.AddDiameterDimension2(textX, textY, 0.0);
                else if (kind == "radius" || kind == "rad")
                    rawDisp = doc.AddRadialDimension2(textX, textY, 0.0);
                else
                    rawDisp = doc.AddDimension2(textX, textY, 0.0);

                if (rawDisp == null && kind != "linear")
                    rawDisp = doc.AddDimension2(textX, textY, 0.0);

                IDisplayDimension dispDim = rawDisp as IDisplayDimension;
                if (dispDim == null)
                {
                    doc.ClearSelection2(true);
                    return null;
                }

                IDimension dim = dispDim.GetDimension2(0) as IDimension;
                if (dim == null)
                {
                    doc.ClearSelection2(true);
                    return null;
                }

                if (valueMetersOrRad.HasValue && string.IsNullOrWhiteSpace(expression))
                {
                    try
                    {
                        dim.SetSystemValue3(
                            valueMetersOrRad.Value,
                            (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,
                            null);
                    }
                    catch { }
                }

                IFeature skFeat = activeSketch as IFeature;
                string skName = skFeat != null ? skFeat.Name : "Sketch1";

                if (!string.IsNullOrWhiteSpace(name))
                {
                    try { dim.Name = name.Trim(); } catch { }
                }

                string fullRef = dim.Name + "@" + skName;
                if (!string.IsNullOrWhiteSpace(expression))
                {
                    string rhs = expression.Trim();
                    if (rhs.StartsWith("=", StringComparison.Ordinal))
                        rhs = rhs.Substring(1).Trim();
                    if (!string.IsNullOrEmpty(rhs))
                        UpsertDimensionEquation(doc, fullRef, rhs);
                }

                doc.ClearSelection2(true);
                return fullRef;
            }
            finally
            {
                if (sm != null && prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        public static bool UpsertDimensionEquation(IModelDoc2 doc, string lhsDimRef, string rhsExpression)
        {
            doc = doc ?? _ctxModel;
            if (doc == null || string.IsNullOrWhiteSpace(lhsDimRef) || string.IsNullOrWhiteSpace(rhsExpression))
                return false;

            IEquationMgr eqMgr = doc.GetEquationMgr() as IEquationMgr;
            if (eqMgr == null) return false;

            string lhsNorm = lhsDimRef.Trim();
            string rhsNorm = rhsExpression.Trim();
            if (rhsNorm.StartsWith("=", StringComparison.Ordinal))
                rhsNorm = rhsNorm.Substring(1).Trim();

            string eqLine = "\"" + lhsNorm + "\" = " + rhsNorm;
            string prefix = "\"" + lhsNorm + "\"";

            int count = eqMgr.GetCount();
            for (int i = 0; i < count; i++)
            {
                string existing = null;
                try { existing = eqMgr.get_Equation(i); } catch { }
                if (!string.IsNullOrWhiteSpace(existing) &&
                    existing.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    eqMgr.set_Equation(i, eqLine);
                    try { eqMgr.EvaluateAll(); } catch { }
                    return true;
                }
            }

            int added = eqMgr.Add3(-1, eqLine, true, (int)swInConfigurationOpts_e.swAllConfiguration, null);
            if (added < 0)
                added = eqMgr.Add2(-1, eqLine, true);
            try { eqMgr.EvaluateAll(); } catch { }
            return added >= 0;
        }

        /// <summary>
        /// Opens Sub-Sketch 1 (the Hole Wizard location point sketch) of an existing Hole Wizard feature,
        /// invokes optional configuration callback on its user ISketchPoints, auto-constrains remaining DOFs,
        /// closes the sketch, and rebuilds.
        /// </summary>
        public static int EditHoleWizardLocations(
            IModelDoc2 doc,
            IFeature holeWizardFeat,
            Action<ISketch, IList<ISketchPoint>> configurePoints = null,
            bool autoFullyDefine = true)
        {
            doc = doc ?? _ctxModel;
            if (doc == null || holeWizardFeat == null) return -1;

            IFeature posSketchFeat = null;
            IFeature sub = holeWizardFeat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                if (string.Equals(sub.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                {
                    posSketchFeat = sub;
                    break;
                }
                sub = sub.GetNextSubFeature() as IFeature;
            }
            if (posSketchFeat == null) return -1;

            doc.ClearSelection2(true);
            if (!posSketchFeat.Select2(false, 0))
                return -1;
            doc.EditSketch();

            ISketchManager sm = doc.SketchManager;
            ISketch activeSketch = sm != null ? sm.ActiveSketch : null;
            if (activeSketch == null) return -1;

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }
            try
            {
                if (prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                List<ISketchPoint> pts = new List<ISketchPoint>();
                object[] rawPts = activeSketch.GetSketchPoints2() as object[];
                if (rawPts != null)
                {
                    foreach (object pObj in rawPts)
                    {
                        if (pObj is ISketchPoint sp)
                            pts.Add(sp);
                    }
                }

                if (configurePoints != null)
                    configurePoints(activeSketch, pts);

                int status = activeSketch.GetConstrainedStatus();
                if (autoFullyDefine && status != (int)swConstrainedStatus_e.swFullyConstrained)
                    status = FullyDefineActiveSketch(doc);

                doc.ClearSelection2(true);
                sm.InsertSketch(true);
                doc.EditRebuild3();
                return status;
            }
            finally
            {
                if (prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        public static bool BeginEditPartInAssembly(
            IAssemblyDoc assy,
            string componentName,
            string planeOrFaceToken = null,
            string sketchName = null)
        {
            assy = assy ?? (_ctxModel as IAssemblyDoc);
            IModelDoc2 assyDoc = assy as IModelDoc2;
            if (assy == null || assyDoc == null || string.IsNullOrWhiteSpace(componentName))
                return false;

            IComponent2 comp = AssemblyGeometryHelper.ResolveComponentByPath(assy, componentName);
            if (comp == null) return false;

            assyDoc.ClearSelection2(true);
            if (!comp.Select4(false, null, false))
                return false;

            int info = -1;
            int status = assy.EditPart2(true, false, ref info);
            assyDoc.ClearSelection2(true);

            bool edited = status == (int)swEditPartCommandStatus_e.swEditPartSuccessful;
            if (!edited) return false;

            if (!string.IsNullOrWhiteSpace(sketchName))
            {
                IFeature skFeat = FindComponentFeatureRecursive(comp, sketchName.Trim());
                if (skFeat == null)
                    skFeat = FindDocFeatureRecursive(assyDoc, sketchName.Trim());
                if (skFeat != null && skFeat.Select2(false, 0))
                {
                    assyDoc.EditSketch();
                    return assyDoc.SketchManager.ActiveSketch != null;
                }
                return false;
            }

            if (!string.IsNullOrWhiteSpace(planeOrFaceToken))
            {
                string tok = planeOrFaceToken.Trim();
                bool selected = false;
                if (tok.StartsWith("face:", StringComparison.OrdinalIgnoreCase) && comp.GetModelDoc2() is IModelDoc2 compDoc)
                {
                    IFace2 f = SwConnectionManager.FindFaceByToken(compDoc, tok);
                    if (f != null && comp.GetCorrespondingEntity(f) is IEntity fEnt)
                        selected = fEnt.Select4(false, null);
                }
                if (!selected)
                {
                    IFeature compPlane = comp.FeatureByName(tok);
                    if (compPlane != null)
                        selected = compPlane.Select2(false, 0);
                }
                if (!selected)
                    selected = SwConnectionManager.TrySelectByToken(assyDoc, tok, false, 0);

                if (selected)
                {
                    assyDoc.SketchManager.InsertSketch(true);
                    return assyDoc.SketchManager.ActiveSketch != null;
                }
            }

            return true;
        }

        public static bool AddInContextRelation(
            IModelDoc2 assyDoc,
            object localSketchEntityOrPoint,
            object externalEntityOrSpec,
            string relation = "sgCONCENTRIC",
            bool replaceConflictingDimensions = false)
        {
            assyDoc = assyDoc ?? _ctxModel;
            if (assyDoc == null || localSketchEntityOrPoint == null || externalEntityOrSpec == null)
                return false;

            ISketchManager sm = assyDoc.SketchManager;
            ISketch activeSketch = sm != null ? sm.ActiveSketch : null;
            if (activeSketch == null) return false;

            string relConst = NormalizeSketchRelationName(relation);
            if (string.IsNullOrEmpty(relConst)) relConst = "sgCONCENTRIC";

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }
            try
            {
                if (prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                if (replaceConflictingDimensions)
                {
                    DeleteLocalSketchDimensionsOnActiveSketch(assyDoc, activeSketch);
                }

                assyDoc.ClearSelection2(true);
                if (!SelectSketchObject(assyDoc, localSketchEntityOrPoint, false, 0))
                {
                    assyDoc.ClearSelection2(true);
                    return false;
                }
                if (!SelectSketchObject(assyDoc, externalEntityOrSpec, true, 0))
                {
                    assyDoc.ClearSelection2(true);
                    return false;
                }

                assyDoc.SketchAddConstraints(relConst);
                assyDoc.ClearSelection2(true);

                int st = activeSketch.GetConstrainedStatus();
                if (replaceConflictingDimensions && st == (int)swConstrainedStatus_e.swOverConstrained)
                {
                    DeleteLocalSketchDimensionsOnActiveSketch(assyDoc, activeSketch);
                }

                return true;
            }
            finally
            {
                if (prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        public static bool ConvertEntitiesInContext(
            IModelDoc2 assyDoc,
            IList<object> externalEntities,
            double offsetMm = 0.0,
            bool flipOffset = false,
            bool capEnds = false,
            bool construction = false)
        {
            assyDoc = assyDoc ?? _ctxModel;
            if (assyDoc == null || externalEntities == null || externalEntities.Count == 0)
                return false;

            ISketchManager sm = assyDoc.SketchManager;
            if (sm == null || sm.ActiveSketch == null) return false;

            bool prevDb = false;
            try { prevDb = sm.AddToDB; } catch { }
            try
            {
                if (prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                assyDoc.ClearSelection2(true);
                int selCount = 0;
                foreach (object item in externalEntities)
                {
                    if (SelectSketchObject(assyDoc, item, selCount > 0, 0))
                        selCount++;
                }
                if (selCount == 0) return false;

                bool ok;
                if (Math.Abs(offsetMm) > 1e-6)
                {
                    double offM = (flipOffset ? -offsetMm : offsetMm) * 0.001;
                    ok = sm.SketchOffset(offM, false, false, capEnds, construction, true);
                }
                else
                {
                    ISketch activeSk = sm.ActiveSketch;
                    int preSegs = 0;
                    if (construction && activeSk != null)
                    {
                        object[] beforeArr = activeSk.GetSketchSegments() as object[];
                        preSegs = beforeArr != null ? beforeArr.Length : 0;
                    }
                    ok = sm.SketchUseEdge3(false, false);
                    if (ok && construction && activeSk != null)
                    {
                        object[] afterArr = activeSk.GetSketchSegments() as object[];
                        if (afterArr != null)
                        {
                            for (int i = preSegs; i < afterArr.Length; i++)
                            {
                                if (afterArr[i] is ISketchSegment seg)
                                    seg.ConstructionGeometry = true;
                            }
                        }
                    }
                }

                assyDoc.ClearSelection2(true);
                return ok;
            }
            finally
            {
                if (prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        public static void EndEditPartInAssembly(IAssemblyDoc assy = null, bool autoFullyDefine = true)
        {
            IModelDoc2 assyDoc = (assy as IModelDoc2) ?? _ctxModel;
            assy = assy ?? (assyDoc as IAssemblyDoc);

            try
            {
                if (assyDoc != null && assyDoc.SketchManager != null && assyDoc.SketchManager.ActiveSketch != null)
                {
                    if (autoFullyDefine)
                        FullyDefineActiveSketch(assyDoc);
                    assyDoc.ClearSelection2(true);
                    assyDoc.SketchManager.InsertSketch(true);
                }
            }
            catch { }

            if (assy != null)
            {
                try { assy.EditAssembly(); } catch { }
            }
            if (assyDoc != null)
            {
                try { assyDoc.EditRebuild3(); } catch { }
            }
        }

        public static Dictionary<string, object> BuildSketchHealthSummary(IModelDoc2 doc = null)
        {
            doc = doc ?? _ctxModel;
            List<string> underDefined = new List<string>();
            List<string> overDefined = new List<string>();
            int total = 0;
            int fullyDefined = 0;

            if (doc != null)
            {
                foreach (IFeature skFeat in EnumerateSketchFeatures(doc))
                {
                    try
                    {
                        if (skFeat.IsSuppressed()) continue;
                        ISketch sk = skFeat.GetSpecificFeature2() as ISketch;
                        if (sk == null) continue;
                        total++;
                        int status = sk.GetConstrainedStatus();
                        string skName = skFeat.Name ?? "Sketch";
                        if (status == (int)swConstrainedStatus_e.swFullyConstrained)
                            fullyDefined++;
                        else if (status == (int)swConstrainedStatus_e.swUnderConstrained)
                            underDefined.Add(skName);
                        else if (status == (int)swConstrainedStatus_e.swOverConstrained ||
                                 status == (int)swConstrainedStatus_e.swNoSolution ||
                                 status == (int)swConstrainedStatus_e.swInvalidSolution)
                            overDefined.Add(skName);
                    }
                    catch { }
                }
            }

            return new Dictionary<string, object>
            {
                { "total_sketches", total },
                { "fully_defined_count", fullyDefined },
                { "under_defined_count", underDefined.Count },
                { "over_defined_count", overDefined.Count },
                { "under_defined_sketches", underDefined },
                { "over_defined_sketches", overDefined },
                { "clean", underDefined.Count == 0 && overDefined.Count == 0 }
            };
        }

        private static void DeleteLocalSketchDimensionsOnActiveSketch(IModelDoc2 doc, ISketch activeSketch)
        {
            if (doc == null || !(activeSketch is IFeature skFeat)) return;
            try
            {
                List<IAnnotation> annots = new List<IAnnotation>();
                IDisplayDimension dd = skFeat.GetFirstDisplayDimension() as IDisplayDimension;
                while (dd != null)
                {
                    try
                    {
                        IAnnotation ann = dd.GetAnnotation() as IAnnotation;
                        if (ann != null) annots.Add(ann);
                    }
                    catch { }
                    try { dd = skFeat.GetNextDisplayDimension(dd) as IDisplayDimension; }
                    catch { dd = null; }
                }
                if (annots.Count == 0) return;

                doc.ClearSelection2(true);
                int selCount = 0;
                foreach (IAnnotation ann in annots)
                {
                    try
                    {
                        if (ann.Select3(selCount > 0, null))
                            selCount++;
                    }
                    catch { }
                }
                if (selCount > 0)
                    doc.Extension.DeleteSelection2(0);
                doc.ClearSelection2(true);
            }
            catch { }
        }

        private static IFeature FindComponentFeatureRecursive(IComponent2 comp, string featName)
        {
            if (comp == null || string.IsNullOrWhiteSpace(featName)) return null;
            IFeature direct = comp.FeatureByName(featName);
            if (direct != null) return direct;

            IFeature f = comp.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.Name, featName, StringComparison.OrdinalIgnoreCase))
                    return f;
                IFeature sub = f.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    if (string.Equals(sub.Name, featName, StringComparison.OrdinalIgnoreCase))
                        return sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        private static IFeature FindDocFeatureRecursive(IModelDoc2 doc, string featName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(featName)) return null;
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.Name, featName, StringComparison.OrdinalIgnoreCase))
                    return f;
                IFeature sub = f.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    if (string.Equals(sub.Name, featName, StringComparison.OrdinalIgnoreCase))
                        return sub;
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return null;
        }

        public static bool SaveSilent(IModelDoc2 doc = null)
        {
            doc = doc ?? _ctxModel;
            if (doc == null) return false;
            int errs = 0, warns = 0;
            return doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errs, ref warns);
        }
    }
}
