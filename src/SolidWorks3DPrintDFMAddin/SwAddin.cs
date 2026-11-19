using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorks3DPrintDFMAddin.Builders;
using SolidWorks3DPrintDFMAddin.Data;
using SolidWorks3DPrintDFMAddin.UI;

namespace SolidWorks3DPrintDFMAddin
{
    [ComVisible(true)]
    [Guid("3E9A4B71-6D2C-4F18-8E53-9A7C1D4B206F")]
    public class SwAddin : ISwAddin
    {
        public const string ADDIN_GUID = "{3E9A4B71-6D2C-4F18-8E53-9A7C1D4B206F}";
        private ISldWorks _swApp;
        private SldWorks _swAppEvents;
        private int _addinCookie;
        private ICommandManager _cmdMgr;
        private PartEventCleanUpHandler _partCleanup;
        private DfmUpgradeManager _upgradeMgr;
        private static DfmPMPHandler _activePmp;
        private const int CMD_GROUP_ID = 5003;

        public static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "SolidWorks3DPrintDFMAddin");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string logPath = Path.Combine(dir, "dfm_debug.log");
                File.AppendAllText(logPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}",
                    DateTime.Now, message, System.Environment.NewLine));
            }
            catch { }
        }

        public static void LaunchPmpForEdit(
            ISldWorks swApp,
            IModelDoc2 model,
            IFeature existingMacroFeat,
            DfmConfig config,
            DfmCommandMode mode)
        {
            _activePmp = new DfmPMPHandler(swApp, model, existingMacroFeat, config, mode);
            _activePmp.Show();
        }

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _swAppEvents = ThisSW as SldWorks;
            _addinCookie = cookie;

            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);
            SetupUI();
            AttachDocEvents();
            Log("ConnectToSW succeeded.");
            return true;
        }

        public bool DisconnectFromSW()
        {
            DetachDocEvents();
            RemoveUI();
            _activePmp = null;
            _swAppEvents = null;
            _swApp = null;
            Log("DisconnectFromSW succeeded.");
            return true;
        }

        private void AttachDocEvents()
        {
            try
            {
                _partCleanup = new PartEventCleanUpHandler(_swApp);
                _partCleanup.AttachToActivePart();

                _upgradeMgr = new DfmUpgradeManager(_swApp, _swAppEvents);
                if (_swApp?.ActiveDoc is IModelDoc2 activeDoc)
                {
                    _upgradeMgr.QueueDocumentCheck(activeDoc);
                }

                if (_swAppEvents != null)
                {
                    _swAppEvents.ActiveDocChangeNotify += OnActiveDocChangeNotify;
                    _swAppEvents.FileOpenPostNotify += OnFileOpenPostNotify;
                }
            }
            catch { }
        }

        private void DetachDocEvents()
        {
            try
            {
                if (_swAppEvents != null)
                {
                    _swAppEvents.ActiveDocChangeNotify -= OnActiveDocChangeNotify;
                    _swAppEvents.FileOpenPostNotify -= OnFileOpenPostNotify;
                }
                _upgradeMgr?.Detach();
                _upgradeMgr = null;
                _partCleanup?.Detach();
                _partCleanup = null;
            }
            catch { }
        }

        private int OnActiveDocChangeNotify()
        {
            try
            {
                if (_swApp == null || _swApp.CommandInProgress)
                    return 0;
            }
            catch { return 0; }

            _partCleanup?.AttachToActivePart();
            return 0;
        }

        private int OnFileOpenPostNotify(string fileName)
        {
            try
            {
                if (_swApp == null || _swApp.CommandInProgress)
                    return 0;
            }
            catch { return 0; }

            _partCleanup?.AttachToActivePart();
            if (_swApp?.ActiveDoc is IModelDoc2 activeDoc)
            {
                _upgradeMgr?.QueueDocumentCheck(activeDoc);
            }
            return 0;
        }

        private void SetupUI()
        {
            try
            {
                _cmdMgr = _swApp.GetCommandManager(_addinCookie);
                if (_cmdMgr == null) return;

                int errors = 0;
                ICommandGroup cmdGroup = _cmdMgr.CreateCommandGroup2(
                    CMD_GROUP_ID,
                    "3D Print DFM",
                    "Automated Capped Teardrop Fillets & Integral Crisscross Counterbore Bridges",
                    "3D Print DFM Tools",
                    -1,
                    true,
                    ref errors);

                if (cmdGroup != null)
                {
                    cmdGroup.IconList = IconResourceHelper.GetCommandIconList();
                    cmdGroup.MainIconList = IconResourceHelper.GetMainIconList();

                    int menuToolbarOpts = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);

                    cmdGroup.AddCommandItem2(
                        "Teardrop Fillet",
                        -1,
                        "Add 40 deg Capped Teardrop Crown Reliefs to Non-Parallel Circular Holes (40-90 deg to Bed)",
                        "Teardrop Fillet",
                        0,
                        "OnLaunchTeardropFillet",
                        "",
                        101,
                        menuToolbarOpts);

                    cmdGroup.AddCommandItem2(
                        "Crisscross Bridge",
                        -1,
                        "Add 2-Tier Integral Crisscross Bridges to Downward-Facing Counterbores & Recesses",
                        "Crisscross Bridge",
                        1,
                        "OnLaunchCrisscrossBridge",
                        "",
                        102,
                        menuToolbarOpts);

                    cmdGroup.AddCommandItem2(
                        "Upgrade Legacy Features",
                        -1,
                        "Upgrade older 3D Print DFM features in this part to the latest algorithm and icons",
                        "Upgrade Legacy DFM Features",
                        0,
                        "OnUpgradeLegacyFeatures",
                        "",
                        103,
                        (int)swCommandItemType_e.swMenuItem);

                    cmdGroup.HasToolbar = true;
                    cmdGroup.HasMenu = true;
                    cmdGroup.Activate();
                }
            }
            catch (Exception ex)
            {
                Log("SetupUI error: " + ex);
            }
        }

        private void RemoveUI()
        {
            try
            {
                if (_cmdMgr != null)
                {
                    _cmdMgr.RemoveCommandGroup2(CMD_GROUP_ID, true);
                }
            }
            catch { }
        }

        public void OnLaunchTeardropFillet()
        {
            LaunchCommand(DfmCommandMode.TeardropFillet);
        }

        /// <summary>
        /// In-process COM bridge callable via ISldWorks.GetAddInObject(ADDIN_GUID)
        /// to query or drive the installed McMaster-Carr SOLIDWORKS Taskpane Add-in.
        /// </summary>
        public string InvokeMcMasterBridge(string requestJson)
        {
            return McMasterAddinBridge.ExecuteJson(_swApp, requestJson);
        }

        public void OnLaunchCrisscrossBridge()
        {
            LaunchCommand(DfmCommandMode.CrisscrossBridge);
        }

        public void OnUpgradeLegacyFeatures()
        {
            try
            {
                if (_swApp == null) return;
                IModelDoc2 model = (IModelDoc2)_swApp.ActiveDoc;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    _swApp.SendMsgToUser2(
                        "Please open a Part (.SLDPRT) document first.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }
                _upgradeMgr?.UpgradeActiveDocumentNow(model);
            }
            catch (Exception ex)
            {
                Log("OnUpgradeLegacyFeatures error: " + ex);
            }
        }

        private void LaunchCommand(DfmCommandMode mode)
        {
            try
            {
                if (_swApp == null) return;
                IModelDoc2 model = (IModelDoc2)_swApp.ActiveDoc;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    _swApp.SendMsgToUser2(
                        "Please open a Part (.SLDPRT) document first.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                _partCleanup?.AttachToActivePart();

                DfmConfig config = DfmConfig.LoadDefaults();
                _activePmp = new DfmPMPHandler(_swApp, model, null, config, mode);
                _activePmp.Show();
            }
            catch (Exception ex)
            {
                Log("LaunchCommand error: " + ex);
            }
        }

        #region COM Registration Functions
        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                string guid = t.GUID.ToString("B").ToUpper();
                string[] hklmPaths = new[]
                {
                    @"SOFTWARE\SolidWorks\AddIns\" + guid,
                    @"SOFTWARE\SolidWorks\SOLIDWORKS 2026\Addins\" + guid
                };

                foreach (string path in hklmPaths)
                {
                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey(path))
                    {
                        if (key != null)
                        {
                            key.SetValue(null, 1, RegistryValueKind.DWord);
                            key.SetValue("Default", 1, RegistryValueKind.DWord);
                            key.SetValue("Title", "3D Print DFM (Teardrops & Crisscross Bridges)", RegistryValueKind.String);
                            key.SetValue("Description", "Automated 40 deg Capped Teardrop Crown Reliefs and Integral 2-Tier Crisscross Bridges for 3D Printing", RegistryValueKind.String);
                        }
                    }
                }

                using (RegistryKey startupKey = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + guid))
                {
                    if (startupKey != null)
                    {
                        startupKey.SetValue(null, 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                string guid = t.GUID.ToString("B").ToUpper();
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\AddIns\" + guid, false);
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\SolidWorks\SOLIDWORKS 2026\Addins\" + guid, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddInsStartup\" + guid, false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\SolidWorks\AddIns\" + guid, false);
            }
            catch { }
        }
        #endregion
    }
}
