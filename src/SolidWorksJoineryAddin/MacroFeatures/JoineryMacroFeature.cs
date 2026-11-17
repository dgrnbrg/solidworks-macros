using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorksJoineryAddin.Data;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    [ComVisible(true)]
    [Guid("98B75F34-C82D-4C91-8EE2-B67D34F18A32")]
    public class JoineryMacroFeature : ISwComFeature
    {
        public const string FEATURE_BASE_NAME = "Multi-Body Joinery";

        /// <summary>
        /// SolidWorks Edit Definition Callback.
        /// Invoked when user right-clicks feature in FeatureManager tree -> "Edit Feature".
        /// </summary>
        public object Edit(object app, object modelDoc, object feature)
        {
            ISldWorks swApp = app as ISldWorks;
            IModelDoc2 model = modelDoc as IModelDoc2;
            IFeature feat = feature as IFeature;
            if (swApp == null || model == null || feat == null)
                return false;

            try
            {
                IMacroFeatureData featData = (IMacroFeatureData)feat.GetDefinition();
                string configId = JoineryMacroFeatureHelper.ReadConfigId(featData);
                JointGroupDefinition group = JointConfigStore.LoadFeatureConfig(model, configId, feat.Name);
                SwAddin.Log(string.Format("JoineryMacroFeature.Edit({0}): configId={1}, loaded={2}",
                    feat.Name, configId ?? "<null>", group != null));

                if (group == null)
                    group = ProjectSettings.CreateDefaultGroup();

                SwAddin.LaunchPmpForEdit(swApp, model, feat, group);
                return true;
            }
            catch (Exception ex)
            {
                SwAddin.Log("JoineryMacroFeature.Edit error: " + ex);
                return false;
            }
        }

        /// <summary>
        /// SolidWorks Regeneration Callback.
        /// Child features are modeled cleanly as real SolidWorks features nested below/under this MacroFeature.
        /// </summary>
        public object Regenerate(object app, object modelDoc, object feature) => true;

        public object Security(object app, object modelDoc, object feature)
            => (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
    }

    public static class JoineryMacroFeatureHelper
    {
        private const string PARAM_KEY = "FeatureConfigId";

        public static void CommitAndNestFeatures(
            IModelDoc2 model,
            IFeature existingMacroFeature,
            JointGroupDefinition group,
            List<IFeature> childFeatures)
        {
            if (model == null || group == null)
                return;

            try
            {
                if (string.IsNullOrEmpty(group.FeatureConfigId))
                    group.FeatureConfigId = Guid.NewGuid().ToString("D");

                string baseName = JoineryMacroFeature.FEATURE_BASE_NAME;
                string progId = typeof(JoineryMacroFeature).FullName;

                IFeature macroFeat = existingMacroFeature;

                if (macroFeat == null)
                {
                    string[] paramNames = new[] { PARAM_KEY };
                    int[] paramTypes = new[] { (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString };
                    string[] paramValues = new[] { group.FeatureConfigId };

                    model.ClearSelection2(true);
                    macroFeat = model.FeatureManager.InsertMacroFeature3(
                        baseName,
                        progId,
                        null,
                        paramNames,
                        paramTypes,
                        paramValues,
                        null,
                        null,
                        null,
                        null,
                        (int)swMacroFeatureOptions_e.swMacroFeatureByDefault);
                }
                else
                {
                    IMacroFeatureData data = (IMacroFeatureData)macroFeat.GetDefinition();
                    if (data != null)
                    {
                        data.AccessSelections(model, null);
                        WriteConfigId(data, group.FeatureConfigId);
                        macroFeat.ModifyDefinition(data, model, null);
                    }
                }

                if (macroFeat != null)
                {
                    group.ManagedMacroFeatureName = macroFeat.Name;
                }

                // Persist full config to disk XML and Part CustomPropertyManager
                JointConfigStore.SaveFeatureConfig(model, group);

                // Nest child features directly under the MacroFeature in the FeatureManager Design Tree
                if (macroFeat != null && childFeatures != null && childFeatures.Count > 0)
                {
                    int absorbedCount = 0;
                    foreach (IFeature child in childFeatures)
                    {
                        if (child == null) continue;
                        try
                        {
                            if (macroFeat.MakeSubFeature((Feature)child))
                            {
                                absorbedCount++;
                            }
                        }
                        catch { }
                    }

                    if (absorbedCount < childFeatures.Count && existingMacroFeature == null)
                    {
                        model.ClearSelection2(true);
                        foreach (IFeature child in childFeatures)
                        {
                            if (child != null)
                                child.Select2(true, 0);
                        }
                        macroFeat.Select2(true, 0);

                        IFeature folder = model.FeatureManager.InsertFeatureTreeFolder2(
                            (int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing);
                        if (folder != null)
                        {
                            try { folder.Name = macroFeat.Name + " Group"; } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SwAddin.Log("CommitAndNestFeatures error: " + ex);
            }
        }

        public static void WriteConfigId(IMacroFeatureData featData, string configId)
        {
            if (featData == null || string.IsNullOrEmpty(configId)) return;
            try
            {
                featData.SetStringByName(PARAM_KEY, configId);
            }
            catch { }
        }

        public static string ReadConfigId(IMacroFeatureData featData)
        {
            if (featData == null) return null;
            try
            {
                featData.GetStringByName(PARAM_KEY, out string val);
                return val;
            }
            catch
            {
                return null;
            }
        }
    }
}
