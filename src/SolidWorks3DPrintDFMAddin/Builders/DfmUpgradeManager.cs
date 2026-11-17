using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Data;
using SolidWorks3DPrintDFMAddin.UI;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    public class DfmUpgradeManager
    {
        private readonly ISldWorks _swApp;
        private readonly SldWorks _swAppEvents;
        private readonly HashSet<string> _checkedDocsInSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IModelDoc2 _pendingModel;
        private string _pendingDocKey;
        private bool _idleHookAttached;

        private class FoundDfmFeature
        {
            public IFeature Feature;
            public IMacroFeatureData FeatData;
            public DfmCommandMode Mode;
            public DfmConfig Config;
        }

        public DfmUpgradeManager(ISldWorks swApp, SldWorks swAppEvents)
        {
            _swApp = swApp;
            _swAppEvents = swAppEvents;
        }

        public void Detach()
        {
            if (_idleHookAttached && _swAppEvents != null)
            {
                try { _swAppEvents.OnIdleNotify -= OnOneShotIdle; } catch { }
            }
            _idleHookAttached = false;
            _pendingModel = null;
            _pendingDocKey = null;
        }

        public void QueueDocumentCheck(IModelDoc2 model)
        {
            if (_swApp == null || _swAppEvents == null || model == null)
                return;

            if (model.GetType() != (int)swDocumentTypes_e.swDocPART)
                return;

            string key = GetDocKey(model);
            if (string.IsNullOrEmpty(key) || _checkedDocsInSession.Contains(key))
                return;

            _pendingModel = model;
            _pendingDocKey = key;

            if (!_idleHookAttached)
            {
                _swAppEvents.OnIdleNotify += OnOneShotIdle;
                _idleHookAttached = true;
            }
        }

        private int OnOneShotIdle()
        {
            try
            {
                if (_swApp != null && _swApp.CommandInProgress)
                    return 0;
            }
            catch { }

            if (_idleHookAttached && _swAppEvents != null)
            {
                try { _swAppEvents.OnIdleNotify -= OnOneShotIdle; } catch { }
                _idleHookAttached = false;
            }

            IModelDoc2 model = _pendingModel;
            string docKey = _pendingDocKey;
            _pendingModel = null;
            _pendingDocKey = null;

            if (model != null)
            {
                ProcessDocument(model, docKey, promptUser: true, showUpToDateMsg: false);
            }

            return 0;
        }

        public void UpgradeActiveDocumentNow(IModelDoc2 model)
        {
            if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                return;

            ProcessDocument(model, GetDocKey(model), promptUser: false, showUpToDateMsg: true);
        }

        private void ProcessDocument(IModelDoc2 model, string docKey, bool promptUser, bool showUpToDateMsg)
        {
            try
            {
                if (!string.IsNullOrEmpty(docKey))
                    _checkedDocsInSession.Add(docKey);

                List<FoundDfmFeature> allFeatures = CollectDfmMacroFeatures(model);
                if (allFeatures.Count == 0)
                {
                    if (showUpToDateMsg)
                    {
                        _swApp?.SendMsgToUser2(
                            "No 3D Print DFM features found in the active part.",
                            (int)swMessageBoxIcon_e.swMbInformation,
                            (int)swMessageBoxBtn_e.swMbOk);
                    }
                    return;
                }

                // Always refresh MacroFeature tree icons if missing or stale
                foreach (FoundDfmFeature item in allFeatures)
                {
                    RefreshFeatureIconIfNeeded(model, item.Feature, item.FeatData, item.Mode);
                }

                List<FoundDfmFeature> outdated = new List<FoundDfmFeature>();
                foreach (FoundDfmFeature item in allFeatures)
                {
                    if (item.Config != null && item.Config.FeatureSchemaVersion < DfmConfig.CURRENT_FEATURE_VERSION)
                    {
                        outdated.Add(item);
                    }
                }

                if (outdated.Count == 0)
                {
                    if (showUpToDateMsg)
                    {
                        _swApp?.SendMsgToUser2(
                            string.Format("All 3D Print DFM features are already up to date (v{0}).", DfmConfig.CURRENT_FEATURE_VERSION),
                            (int)swMessageBoxIcon_e.swMbInformation,
                            (int)swMessageBoxBtn_e.swMbOk);
                    }
                    return;
                }

                if (promptUser && _swApp != null)
                {
                    int res = _swApp.SendMsgToUser2(
                        string.Format(
                            "This part contains {0} 3D Print DFM feature(s) created with an older version of the add-in.\n\n" +
                            "Upgrade and rebuild them with the latest algorithm (v{1})?",
                            outdated.Count, DfmConfig.CURRENT_FEATURE_VERSION),
                        (int)swMessageBoxIcon_e.swMbQuestion,
                        (int)swMessageBoxBtn_e.swMbYesNo);

                    if (res != (int)swMessageBoxResult_e.swMbHitYes)
                        return;
                }

                ExecuteTransactionalUpgrade(model, outdated);
            }
            catch (Exception ex)
            {
                SwAddin.Log("DfmUpgradeManager.ProcessDocument error: " + ex);
            }
        }

        private void ExecuteTransactionalUpgrade(IModelDoc2 model, List<FoundDfmFeature> outdated)
        {
            IModelDocExtension ext = model.Extension;
            Dictionary<DfmConfig, int> originalVersions = new Dictionary<DfmConfig, int>();
            foreach (FoundDfmFeature item in outdated)
            {
                originalVersions[item.Config] = item.Config.FeatureSchemaVersion;
            }

            bool allSucceeded = true;
            ext.StartRecordingUndoObject();

            try
            {
                foreach (FoundDfmFeature item in outdated)
                {
                    if (!UpgradeSingleFeature(model, item))
                    {
                        allSucceeded = false;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("DfmUpgradeManager.ExecuteTransactionalUpgrade error: " + ex);
                allSucceeded = false;
            }

            if (allSucceeded)
            {
                ext.FinishRecordingUndoObject2("Upgrade 3D Print DFM Features", false);
                model.ClearSelection2(true);
                model.EditRebuild3();
                SwAddin.Log(string.Format("Upgraded {0} DFM feature(s) to v{1}.", outdated.Count, DfmConfig.CURRENT_FEATURE_VERSION));
            }
            else
            {
                ext.FinishRecordingUndoObject2("Failed DFM Upgrade", true);
                model.EditUndo2(1);
                foreach (KeyValuePair<DfmConfig, int> kv in originalVersions)
                {
                    kv.Key.FeatureSchemaVersion = kv.Value;
                }
                _swApp?.SendMsgToUser2(
                    "One or more legacy 3D Print DFM features failed to rebuild cleanly. Changes were rolled back and the part was left unchanged.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private bool UpgradeSingleFeature(IModelDoc2 model, FoundDfmFeature item)
        {
            DfmConfig cfg = item.Config;
            cfg.MigrateInMemory();

            if (cfg.BedRefPersistId != null)
            {
                object bedObj = BedStateStore.ResolvePersistReference(model, cfg.BedRefPersistId);
                if (bedObj != null && TopologyScanner.TryResolveBedReference(
                    bedObj, cfg.FlipBedDirection, out Core.Vector3D bedUp, out Core.Vector3D refPt))
                {
                    cfg.BedUpVector = bedUp;
                    cfg.BedReferencePoint = refPt;
                }
            }

            if (item.Mode == DfmCommandMode.TeardropFillet)
            {
                List<IFace2> faces = ResolveFaces(model, cfg.TeardropFacePersistIds);
                List<DetectedHoleFaceGroup> liveGroups = null;
                if (faces.Count > 0)
                {
                    liveGroups = TopologyScanner.BuildTeardropTargetsFromSelectedFaces(faces, cfg);
                    if (liveGroups.Count > 0)
                    {
                        cfg.TeardropTargets.Clear();
                        foreach (DetectedHoleFaceGroup g in liveGroups)
                            cfg.TeardropTargets.Add(g.Target);
                    }
                }

                List<IFeature> created = DfmFeatureBuilder.ApplyTeardropFeatures(_swApp, model, cfg, liveGroups);
                if (created.Count == 0 && cfg.TeardropTargets.Count > 0)
                    return false;

                DfmMacroFeatureHelper.CommitAndNestFeatures(model, item.Feature, cfg, DfmCommandMode.TeardropFillet, created);
                return true;
            }
            else
            {
                List<IFace2> faces = ResolveFaces(model, cfg.BridgeFacePersistIds);
                List<DetectedRecessFace> liveRecesses = null;
                if (faces.Count > 0)
                {
                    liveRecesses = TopologyScanner.BuildBridgeTargetsFromSelectedFaces(faces, cfg);
                    if (liveRecesses.Count > 0)
                    {
                        cfg.BridgeTargets.Clear();
                        foreach (DetectedRecessFace r in liveRecesses)
                            cfg.BridgeTargets.Add(r.Target);
                    }
                }

                List<IFeature> created = DfmFeatureBuilder.ApplyCrisscrossBridgeFeatures(_swApp, model, cfg, liveRecesses);
                if (created.Count == 0 && cfg.BridgeTargets.Count > 0)
                    return false;

                DfmMacroFeatureHelper.CommitAndNestFeatures(model, item.Feature, cfg, DfmCommandMode.CrisscrossBridge, created);
                return true;
            }
        }

        private static List<IFace2> ResolveFaces(IModelDoc2 model, IList<byte[]> persistIds)
        {
            List<IFace2> faces = new List<IFace2>();
            if (model == null || persistIds == null) return faces;

            foreach (byte[] pid in persistIds)
            {
                object resolved = BedStateStore.ResolvePersistReference(model, pid);
                if (resolved is IFace2 face && !faces.Contains(face))
                {
                    faces.Add(face);
                }
            }
            return faces;
        }

        private static List<FoundDfmFeature> CollectDfmMacroFeatures(IModelDoc2 model)
        {
            List<FoundDfmFeature> list = new List<FoundDfmFeature>();
            if (model == null) return list;

            string tdProgId = typeof(TeardropMacroFeature).FullName;
            string brProgId = typeof(BridgeMacroFeature).FullName;

            IFeature feat = (IFeature)model.FirstFeature();
            while (feat != null)
            {
                try
                {
                    if (string.Equals(feat.GetTypeName2(), "MacroFeature", StringComparison.OrdinalIgnoreCase))
                    {
                        IMacroFeatureData featData = feat.GetDefinition() as IMacroFeatureData;
                        string progId = featData?.GetProgId();

                        if (string.Equals(progId, tdProgId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(progId, brProgId, StringComparison.OrdinalIgnoreCase))
                        {
                            DfmCommandMode mode = string.Equals(progId, tdProgId, StringComparison.OrdinalIgnoreCase)
                                ? DfmCommandMode.TeardropFillet
                                : DfmCommandMode.CrisscrossBridge;

                            string configId = DfmMacroFeatureHelper.ReadConfigId(featData);
                            DfmConfig cfg = DfmConfigStore.LoadFeatureConfig(model, configId, feat.Name);

                            list.Add(new FoundDfmFeature
                            {
                                Feature = feat,
                                FeatData = featData,
                                Mode = mode,
                                Config = cfg
                            });
                        }
                    }
                }
                catch { }

                feat = (IFeature)feat.GetNextFeature();
            }

            return list;
        }

        public static void RefreshFeatureIconIfNeeded(
            IModelDoc2 model,
            IFeature feat,
            IMacroFeatureData featData,
            DfmCommandMode mode)
        {
            if (model == null || feat == null || featData == null)
                return;

            try
            {
                string[] expected = mode == DfmCommandMode.TeardropFillet
                    ? IconResourceHelper.GetTeardropTreeIconFiles()
                    : IconResourceHelper.GetBridgeTreeIconFiles();

                string[] current = featData.IconFiles as string[];
                if (IconPathsMatch(current, expected))
                    return;

                featData.AccessSelections(model, null);
                featData.IconFiles = expected;
                if (!feat.ModifyDefinition(featData, model, null))
                {
                    featData.ReleaseSelectionAccess();
                }
            }
            catch { }
        }

        private static bool IconPathsMatch(string[] current, string[] expected)
        {
            if (current == null || expected == null || current.Length != expected.Length)
                return false;

            for (int i = 0; i < expected.Length; i++)
            {
                if (!string.Equals(current[i], expected[i], StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(current[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string GetDocKey(IModelDoc2 model)
        {
            if (model == null) return null;
            string path = model.GetPathName();
            return !string.IsNullOrEmpty(path) ? path : model.GetTitle();
        }
    }
}
