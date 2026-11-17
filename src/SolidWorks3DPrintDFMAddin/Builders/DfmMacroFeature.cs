using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorks3DPrintDFMAddin.Data;
using SolidWorks3DPrintDFMAddin.UI;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    [ComVisible(true)]
    [Guid("D14C6A92-3E85-4A19-9B72-5C8E4F102D63")]
    public class TeardropMacroFeature : ISwComFeature
    {
        public const string FEATURE_BASE_NAME = "Teardrop Fillet";

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
                string configId = DfmMacroFeatureHelper.ReadConfigId(featData);
                DfmConfig config = DfmConfigStore.LoadFeatureConfig(model, configId, feat.Name);
                SwAddin.Log(string.Format("TeardropMacroFeature.Edit({0}): configId={1}, loaded={2}",
                    feat.Name, configId ?? "<null>", config != null));

                if (config == null)
                    config = DfmConfig.LoadDefaults();

                SwAddin.LaunchPmpForEdit(swApp, model, feat, config, DfmCommandMode.TeardropFillet);
                return true;
            }
            catch (Exception ex)
            {
                SwAddin.Log("TeardropMacroFeature.Edit error: " + ex);
                return false;
            }
        }

        public object Regenerate(object app, object modelDoc, object feature) => true;

        public object Security(object app, object modelDoc, object feature)
            => (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
    }

    [ComVisible(true)]
    [Guid("E25D7B83-4F96-4B2A-8C61-6D9F5A213E74")]
    public class BridgeMacroFeature : ISwComFeature
    {
        public const string FEATURE_BASE_NAME = "Crisscross Bridge";

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
                string configId = DfmMacroFeatureHelper.ReadConfigId(featData);
                DfmConfig config = DfmConfigStore.LoadFeatureConfig(model, configId, feat.Name);
                SwAddin.Log(string.Format("BridgeMacroFeature.Edit({0}): configId={1}, loaded={2}",
                    feat.Name, configId ?? "<null>", config != null));

                if (config == null)
                    config = DfmConfig.LoadDefaults();

                SwAddin.LaunchPmpForEdit(swApp, model, feat, config, DfmCommandMode.CrisscrossBridge);
                return true;
            }
            catch (Exception ex)
            {
                SwAddin.Log("BridgeMacroFeature.Edit error: " + ex);
                return false;
            }
        }

        public object Regenerate(object app, object modelDoc, object feature) => true;

        public object Security(object app, object modelDoc, object feature)
            => (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
    }

    public static class DfmMacroFeatureHelper
    {
        private const string PARAM_KEY = "FeatureConfigId";

        public static void CommitAndNestFeatures(
            IModelDoc2 model,
            IFeature existingMacroFeature,
            DfmConfig config,
            DfmCommandMode mode,
            List<IFeature> childFeatures)
        {
            if (model == null || config == null)
                return;

            try
            {
                if (string.IsNullOrEmpty(config.FeatureConfigId))
                    config.FeatureConfigId = Guid.NewGuid().ToString("D");

                string baseName = mode == DfmCommandMode.TeardropFillet
                    ? TeardropMacroFeature.FEATURE_BASE_NAME
                    : BridgeMacroFeature.FEATURE_BASE_NAME;

                string progId = mode == DfmCommandMode.TeardropFillet
                    ? typeof(TeardropMacroFeature).FullName
                    : typeof(BridgeMacroFeature).FullName;

                IFeature macroFeat = existingMacroFeature;

                if (macroFeat == null)
                {
                    string[] paramNames = new[] { PARAM_KEY };
                    int[] paramTypes = new[] { (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString };
                    string[] paramValues = new[] { config.FeatureConfigId };
                    string[] iconFiles = mode == DfmCommandMode.TeardropFillet
                        ? IconResourceHelper.GetTeardropTreeIconFiles()
                        : IconResourceHelper.GetBridgeTreeIconFiles();

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
                        iconFiles,
                        (int)swMacroFeatureOptions_e.swMacroFeatureByDefault);
                }
                else
                {
                    IMacroFeatureData data = (IMacroFeatureData)macroFeat.GetDefinition();
                    if (data != null)
                    {
                        data.AccessSelections(model, null);
                        WriteConfigId(data, config.FeatureConfigId);
                        macroFeat.ModifyDefinition(data, model, null);
                    }
                }

                if (macroFeat != null)
                {
                    config.ManagedMacroFeatureName = macroFeat.Name;
                }

                // Persist full config (parameters, child feature names, and PersistReference byte arrays)
                DfmConfigStore.SaveFeatureConfig(model, config);

                // Nest child features directly under the MacroFeature in the FeatureManager Design Tree
                if (macroFeat != null && childFeatures != null && childFeatures.Count > 0)
                {
                    foreach (IFeature child in childFeatures)
                    {
                        if (child == null) continue;
                        try
                        {
                            macroFeat.MakeSubFeature((Feature)child);
                        }
                        catch { }
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
