using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    public class MassStats
    {
        public double MassKg { get; set; }
        public double VolumeM3 { get; set; }
        public double SurfaceAreaM2 { get; set; }
        public double[] CenterOfMassM { get; set; }
    }

    public class SwConnectionManager
    {
        private ISldWorks _swApp;

        public ISldWorks GetApp(bool launchIfNotRunning = false)
        {
            if (_swApp != null)
            {
                try
                {
                    // Probe live RPC connectivity
                    string rev = _swApp.RevisionNumber();
                    if (!string.IsNullOrEmpty(rev))
                        return _swApp;
                }
                catch
                {
                    _swApp = null;
                }
            }

            try
            {
                object active = Marshal.GetActiveObject("SldWorks.Application");
                _swApp = active as ISldWorks;
                if (_swApp != null)
                    return _swApp;
            }
            catch (COMException)
            {
                // Not currently running in ROT
            }

            if (launchIfNotRunning)
            {
                Process[] running = Process.GetProcessesByName("SLDWORKS");
                if (running.Length > 0)
                {
                    ISldWorks attached = PollRotForApp(15);
                    if (attached != null)
                        return attached;

                    string pidList = string.Join(", ", Array.ConvertAll(running, p => p.Id.ToString(CultureInfo.InvariantCulture)));
                    throw new InvalidOperationException(
                        string.Format("SLDWORKS.exe is already running (PID: {0}) but its COM interface is not registered in the ROT yet. It may still be loading add-ins, blocked on a startup/crash-recovery dialog, or running elevated (As Administrator).", pidList));
                }

                ThreeDExperienceLaunchResult launch = LaunchOrRestartVia3DExperience(false, false, 90);
                if (_swApp != null)
                    return _swApp;

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(launch.StatusMessage)
                        ? "Launched SOLIDWORKS via 3DEXPERIENCE in Microsoft Edge. Wait ~30-60s and call sw_connect_status again."
                        : launch.StatusMessage);
            }

            throw new InvalidOperationException("SOLIDWORKS is not running in this desktop session. Open SOLIDWORKS or pass launch_if_closed=true.");
        }

        public ThreeDExperienceLaunchResult LaunchOrRestartVia3DExperience(
            bool restartIfRunning,
            bool forceRestart,
            int waitTimeoutSeconds)
        {
            if (restartIfRunning)
                _swApp = null;

            ThreeDExperienceLaunchResult result = ThreeDExperienceEdgeLauncher.LaunchOrRestartViaEdge(
                restartIfRunning,
                forceRestart,
                waitTimeoutSeconds);

            if (result.Connected)
            {
                try
                {
                    object active = Marshal.GetActiveObject("SldWorks.Application");
                    ISldWorks attached = active as ISldWorks;
                    if (attached != null)
                    {
                        _swApp = attached;
                        try { _swApp.Visible = true; } catch { }
                    }
                }
                catch { }
            }

            return result;
        }

        private ISldWorks PollRotForApp(int maxSeconds)
        {
            for (int i = 0; i < maxSeconds; i++)
            {
                try
                {
                    object active = Marshal.GetActiveObject("SldWorks.Application");
                    ISldWorks candidate = active as ISldWorks;
                    if (candidate != null)
                    {
                        _swApp = candidate;
                        return _swApp;
                    }
                }
                catch (COMException)
                {
                    // Still booting or dialog open
                }
                Thread.Sleep(1000);
            }
            return null;
        }

        public bool TryAttachOnly(out ISldWorks app)
        {
            app = null;
            try
            {
                app = GetApp(false);
                return app != null;
            }
            catch
            {
                return false;
            }
        }

        public IModelDoc2 RequireModel(string targetDoc = null, bool launchIfNotRunning = false)
        {
            ISldWorks app = GetApp(launchIfNotRunning);
            IModelDoc2 model = null;

            if (!string.IsNullOrWhiteSpace(targetDoc))
            {
                IModelDoc2 active = app.ActiveDoc as IModelDoc2;
                if (active != null && MatchesDocumentIdentifier(active, targetDoc))
                {
                    model = active;
                }
                else
                {
                    object[] docs = app.GetDocuments() as object[];
                    if (docs != null)
                    {
                        foreach (object dObj in docs)
                        {
                            if (dObj is IModelDoc2 candidate && MatchesDocumentIdentifier(candidate, targetDoc))
                            {
                                model = candidate;
                                ActivateDocumentSafe(app, candidate);
                                break;
                            }
                        }
                    }
                }
                if (model == null && File.Exists(targetDoc))
                {
                    model = OpenOrImport(app, targetDoc, out _);
                }
            }
            else
            {
                model = app.ActiveDoc as IModelDoc2;
            }

            if (model == null)
                throw new InvalidOperationException("No active SOLIDWORKS document found.");
            return model;
        }

        public static bool MatchesDocumentIdentifier(IModelDoc2 candidate, string identifier)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(identifier))
                return false;
            string clean = identifier.Trim();
            string path = candidate.GetPathName() ?? string.Empty;
            string title = candidate.GetTitle() ?? string.Empty;
            return string.Equals(path, clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileName(path), clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(title, clean, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(path) &&
                    string.Equals(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(clean), StringComparison.OrdinalIgnoreCase));
        }

        public static bool ActivateDocumentSafe(ISldWorks app, IModelDoc2 doc)
        {
            if (app == null || doc == null) return false;
            IModelDoc2 active = null;
            try { active = app.ActiveDoc as IModelDoc2; } catch { }
            if (active != null)
            {
                string actPath = active.GetPathName() ?? string.Empty;
                string docPath = doc.GetPathName() ?? string.Empty;
                if (!string.IsNullOrEmpty(actPath) && !string.IsNullOrEmpty(docPath) &&
                    string.Equals(actPath, docPath, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(active.GetTitle(), doc.GetTitle(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            int errors = 0;
            object activated = app.ActivateDoc3(
                doc.GetTitle(),
                false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors);
            return activated != null || errors == 0;
        }

        public static T RunWithModalGuard<T>(ISldWorks app, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (app == null) return action();

            bool prevCmd = false;
            bool hasCmd = false;
            try { prevCmd = app.CommandInProgress; app.CommandInProgress = true; hasCmd = true; } catch { }

            bool prevExtRef = false, hasExtRef = false;
            bool prevShowErr = true, hasShowErr = false;
            bool prevAutoFlip = false, hasAutoFlip = false;

            try
            {
                prevExtRef = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave);
                hasExtRef = true;
                if (!prevExtRef)
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, true);
            }
            catch { }

            try
            {
                prevShowErr = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild);
                hasShowErr = true;
                if (prevShowErr)
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, false);
            }
            catch { }

            try
            {
                prevAutoFlip = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swPromptForAutoMateFlip);
                hasAutoFlip = true;
                if (prevAutoFlip)
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swPromptForAutoMateFlip, false);
            }
            catch { }

            try
            {
                return action();
            }
            finally
            {
                if (hasAutoFlip) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swPromptForAutoMateFlip, prevAutoFlip); } catch { } }
                if (hasShowErr) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, prevShowErr); } catch { } }
                if (hasExtRef) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, prevExtRef); } catch { } }
                if (hasCmd) { try { app.CommandInProgress = prevCmd; } catch { } }
            }
        }

        public IModelDoc2 OpenOrImport(ISldWorks app, string filePath, out string warnings)
        {
            warnings = null;
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found: " + filePath);

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            int errors = 0;
            int warns = 0;

            if (ext == ".sldprt" || ext == ".sldasm" || ext == ".slddrw")
            {
                int docType = (int)swDocumentTypes_e.swDocPART;
                if (ext == ".sldasm") docType = (int)swDocumentTypes_e.swDocASSEMBLY;
                else if (ext == ".slddrw") docType = (int)swDocumentTypes_e.swDocDRAWING;

                IModelDoc2 doc = app.OpenDoc6(filePath, docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warns);
                if (doc == null)
                    throw new InvalidOperationException("OpenDoc6 failed with error code " + errors);
                if (warns != 0)
                    warnings = "OpenDoc6 warnings code: " + warns;
                return doc;
            }

            // STEP / IGES / Parasolid / Neutral CAD import
            IModelDoc2 imported = app.LoadFile4(filePath, "", null, ref errors);
            if (imported == null)
                throw new InvalidOperationException("LoadFile4 import failed for '" + filePath + "' with error code " + errors);
            return imported;
        }

        public static string SetDocumentUnitSystem(IModelDoc2 model, string domainOrUnitSystem)
        {
            string sys = ShopCatalogService.GetPreferredUnitSystemForDomain(domainOrUnitSystem);
            int unitEnum = sys == "MMGS"
                ? (int)swUnitSystem_e.swUnitSystem_MMGS
                : (int)swUnitSystem_e.swUnitSystem_IPS;

            model.Extension.SetUserPreferenceInteger(
                (int)swUserPreferenceIntegerValue_e.swUnitSystem,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified,
                unitEnum);

            return sys;
        }

        public static string GetDocumentUnits(IModelDoc2 model)
        {
            if (model == null) return "in";
            int sys = model.Extension.GetUserPreferenceInteger(
                (int)swUserPreferenceIntegerValue_e.swUnitSystem,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);

            if (sys == (int)swUnitSystem_e.swUnitSystem_MMGS)
                return "mm";
            if (sys == (int)swUnitSystem_e.swUnitSystem_MKS)
                return "m";
            if (sys == (int)swUnitSystem_e.swUnitSystem_IPS)
                return "in";

            // Custom unit system: inspect length unit
            int lenUnit = model.Extension.GetUserPreferenceInteger(
                (int)swUserPreferenceIntegerValue_e.swUnitsLinear,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            if (lenUnit == (int)swLengthUnit_e.swMM) return "mm";
            if (lenUnit == (int)swLengthUnit_e.swMETER) return "m";
            return "in";
        }

        public static string ResolveUnits(IModelDoc2 model, string requestedUnits)
        {
            if (!string.IsNullOrWhiteSpace(requestedUnits))
                return ShopCatalogService.NormalizeUnitsLabel(requestedUnits);
            return GetDocumentUnits(model);
        }

        public T ExecuteInUndoTransaction<T>(IModelDoc2 model, string undoTitle, bool rollbackOnError, Func<T> action)
        {
            if (model == null)
                return action();

            bool recording = false;
            try
            {
                model.Extension.StartRecordingUndoObject();
                recording = true;
            }
            catch
            {
                recording = false;
            }

            try
            {
                T result = action();
                if (recording)
                {
                    try { model.Extension.FinishRecordingUndoObject2(undoTitle ?? "MCP Operation", false); } catch { }
                }
                return result;
            }
            catch
            {
                if (recording)
                {
                    try { model.Extension.FinishRecordingUndoObject2(undoTitle ?? "MCP Failed Operation", false); } catch { }
                    if (rollbackOnError)
                    {
                        try { model.EditUndo2(1); } catch { }
                    }
                }
                throw;
            }
        }

        public static MassStats ComputeMassStats(IModelDoc2 model)
        {
            if (model == null) return null;
            try
            {
                IMassProperty2 mp = model.Extension.CreateMassProperty2() as IMassProperty2;
                if (mp == null) return null;
                mp.UseSystemUnits = true;
                double[] com = mp.CenterOfMass as double[];
                return new MassStats
                {
                    MassKg = mp.Mass,
                    VolumeM3 = mp.Volume,
                    SurfaceAreaM2 = mp.SurfaceArea,
                    CenterOfMassM = com ?? new double[] { 0, 0, 0 }
                };
            }
            catch
            {
                return null;
            }
        }

        public static string ComputeFaceToken(IFace2 face, int bodyIndex)
        {
            if (face == null) return string.Empty;
            double[] box = face.GetBox() as double[];
            double cx = box != null && box.Length >= 6 ? 0.5 * (box[0] + box[3]) : 0;
            double cy = box != null && box.Length >= 6 ? 0.5 * (box[1] + box[4]) : 0;
            double cz = box != null && box.Length >= 6 ? 0.5 * (box[2] + box[5]) : 0;
            double[] uv = face.GetUVBounds() as double[];
            ISurface surf = face.GetSurface() as ISurface;
            double[] n = new double[] { 0, 0, 1 };
            if (surf != null && uv != null && uv.Length >= 4)
            {
                double[] eval = surf.Evaluate(0.5 * (uv[0] + uv[1]), 0.5 * (uv[2] + uv[3]), 1, 1) as double[];
                if (eval != null && eval.Length >= 12)
                {
                    cx = eval[0];
                    cy = eval[1];
                    cz = eval[2];
                    n[0] = eval[eval.Length - 3];
                    n[1] = eval[eval.Length - 2];
                    n[2] = eval[eval.Length - 1];
                }
            }
            if (face.FaceInSurfaceSense())
            {
                n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2];
            }
            return string.Format(CultureInfo.InvariantCulture,
                "face:{0}:{1:F4},{2:F4},{3:F4}@{4:F5},{5:F5},{6:F5}",
                bodyIndex, n[0], n[1], n[2], cx, cy, cz);
        }

        public static string ComputeEdgeToken(IEdge edge, int bodyIndex)
        {
            if (edge == null) return string.Empty;
            ICurve curve = edge.GetCurve() as ICurve;
            double[] cp = edge.GetCurveParams2() as double[];
            double cx = 0, cy = 0, cz = 0;
            if (curve != null && cp != null && cp.Length >= 8)
            {
                double tMid = 0.5 * (cp[6] + cp[7]);
                double[] pt = curve.Evaluate2(tMid, 0) as double[];
                if (pt != null && pt.Length >= 3)
                {
                    cx = pt[0]; cy = pt[1]; cz = pt[2];
                }
            }
            return string.Format(CultureInfo.InvariantCulture,
                "edge:{0}:{1:F5},{2:F5},{3:F5}",
                bodyIndex, cx, cy, cz);
        }

        public static bool TrySelectByToken(IModelDoc2 model, string token, bool append = false, int mark = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(token))
                return false;

            ISelectionMgr selMgr = model.SelectionManager as ISelectionMgr;
            SelectData selData = selMgr != null ? (SelectData)selMgr.CreateSelectData() : null;
            if (selData != null) selData.Mark = mark;

            if (token.StartsWith("pers:", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = Convert.FromBase64String(token.Substring(5));
                int err = 0;
                object obj = model.Extension.GetObjectByPersistReference3(bytes, out err);
                if (obj is IEntity ent)
                    return ent.Select4(append, selData);
                if (obj is IFeature feat)
                    return feat.Select2(append, mark);
                return false;
            }

            if (token.StartsWith("face:", StringComparison.OrdinalIgnoreCase))
            {
                IFace2 face = FindFaceByToken(model, token);
                if (face is IEntity ent)
                    return ent.Select4(append, selData);
                return false;
            }

            if (token.StartsWith("edge:", StringComparison.OrdinalIgnoreCase))
            {
                IEdge edge = FindEdgeByToken(model, token);
                if (edge is IEntity ent)
                    return ent.Select4(append, selData);
                return false;
            }

            // Try standard named features/planes/sketches/axes
            string[] types = new[] { "PLANE", "SKETCH", "BODYFEATURE", "AXIS", "COORDSYS", "DATUMPOINT", "SOLIDBODY", "SURFACEBODY", "" };
            foreach (string t in types)
            {
                if (model.Extension.SelectByID2(token, t, 0, 0, 0, append, mark, null, 0))
                    return true;
            }

            // Fallback traverse feature tree by name
            IFeature f = model.FirstFeature() as IFeature;
            while (f != null)
            {
                if (string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase))
                    return f.Select2(append, mark);
                f = f.GetNextFeature() as IFeature;
            }

            return false;
        }

        public static IFace2 FindFaceByToken(IModelDoc2 model, string token)
        {
            if (!(model is IPartDoc part)) return null;
            string[] parts = token.Split(':');
            if (parts.Length < 3) return null;
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bodyIdx);
            string[] normAndCenter = parts[2].Split('@');
            if (normAndCenter.Length != 2) return null;
            string[] nStr = normAndCenter[0].Split(',');
            string[] cStr = normAndCenter[1].Split(',');
            if (nStr.Length != 3 || cStr.Length != 3) return null;

            double nx = double.Parse(nStr[0], CultureInfo.InvariantCulture);
            double ny = double.Parse(nStr[1], CultureInfo.InvariantCulture);
            double nz = double.Parse(nStr[2], CultureInfo.InvariantCulture);
            double cx = double.Parse(cStr[0], CultureInfo.InvariantCulture);
            double cy = double.Parse(cStr[1], CultureInfo.InvariantCulture);
            double cz = double.Parse(cStr[2], CultureInfo.InvariantCulture);

            object[] bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if (bodies == null || bodyIdx < 0 || bodyIdx >= bodies.Length) return null;

            IBody2 body = bodies[bodyIdx] as IBody2;
            object[] faces = body?.GetFaces() as object[];
            if (faces == null) return null;

            IFace2 best = null;
            double bestDist = double.MaxValue;
            foreach (object fObj in faces)
            {
                if (!(fObj is IFace2 face)) continue;
                double[] uv = face.GetUVBounds() as double[];
                ISurface surf = face.GetSurface() as ISurface;
                double[] box = face.GetBox() as double[];
                double fx = box != null && box.Length >= 6 ? 0.5 * (box[0] + box[3]) : 0;
                double fy = box != null && box.Length >= 6 ? 0.5 * (box[1] + box[4]) : 0;
                double fz = box != null && box.Length >= 6 ? 0.5 * (box[2] + box[5]) : 0;
                double fnx = 0, fny = 0, fnz = 0;

                if (surf != null && uv != null && uv.Length >= 4)
                {
                    double[] eval = surf.Evaluate(0.5 * (uv[0] + uv[1]), 0.5 * (uv[2] + uv[3]), 1, 1) as double[];
                    if (eval != null && eval.Length >= 12)
                    {
                        fx = eval[0]; fy = eval[1]; fz = eval[2];
                        fnx = eval[eval.Length - 3]; fny = eval[eval.Length - 2]; fnz = eval[eval.Length - 1];
                        if (face.FaceInSurfaceSense()) { fnx = -fnx; fny = -fny; fnz = -fnz; }
                    }
                }

                double d = Math.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy) + (fz - cz) * (fz - cz));
                double dot = fnx * nx + fny * ny + fnz * nz;
                double score = d + (1.0 - Math.Max(-1.0, Math.Min(1.0, dot))) * 0.05;
                if (score < bestDist)
                {
                    bestDist = score;
                    best = face;
                }
            }
            return best;
        }

        public static IEdge FindEdgeByToken(IModelDoc2 model, string token)
        {
            if (!(model is IPartDoc part)) return null;
            string[] parts = token.Split(':');
            if (parts.Length < 3) return null;
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bodyIdx);
            string[] cStr = parts[2].Split(',');
            if (cStr.Length != 3) return null;

            double cx = double.Parse(cStr[0], CultureInfo.InvariantCulture);
            double cy = double.Parse(cStr[1], CultureInfo.InvariantCulture);
            double cz = double.Parse(cStr[2], CultureInfo.InvariantCulture);

            object[] bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if (bodies == null || bodyIdx < 0 || bodyIdx >= bodies.Length) return null;

            IBody2 body = bodies[bodyIdx] as IBody2;
            object[] edges = body?.GetEdges() as object[];
            if (edges == null) return null;

            IEdge best = null;
            double bestDist = double.MaxValue;
            foreach (object eObj in edges)
            {
                if (!(eObj is IEdge edge)) continue;
                ICurve curve = edge.GetCurve() as ICurve;
                double[] cp = edge.GetCurveParams2() as double[];
                if (curve != null && cp != null && cp.Length >= 8)
                {
                    double[] pt = curve.Evaluate2(0.5 * (cp[6] + cp[7]), 0) as double[];
                    if (pt != null && pt.Length >= 3)
                    {
                        double d = Math.Sqrt((pt[0] - cx) * (pt[0] - cx) + (pt[1] - cy) * (pt[1] - cy) + (pt[2] - cz) * (pt[2] - cz));
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = edge;
                        }
                    }
                }
            }
            return best;
        }

        public static void MapPointToSketch2D(IMathUtility mathUtil, IMathTransform modelToSketch, double mx, double my, double mz, out double sx, out double sy)
        {
            IMathPoint pt = mathUtil.CreatePoint(new double[] { mx, my, mz }) as IMathPoint;
            IMathPoint projected = pt.MultiplyTransform(modelToSketch) as IMathPoint;
            double[] arr = projected.ArrayData as double[];
            sx = arr[0];
            sy = arr[1];
        }

        public static string ResolveDefaultTemplate(ISldWorks app, string docType)
        {
            string swTemplateDir = @"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2026\templates";
            string t = (docType ?? "part").ToLowerInvariant();
            string file = "Part.PRTDOT";
            int pref = (int)swUserPreferenceStringValue_e.swDefaultTemplatePart;
            if (t == "assembly")
            {
                file = "Assembly.ASMDOT";
                pref = (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly;
            }
            else if (t == "drawing")
            {
                file = "Drawing.DRWDOT";
                pref = (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing;
            }

            string userDefault = app.GetUserPreferenceStringValue(pref);
            if (!string.IsNullOrWhiteSpace(userDefault) && File.Exists(userDefault))
                return userDefault;

            string candidate = Path.Combine(swTemplateDir, file);
            if (File.Exists(candidate))
                return candidate;

            return string.Empty;
        }
    }
}
