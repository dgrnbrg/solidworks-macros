using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    /// <summary>
    /// Attaches to active PartDoc delete events and cleans up orphaned child sketches,
    /// reference planes, slots, and tabs whenever the user deletes a Multi-Body Joinery
    /// feature from the FeatureManager tree. Executes deletions safely on OnIdleNotify.
    /// </summary>
    public class PartEventCleanUpHandler
    {
        private readonly ISldWorks _swApp;
        private PartDoc _attachedPart;
        private IModelDoc2 _attachedModel;
        private readonly HashSet<string> _queuedFeatureNamesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<JointGroupDefinition> _configsToRemove = new List<JointGroupDefinition>();

        public static bool SuppressCleanup { get; set; }

        public PartEventCleanUpHandler(ISldWorks swApp)
        {
            _swApp = swApp;
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
                List<JointGroupDefinition> configs = JointConfigStore.GetAllConfigsForModel(_attachedModel);
                foreach (JointGroupDefinition cfg in configs)
                {
                    if (!string.IsNullOrEmpty(cfg.ManagedMacroFeatureName) &&
                        string.Equals(cfg.ManagedMacroFeatureName, itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        QueueAllChildFeatures(cfg);
                        if (!_configsToRemove.Contains(cfg))
                            _configsToRemove.Add(cfg);
                    }

                    if (cfg.Joints != null)
                    {
                        foreach (JointDefinition j in cfg.Joints)
                        {
                            if (string.Equals(j.ManagedSlotCutName, itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                QueueIfNotEmpty(j.ManagedSlotSketchName);
                            }
                            if (string.Equals(j.ManagedTabBossName, itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                QueueIfNotEmpty(j.ManagedTabSketchName);
                            }
                        }
                    }
                }

                if (itemName.StartsWith("JoinerySlotCut_", StringComparison.OrdinalIgnoreCase) ||
                    itemName.StartsWith("JoineryTabBoss_", StringComparison.OrdinalIgnoreCase))
                {
                    TryQueueAbsorbedChildrenFromFeature(itemName);
                }
            }
            catch { }

            return 0;
        }

        private int OnPartDeleteItemNotify(int entityType, string itemName)
        {
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

        private void QueueAllChildFeatures(JointGroupDefinition cfg)
        {
            if (cfg.Joints == null) return;
            foreach (JointDefinition j in cfg.Joints)
            {
                QueueIfNotEmpty(j.ManagedTabBossName);
                QueueIfNotEmpty(j.ManagedTabSketchName);
                QueueIfNotEmpty(j.ManagedSlotCutName);
                QueueIfNotEmpty(j.ManagedSlotSketchName);
                QueueIfNotEmpty(j.ManagedRefPlaneName);
            }
        }

        private void QueueIfNotEmpty(string name)
        {
            if (!string.IsNullOrEmpty(name))
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
                    _attachedModel.ClearSelection2(true);
                    if (ext.SelectByID2(featName, "BODYFEATURE", 0, 0, 0, false, 0, null, 0) ||
                        ext.SelectByID2(featName, "SKETCH", 0, 0, 0, false, 0, null, 0) ||
                        ext.SelectByID2(featName, "PLANE", 0, 0, 0, false, 0, null, 0))
                    {
                        if (ext.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                        {
                            deletedAny = true;
                        }
                    }
                }

                if (_configsToRemove.Count > 0)
                {
                    foreach (JointGroupDefinition cfg in _configsToRemove)
                    {
                        JointConfigStore.DeleteFeatureConfig(_attachedModel, cfg);
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
