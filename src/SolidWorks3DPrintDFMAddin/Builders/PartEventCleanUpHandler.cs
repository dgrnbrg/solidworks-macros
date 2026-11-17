using System;
using System.Collections.Generic;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks3DPrintDFMAddin.Data;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    /// <summary>
    /// Attaches to active PartDoc delete events and cleans up orphaned child sketches,
    /// reference planes, and cut features whenever the user deletes a Teardrop Fillet or
    /// Crisscross Bridge feature (or one of its Cut-Extrudes) from the FeatureManager tree.
    /// Uses a short one-shot WinForms Timer after DeleteItemNotify instead of OnIdleNotify
    /// so zero COM->CLR callbacks fire during SolidWorks' normal idle message loop.
    /// </summary>
    public class PartEventCleanUpHandler
    {
        private readonly ISldWorks _swApp;
        private PartDoc _attachedPart;
        private IModelDoc2 _attachedModel;
        private readonly Timer _deferredCleanupTimer;
        private readonly List<string> _queuedFeatureNamesToDelete = new List<string>();
        private readonly List<DfmConfig> _configsToRemove = new List<DfmConfig>();

        public static bool SuppressCleanup { get; set; }

        public PartEventCleanUpHandler(ISldWorks swApp)
        {
            _swApp = swApp;
            _deferredCleanupTimer = new Timer { Interval = 60, Enabled = false };
            _deferredCleanupTimer.Tick += OnDeferredCleanupTimerTick;
        }

        private void OnDeferredCleanupTimerTick(object sender, EventArgs e)
        {
            _deferredCleanupTimer.Enabled = false;
            ProcessPendingCleanup();
        }

        public void AttachToActivePart()
        {
            try
            {
                if (_swApp == null) return;
                IModelDoc2 active = _swApp.ActiveDoc as IModelDoc2;
                if (active == _attachedModel) return;

                Detach();

                if (active != null && active.GetType() == (int)swDocumentTypes_e.swDocPART)
                {
                    _attachedModel = active;
                    _attachedPart = active as PartDoc;
                    if (_attachedPart != null)
                    {
                        _attachedPart.DeleteItemPreNotify += OnPartDeleteItemPreNotify;
                        _attachedPart.DeleteItemNotify += OnPartDeleteItemNotify;
                        _attachedPart.DestroyNotify += OnPartDestroyNotify;
                    }
                }
            }
            catch { }
        }

        public void Detach()
        {
            try
            {
                if (_deferredCleanupTimer != null)
                    _deferredCleanupTimer.Enabled = false;

                if (_attachedPart != null)
                {
                    _attachedPart.DeleteItemPreNotify -= OnPartDeleteItemPreNotify;
                    _attachedPart.DeleteItemNotify -= OnPartDeleteItemNotify;
                    _attachedPart.DestroyNotify -= OnPartDestroyNotify;
                }
            }
            catch { }
            finally
            {
                _attachedPart = null;
                _attachedModel = null;
                _queuedFeatureNamesToDelete.Clear();
                _configsToRemove.Clear();
            }
        }

        private int OnPartDestroyNotify()
        {
            Detach();
            return 0;
        }

        private int OnPartDeleteItemPreNotify(int entityType, string itemName)
        {
            if (SuppressCleanup || _attachedModel == null || string.IsNullOrEmpty(itemName))
                return 0;

            if (entityType != (int)swNotifyEntityType_e.swNotifyFeature)
                return 0;

            try
            {
                List<DfmConfig> configs = DfmConfigStore.GetAllConfigsForModel(_attachedModel);
                foreach (DfmConfig cfg in configs)
                {
                    if (!string.IsNullOrEmpty(cfg.ManagedMacroFeatureName) &&
                        string.Equals(cfg.ManagedMacroFeatureName, itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        QueueAllChildFeatures(cfg);
                        if (!_configsToRemove.Contains(cfg))
                            _configsToRemove.Add(cfg);
                    }

                    if (cfg.TeardropTargets != null)
                    {
                        foreach (TeardropHoleTarget t in cfg.TeardropTargets)
                        {
                            if (string.Equals(t.ManagedCutFeatureName, itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                QueueIfNotEmpty(t.ManagedSketchName);
                                QueueIfNotEmpty(t.ManagedRefPlaneName);
                            }
                        }
                    }

                    if (cfg.BridgeTargets != null)
                    {
                        foreach (CrisscrossBridgeTarget b in cfg.BridgeTargets)
                        {
                            if (string.Equals(b.ManagedTier1CutName, itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                QueueIfNotEmpty(b.ManagedTier1SketchName);
                            }
                            if (string.Equals(b.ManagedTier2CutName, itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                QueueIfNotEmpty(b.ManagedTier2SketchName);
                            }
                        }
                    }
                }

                if (itemName.StartsWith("TeardropCut_", StringComparison.OrdinalIgnoreCase) ||
                    itemName.StartsWith("BridgeTier", StringComparison.OrdinalIgnoreCase))
                {
                    TryQueueAbsorbedChildrenFromFeature(itemName);
                }
            }
            catch { }

            return 0;
        }

        private int OnPartDeleteItemNotify(int entityType, string itemName)
        {
            if (!SuppressCleanup && _queuedFeatureNamesToDelete.Count > 0 && _deferredCleanupTimer != null)
            {
                _deferredCleanupTimer.Enabled = true;
            }
            return 0;
        }

        private void TryQueueAbsorbedChildrenFromFeature(string featureName)
        {
            try
            {
                IFeature feat = _attachedPart?.FeatureByName(featureName) as IFeature;
                if (feat == null) return;

                IFeature sub = feat.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    QueueIfNotEmpty(sub.Name);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
            }
            catch { }
        }

        private void QueueAllChildFeatures(DfmConfig cfg)
        {
            if (cfg.TeardropTargets != null)
            {
                foreach (TeardropHoleTarget t in cfg.TeardropTargets)
                {
                    QueueIfNotEmpty(t.ManagedCutFeatureName);
                    QueueIfNotEmpty(t.ManagedSketchName);
                    QueueIfNotEmpty(t.ManagedRefPlaneName);
                }
            }

            if (cfg.BridgeTargets != null)
            {
                foreach (CrisscrossBridgeTarget b in cfg.BridgeTargets)
                {
                    if (b.ManagedCutNames != null)
                    {
                        for (int i = b.ManagedCutNames.Count - 1; i >= 0; i--)
                            QueueIfNotEmpty(b.ManagedCutNames[i]);
                    }
                    if (b.ManagedSketchNames != null)
                    {
                        for (int i = b.ManagedSketchNames.Count - 1; i >= 0; i--)
                            QueueIfNotEmpty(b.ManagedSketchNames[i]);
                    }
                    QueueIfNotEmpty(b.ManagedTier2CutName);
                    QueueIfNotEmpty(b.ManagedTier2SketchName);
                    QueueIfNotEmpty(b.ManagedTier1CutName);
                    QueueIfNotEmpty(b.ManagedTier1SketchName);
                    QueueIfNotEmpty(b.ManagedRefPlaneName);
                }
            }
        }

        private void QueueIfNotEmpty(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (string existing in _queuedFeatureNamesToDelete)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            _queuedFeatureNamesToDelete.Add(name);
        }

        public void ProcessPendingCleanup()
        {
            if (SuppressCleanup || _attachedModel == null || _queuedFeatureNamesToDelete.Count == 0)
                return;

            SuppressCleanup = true;
            try
            {
                List<string> toDelete = new List<string>(_queuedFeatureNamesToDelete);
                _queuedFeatureNamesToDelete.Clear();

                IModelDocExtension ext = _attachedModel.Extension;
                bool deletedAny = false;

                foreach (string featName in toDelete)
                {
                    if (string.IsNullOrEmpty(featName)) continue;
                    // Check FeatureByName before selecting so SolidWorks never logs "Selection Failed"
                    IFeature existing = _attachedPart?.FeatureByName(featName) as IFeature;
                    if (existing == null) continue;

                    _attachedModel.ClearSelection2(true);
                    if (existing.Select2(false, 0))
                    {
                        if (ext.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                        {
                            deletedAny = true;
                        }
                    }
                }

                if (_configsToRemove.Count > 0)
                {
                    foreach (DfmConfig cfg in _configsToRemove)
                    {
                        DfmConfigStore.DeleteFeatureConfig(_attachedModel, cfg);
                    }
                    _configsToRemove.Clear();
                }

                if (deletedAny)
                {
                    _attachedModel.ClearSelection2(true);
                    _attachedModel.EditRebuild3();
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("ProcessPendingCleanup error: " + ex);
            }
            finally
            {
                SuppressCleanup = false;
            }
        }
    }
}
