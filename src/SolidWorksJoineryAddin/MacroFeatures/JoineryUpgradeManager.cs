using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksJoineryAddin.Data;
using SolidWorksJoineryAddin.UI;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    public class JoineryUpgradeManager
    {
        private readonly ISldWorks _swApp;
        private readonly SldWorks _swAppEvents;
        private readonly HashSet<string> _checkedDocsInSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IModelDoc2 _pendingModel;
        private string _pendingDocKey;
        private bool _idleHookAttached;

        private class FoundJoineryFeature
        {
            public IFeature Feature;
            public IMacroFeatureData FeatData;
            public JointGroupDefinition Group;
        }

        public JoineryUpgradeManager(ISldWorks swApp, SldWorks swAppEvents)
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

                List<FoundJoineryFeature> allFeatures = CollectJoineryMacroFeatures(model);
                if (allFeatures.Count == 0)
                {
                    if (showUpToDateMsg)
                    {
                        _swApp?.SendMsgToUser2(
                            "No Multi-Body Joinery features found in the active part.",
                            (int)swMessageBoxIcon_e.swMbInformation,
                            (int)swMessageBoxBtn_e.swMbOk);
                    }
                    return;
                }

                foreach (FoundJoineryFeature item in allFeatures)
                {
                    RefreshFeatureIconIfNeeded(model, item.Feature, item.FeatData);
                }

                List<FoundJoineryFeature> outdated = new List<FoundJoineryFeature>();
                foreach (FoundJoineryFeature item in allFeatures)
                {
                    if (item.Group != null && item.Group.FeatureSchemaVersion < JointGroupDefinition.CURRENT_FEATURE_VERSION)
                    {
                        outdated.Add(item);
                    }
                }

                if (outdated.Count == 0)
                {
                    if (showUpToDateMsg)
                    {
                        _swApp?.SendMsgToUser2(
                            string.Format("All Multi-Body Joinery features are already up to date (v{0}).", JointGroupDefinition.CURRENT_FEATURE_VERSION),
                            (int)swMessageBoxIcon_e.swMbInformation,
                            (int)swMessageBoxBtn_e.swMbOk);
                    }
                    return;
                }

                if (promptUser && _swApp != null)
                {
                    int res = _swApp.SendMsgToUser2(
                        string.Format(
                            "This part contains {0} Multi-Body Joinery feature(s) created with an older version of the add-in.\n\n" +
                            "Upgrade and rebuild them with the latest algorithm (v{1})?",
                            outdated.Count, JointGroupDefinition.CURRENT_FEATURE_VERSION),
                        (int)swMessageBoxIcon_e.swMbQuestion,
                        (int)swMessageBoxBtn_e.swMbYesNo);

                    if (res != (int)swMessageBoxResult_e.swMbHitYes)
                        return;
                }

                ExecuteTransactionalUpgrade(model, outdated);
            }
            catch (Exception ex)
            {
                SwAddin.Log("JoineryUpgradeManager.ProcessDocument error: " + ex);
            }
        }

        private void ExecuteTransactionalUpgrade(IModelDoc2 model, List<FoundJoineryFeature> outdated)
        {
            IModelDocExtension ext = model.Extension;
            Dictionary<JointGroupDefinition, int> originalVersions = new Dictionary<JointGroupDefinition, int>();
            foreach (FoundJoineryFeature item in outdated)
            {
                originalVersions[item.Group] = item.Group.FeatureSchemaVersion;
            }

            bool allSucceeded = true;
            ext.StartRecordingUndoObject();

            try
            {
                foreach (FoundJoineryFeature item in outdated)
                {
                    List<IFeature> created = GeometryBuilder.ApplyJointGroupToModel(_swApp, model, item.Group);
                    if (created.Count == 0 && item.Group.Joints != null && item.Group.Joints.Count > 0)
                    {
                        allSucceeded = false;
                        break;
                    }

                    JoineryMacroFeatureHelper.CommitAndNestFeatures(model, item.Feature, item.Group, created);
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("JoineryUpgradeManager.ExecuteTransactionalUpgrade error: " + ex);
                allSucceeded = false;
            }

            if (allSucceeded)
            {
                ext.FinishRecordingUndoObject2("Upgrade Multi-Body Joinery Features", false);
                model.ClearSelection2(true);
                model.EditRebuild3();
                SwAddin.Log(string.Format("Upgraded {0} Joinery feature(s) to v{1}.", outdated.Count, JointGroupDefinition.CURRENT_FEATURE_VERSION));
            }
            else
            {
                ext.FinishRecordingUndoObject2("Failed Joinery Upgrade", true);
                model.EditUndo2(1);
                foreach (KeyValuePair<JointGroupDefinition, int> kv in originalVersions)
                {
                    kv.Key.FeatureSchemaVersion = kv.Value;
                }
                _swApp?.SendMsgToUser2(
                    "One or more legacy Multi-Body Joinery features failed to rebuild cleanly. Changes were rolled back and the part was left unchanged.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private static List<FoundJoineryFeature> CollectJoineryMacroFeatures(IModelDoc2 model)
        {
            List<FoundJoineryFeature> list = new List<FoundJoineryFeature>();
            if (model == null) return list;

            string progIdTarget = typeof(JoineryMacroFeature).FullName;
            IFeature feat = (IFeature)model.FirstFeature();
            while (feat != null)
            {
                try
                {
                    if (string.Equals(feat.GetTypeName2(), "MacroFeature", StringComparison.OrdinalIgnoreCase))
                    {
                        IMacroFeatureData featData = feat.GetDefinition() as IMacroFeatureData;
                        string progId = featData?.GetProgId();
                        if (string.Equals(progId, progIdTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            string configId = JoineryMacroFeatureHelper.ReadConfigId(featData);
                            JointGroupDefinition grp = JointConfigStore.LoadFeatureConfig(model, configId, feat.Name);
                            list.Add(new FoundJoineryFeature
                            {
                                Feature = feat,
                                FeatData = featData,
                                Group = grp
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
            IMacroFeatureData featData)
        {
            if (model == null || feat == null || featData == null)
                return;

            try
            {
                string[] expected = IconResourceHelper.GetJoineryTreeIconFiles();
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
