using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SolidWorksJoineryAddin.Data;
using SolidWorksJoineryAddin.MacroFeatures;
using SolidWorksJoineryAddin.UI;

namespace SolidWorksJoineryAddin
{
    [ComVisible(true)]
    [Guid("C924295D-2877-4D90-95FE-3BE5C2A5405A")]
    public class SwAddin : ISwAddin
    {
        public const string ADDIN_GUID = "{C924295D-2877-4D90-95FE-3BE5C2A5405A}";
        private ISldWorks _swApp;
        private SldWorks _swAppEvents;
        private int _addinCookie;
        private ICommandManager _cmdMgr;
        private PartEventCleanUpHandler _partCleanup;
        private JoineryUpgradeManager _upgradeMgr;
        private static JoineryPMPHandler _activePmp;
        private const int CMD_GROUP_ID = 5001;

        public static void Log(string message)
        {
            try
            {
                string dir = JointGroupDefinition.GetAppDataDir();
                string logPath = Path.Combine(dir, "joinery_debug.log");
                File.AppendAllText(logPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}",
                    DateTime.Now, message, System.Environment.NewLine));
            }
            catch { }
        }

        public static void LaunchPmpForEdit(
            ISldWorks swApp,
            IModelDoc2 model,
            IFeature existingMacroFeat,
            JointGroupDefinition group)
        {
            _activePmp = new JoineryPMPHandler(swApp, model, existingMacroFeat, group);
            _activePmp.Show();
        }

        public bool ConnectToSW(object ThisSW, int cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _swAppEvents = ThisSW as SldWorks;
            _addinCookie = cookie;

            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);
            ProjectSettings.Load();
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

                _upgradeMgr = new JoineryUpgradeManager(_swApp, _swAppEvents);
                if (_swApp?.ActiveDoc is IModelDoc2 activeDoc)
                {
                    _upgradeMgr.QueueDocumentCheck(activeDoc);
                }

                if (_swAppEvents != null)
                {
                    _swAppEvents.ActiveDocChangeNotify += OnActiveDocChangeNotify;
                    _swAppEvents.FileOpenPostNotify += OnFileOpenPostNotify;
                    _swAppEvents.OnIdleNotify += OnSwIdleNotify;
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
                    _swAppEvents.OnIdleNotify -= OnSwIdleNotify;
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
            _partCleanup?.AttachToActivePart();
            return 0;
        }

        private int OnFileOpenPostNotify(string fileName)
        {
            _partCleanup?.AttachToActivePart();
            if (_swApp?.ActiveDoc is IModelDoc2 activeDoc)
            {
                _upgradeMgr?.QueueDocumentCheck(activeDoc);
            }
            return 0;
        }

        private int OnSwIdleNotify()
        {
            _partCleanup?.ProcessPendingCleanup();
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
                    "Multi-Body Joinery",
                    "Automated Tab & Slot, Finger Joints, and Dados with Dogbone Relief",
                    "Multi-Body Joinery",
                    -1,
                    true,
                    ref errors);

                if (cmdGroup != null)
                {
                    cmdGroup.IconList = IconResourceHelper.GetCommandIconList();
                    cmdGroup.MainIconList = IconResourceHelper.GetMainIconList();

                    cmdGroup.AddCommandItem2(
                        "Joinery Manager",
                        -1,
                        "Open Multi-Body Joinery PropertyManager Page",
                        "Multi-Body Joinery Manager",
                        0,
                        "OnLaunchJoineryManager",
                        "",
                        0,
                        (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

                    cmdGroup.AddCommandItem2(
                        "Upgrade Legacy Features",
                        -1,
                        "Upgrade older Multi-Body Joinery features in this part to the latest algorithm and icons",
                        "Upgrade Legacy Joinery Features",
                        0,
                        "OnUpgradeLegacyFeatures",
                        "",
                        1,
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

        public void OnLaunchJoineryManager()
        {
            try
            {
                if (_swApp == null) return;
                IModelDoc2 model = (IModelDoc2)_swApp.ActiveDoc;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    _swApp.SendMsgToUser2(
                        "Please open a Part (.SLDPRT) document before launching the Joinery Manager.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                _partCleanup?.AttachToActivePart();

                JointGroupDefinition group = ProjectSettings.CreateDefaultGroup();
                _activePmp = new JoineryPMPHandler(_swApp, model, null, group);
                _activePmp.Show();
            }
            catch (Exception ex)
            {
                Log("OnLaunchJoineryManager error: " + ex);
            }
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
                            key.SetValue("Title", "Multi-Body Joinery Generator", RegistryValueKind.String);
                            key.SetValue("Description", "Parametric Multi-Body Tab & Slot, Finger Joint, and Dado generator with Diagonal Dogbones and Poka-Yoke Keying", RegistryValueKind.String);
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
