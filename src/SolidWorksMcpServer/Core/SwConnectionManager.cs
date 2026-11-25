using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    public static class SwComExecutionGate
    {
        private const string ComMutexName = @"Local\SolidWorksMcpComSerializeMutex";

        public static bool IsOfflineTestMode()
        {
            string val = System.Environment.GetEnvironmentVariable("SOLIDWORKS_MCP_OFFLINE_TEST");
            if (string.IsNullOrWhiteSpace(val))
                return false;
            val = val.Trim();
            return string.Equals(val, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetComMutexName()
        {
            string overrideName = System.Environment.GetEnvironmentVariable("SOLIDWORKS_MCP_COM_MUTEX_NAME");
            if (!string.IsNullOrWhiteSpace(overrideName))
                return overrideName.Trim();
            if (IsOfflineTestMode())
                return ComMutexName + "_Offline_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            return ComMutexName;
        }

        public static bool ShouldBypassComMutex(string toolName, Dictionary<string, object> args)
        {
            if (IsOfflineTestMode())
                return true;
            if (string.Equals(toolName, "sw_get_shop_profile", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(toolName, "sw_execute_csharp", StringComparison.OrdinalIgnoreCase) &&
                args != null && JsonHelper.GetBool(args, "compile_only", false))
                return true;
            if (string.Equals(toolName, "sw_connect_status", StringComparison.OrdinalIgnoreCase) &&
                SwSessionLockManager.ShouldBypassComMutexForConnectStatus(args))
                return true;
            return false;
        }

        public static bool TryRunSerialized<T>(int timeoutMs, Func<T> action, out T result)
        {
            if (action == null) throw new ArgumentNullException("action");
            using (Mutex mutex = new Mutex(false, GetComMutexName()))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(timeoutMs);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        result = default(T);
                        return false;
                    }

                    result = action();
                    return true;
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        public static T RunSerialized<T>(string toolName, Dictionary<string, object> args, Func<T> action, int timeoutMs = 180000)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (ShouldBypassComMutex(toolName, args))
                return action();

            using (Mutex mutex = new Mutex(false, GetComMutexName()))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = mutex.WaitOne(timeoutMs);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        throw new TimeoutException(
                            "Timed out waiting for exclusive SOLIDWORKS COM STA access (another MCP tool call is still executing). Never issue parallel SOLIDWORKS tool calls.");
                    }

                    return action();
                }
                finally
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }
    }

    public class SwConnectionManager
    {
        private const string LaunchMutexName = @"Local\SolidWorks3DExperienceLaunchMutex";
        private const int GWL_STYLE = -16;
        private const int WS_VISIBLE = 0x10000000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private const uint DESKTOP_HOOKCONTROL_ENUM_ACCESS = 0x01FF;

        [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

        private static readonly object _uiMaskLock = new object();
        private static readonly HashSet<IntPtr> _maskedControlBarHwnds = new HashSet<IntPtr>();
        private static IntPtr _cachedMainFrameHwnd = IntPtr.Zero;
        private static int _modalGuardDepth;

        private ISldWorks _swApp;

        private static bool AnySldWorksStartedRecently(Process[] procs, double withinSeconds = 120.0)
        {
            if (procs == null || procs.Length == 0)
                return false;
            DateTime now = DateTime.Now;
            foreach (Process p in procs)
            {
                try
                {
                    if ((now - p.StartTime).TotalSeconds < withinSeconds)
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static void EnsureHeadlessSafePreferences(ISldWorks app)
        {
            if (app == null) return;
            try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
            try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, true); } catch { }
            try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, false); } catch { }
            try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swPromptForAutoMateFlip, false); } catch { }
        }

        public ISldWorks GetApp(bool launchIfNotRunning = false)
        {
            if (SwComExecutionGate.IsOfflineTestMode())
                throw new InvalidOperationException("SOLIDWORKS is not running in this desktop session (offline test mode).");

            ThreeDExperienceEdgeLauncher.DismissActiveSolidWorksModalDialogs();

            if (_swApp != null)
            {
                try
                {
                    // Probe live RPC connectivity
                    string rev = _swApp.RevisionNumber();
                    if (!string.IsNullOrEmpty(rev))
                    {
                        EnsureHeadlessSafePreferences(_swApp);
                        return _swApp;
                    }
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
                {
                    EnsureHeadlessSafePreferences(_swApp);
                    return _swApp;
                }
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
                    int rotWaitSecs = AnySldWorksStartedRecently(running, 120.0) ? 75 : 20;
                    ISldWorks attached = PollRotForApp(rotWaitSecs);
                    if (attached != null)
                    {
                        EnsureHeadlessSafePreferences(attached);
                        return attached;
                    }

                    string pidList = string.Join(", ", Array.ConvertAll(running, p => p.Id.ToString(CultureInfo.InvariantCulture)));
                    throw new InvalidOperationException(
                        string.Format("SLDWORKS.exe is already running (PID: {0}) but its COM interface is not registered in the ROT yet (waited {1}s). It may still be loading add-ins, blocked on a startup/crash-recovery dialog, or running elevated (As Administrator).", pidList, rotWaitSecs));
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
            using (Mutex launchMutex = new Mutex(false, LaunchMutexName))
            {
                bool acquired = false;
                try
                {
                    try
                    {
                        acquired = launchMutex.WaitOne(120000);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired)
                    {
                        throw new TimeoutException("Timed out waiting for 3DEXPERIENCE launch lock (another launch is in progress).");
                    }

                    if (restartIfRunning)
                    {
                        _swApp = null;
                    }
                    else
                    {
                        try
                        {
                            object alreadyActive = Marshal.GetActiveObject("SldWorks.Application");
                            if (alreadyActive is ISldWorks existingSw)
                            {
                                _swApp = existingSw;
                                return new ThreeDExperienceLaunchResult
                                {
                                    Connected = true,
                                    StatusMessage = "SOLIDWORKS is already registered in the ROT."
                                };
                            }
                        }
                        catch (COMException) { }

                        Process[] runningNow = Process.GetProcessesByName("SLDWORKS");
                        if (runningNow.Length > 0)
                        {
                            int waitSecs = AnySldWorksStartedRecently(runningNow, 120.0) ? Math.Max(45, waitTimeoutSeconds) : 20;
                            ISldWorks polled = PollRotForApp(waitSecs);
                            if (polled != null)
                            {
                                return new ThreeDExperienceLaunchResult
                                {
                                    Connected = true,
                                    LaunchedProcess = true,
                                    StatusMessage = "Attached to SLDWORKS.exe after ROT startup."
                                };
                            }
                        }
                    }

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
                finally
                {
                    if (acquired)
                    {
                        try { launchMutex.ReleaseMutex(); } catch { }
                    }
                }
            }
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
            string cleanBase = Path.GetFileNameWithoutExtension(clean);
            string titleBase = Path.GetFileNameWithoutExtension(title);
            return string.Equals(path, clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileName(path), clean, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(title, clean, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrEmpty(titleBase) && !string.IsNullOrEmpty(cleanBase) &&
                    string.Equals(titleBase, cleanBase, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(cleanBase) &&
                    string.Equals(Path.GetFileNameWithoutExtension(path), cleanBase, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ActivateDocumentSafe(ISldWorks app, IModelDoc2 doc)
        {
            if (app == null || doc == null) return false;
            string docPath = doc.GetPathName() ?? string.Empty;
            string docTitle = doc.GetTitle() ?? string.Empty;
            IModelDoc2 active = null;
            try { active = app.ActiveDoc as IModelDoc2; } catch { }
            if (active != null &&
                (MatchesDocumentIdentifier(active, docPath) || MatchesDocumentIdentifier(active, docTitle)))
            {
                return true;
            }
            string activateTarget = !string.IsNullOrEmpty(docPath) ? Path.GetFileName(docPath) : docTitle;
            int errors = 0;
            object activated = app.ActivateDoc3(
                activateTarget,
                false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref errors);
            return activated != null || errors == 0;
        }

        internal static void RunOnInteractiveDesktop(Action action)
        {
            if (action == null)
                return;

            bool invoked = false;
            IntPtr hDesktop = IntPtr.Zero;
            try
            {
                hDesktop = OpenDesktopW("Default", 0, false, DESKTOP_HOOKCONTROL_ENUM_ACCESS);
                if (hDesktop != IntPtr.Zero)
                {
                    EnumDesktopWindows(hDesktop, (hWnd, lParam) =>
                    {
                        invoked = true;
                        action();
                        return false;
                    }, IntPtr.Zero);
                }
            }
            catch { }
            finally
            {
                if (hDesktop != IntPtr.Zero)
                {
                    try { CloseDesktop(hDesktop); } catch { }
                }
            }

            if (!invoked)
            {
                try { action(); } catch { }
            }
        }

        private static IntPtr FindMdiClientChild(IntPtr hWndMainFrame)
        {
            if (hWndMainFrame == IntPtr.Zero || !IsWindow(hWndMainFrame))
                return IntPtr.Zero;
            IntPtr mdi = FindWindowExW(hWndMainFrame, IntPtr.Zero, "swMdiClient", null);
            if (mdi == IntPtr.Zero)
                mdi = FindWindowExW(hWndMainFrame, IntPtr.Zero, "MDIClient", null);
            return mdi;
        }

        private static IntPtr ResolveMainFrameHwnd(ISldWorks app)
        {
            lock (_uiMaskLock)
            {
                if (_cachedMainFrameHwnd != IntPtr.Zero &&
                    IsWindow(_cachedMainFrameHwnd) &&
                    FindMdiClientChild(_cachedMainFrameHwnd) != IntPtr.Zero)
                {
                    return _cachedMainFrameHwnd;
                }
                _cachedMainFrameHwnd = IntPtr.Zero;
            }

            IntPtr found = IntPtr.Zero;
            try
            {
                Process[] swProcs = Process.GetProcessesByName("SLDWORKS");
                if (swProcs != null && swProcs.Length > 0)
                {
                    HashSet<uint> pids = new HashSet<uint>();
                    foreach (Process p in swProcs)
                    {
                        try { pids.Add((uint)p.Id); } catch { }
                    }

                    if (pids.Count > 0)
                    {
                        EnumWindows((hWnd, lParam) =>
                        {
                            uint pid = 0;
                            GetWindowThreadProcessId(hWnd, out pid);
                            if (!pids.Contains(pid))
                                return true;

                            StringBuilder cls = new StringBuilder(128);
                            GetClassNameW(hWnd, cls, cls.Capacity);
                            string clsName = cls.ToString();
                            if (clsName.StartsWith("Afx:", StringComparison.Ordinal))
                            {
                                if (FindMdiClientChild(hWnd) != IntPtr.Zero)
                                {
                                    found = hWnd;
                                    return false;
                                }
                            }
                            return true;
                        }, IntPtr.Zero);
                    }
                }
            }
            catch { }

            if (found == IntPtr.Zero && app != null)
            {
                try
                {
                    IFrame frame = app.Frame() as IFrame;
                    if (frame != null)
                    {
                        long hwndVal = 0;
                        try { hwndVal = frame.GetHWndx64(); } catch { }
                        if (hwndVal == 0)
                        {
                            try { hwndVal = frame.GetHWnd(); } catch { }
                        }
                        if (hwndVal != 0)
                        {
                            IntPtr candidate = new IntPtr(hwndVal);
                            if (IsWindow(candidate))
                                found = candidate;
                        }
                    }
                }
                catch { }
            }

            if (found != IntPtr.Zero)
            {
                lock (_uiMaskLock)
                {
                    _cachedMainFrameHwnd = found;
                }
            }
            return found;
        }

        public static void MaskNonMdiControlBarsVisible(ISldWorks app = null)
        {
            if (SwComExecutionGate.IsOfflineTestMode())
                return;

            RunOnInteractiveDesktop(() =>
            {
                IntPtr hWndMainFrame = ResolveMainFrameHwnd(app);
                if (hWndMainFrame == IntPtr.Zero || !IsWindow(hWndMainFrame))
                    return;

                IntPtr hWndMdiClient = FindMdiClientChild(hWndMainFrame);
                List<IntPtr> candidates = new List<IntPtr>();
                try
                {
                    EnumChildWindows(hWndMainFrame, (child, lParam) =>
                    {
                        if (child == IntPtr.Zero || child == hWndMdiClient)
                            return true;
                        if (hWndMdiClient != IntPtr.Zero && IsChild(hWndMdiClient, child))
                            return true;
                        int style = GetWindowLongW(child, GWL_STYLE);
                        if ((style & WS_VISIBLE) != 0)
                            candidates.Add(child);
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                foreach (IntPtr child in candidates)
                {
                    try
                    {
                        if (!IsWindow(child)) continue;
                        int style = GetWindowLongW(child, GWL_STYLE);
                        if ((style & WS_VISIBLE) != 0)
                        {
                            SetWindowLongW(child, GWL_STYLE, style & ~WS_VISIBLE);
                            lock (_uiMaskLock)
                            {
                                _maskedControlBarHwnds.Add(child);
                            }
                        }
                    }
                    catch { }
                }

                // Also clear WS_VISIBLE on hWndMainFrame itself so that even if SOLIDWORKS
                // internally calls ShowWindow(swCMBar->m_hWnd, SW_SHOW) during InsertSketch
                // or FeatureRevolve2 (sldappu+0x0089479C / 0x00881DD0) and immediately sends
                // 0x302D -> WM_IDLEUPDATECMDUI (0x0363) -> sldappu+0x00882110 before returning
                // to managed code, USER32!IsWindowVisible(swCMBar->m_hWnd) at +0x0088216B walks
                // GetParent(swCMBar->m_hWnd) == hWndMainFrame and still returns FALSE (0).
                try
                {
                    int mainStyle = GetWindowLongW(hWndMainFrame, GWL_STYLE);
                    if ((mainStyle & WS_VISIBLE) != 0)
                    {
                        SetWindowLongW(hWndMainFrame, GWL_STYLE, mainStyle & ~WS_VISIBLE);
                        lock (_uiMaskLock)
                        {
                            _maskedControlBarHwnds.Add(hWndMainFrame);
                        }
                    }
                }
                catch { }
            });
        }

        public static void ClearDatumPlaneSelectionsIfIdle(ISldWorks app)
        {
            if (app == null || SwComExecutionGate.IsOfflineTestMode())
                return;

            try
            {
                IModelDoc2 activeDoc = app.ActiveDoc as IModelDoc2;
                if (activeDoc == null)
                    return;

                ISketchManager sm = activeDoc.SketchManager;
                if (sm != null && sm.ActiveSketch != null)
                    return;

                ISelectionMgr selMgr = activeDoc.SelectionManager as ISelectionMgr;
                if (selMgr == null)
                    return;

                int count = selMgr.GetSelectedObjectCount2(-1);
                for (int i = 1; i <= count; i++)
                {
                    if (selMgr.GetSelectedObjectType3(i, -1) == (int)swSelectType_e.swSelDATUMPLANES)
                    {
                        activeDoc.ClearSelection2(true);
                        break;
                    }
                }
            }
            catch { }
        }

        public static void RestoreNonMdiControlBarsVisible(ISldWorks app = null, bool force = false)
        {
            if (SwComExecutionGate.IsOfflineTestMode())
                return;

            if (!force && app != null)
            {
                try
                {
                    IModelDoc2 activeDoc = app.ActiveDoc as IModelDoc2;
                    ISketchManager sm = activeDoc != null ? activeDoc.SketchManager : null;
                    if (sm != null && sm.ActiveSketch != null)
                        return;
                }
                catch { }
            }

            ClearDatumPlaneSelectionsIfIdle(app);

            List<IntPtr> toRestore;
            lock (_uiMaskLock)
            {
                if (_maskedControlBarHwnds.Count == 0)
                    return;
                toRestore = new List<IntPtr>(_maskedControlBarHwnds);
                _maskedControlBarHwnds.Clear();
            }

            RunOnInteractiveDesktop(() =>
            {
                foreach (IntPtr child in toRestore)
                {
                    try
                    {
                        if (child != IntPtr.Zero && IsWindow(child))
                        {
                            int style = GetWindowLongW(child, GWL_STYLE);
                            if ((style & WS_VISIBLE) == 0)
                                SetWindowLongW(child, GWL_STYLE, style | WS_VISIBLE);
                        }
                    }
                    catch { }
                }
            });
        }

        public static T RunWithModalGuard<T>(ISldWorks app, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (app == null) return action();

            // NOTE: Never enable CommandInProgress during CAD modeling.
            // In SOLIDWORKS 2026 SP04 it enables uiBlockMan_c and triggers a native Access Violation
            // in slduiu:uiModelDoc_c::conditionalBlockCmd when InsertSketch updates the ribbon.
            bool enteredDepth = false;
            bool prevExtRef = false, hasExtRef = false;
            bool prevShowErr = true, hasShowErr = false;
            bool prevAutoFlip = false, hasAutoFlip = false;
            bool prevTaskPanePinned = false, hasTaskPanePinned = false;
            bool rpcDisconnected = false;

            try
            {
                lock (_uiMaskLock)
                {
                    _modalGuardDepth++;
                }
                enteredDepth = true;
                MaskNonMdiControlBarsVisible(app);

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
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                }
                catch { }

                try
                {
                    prevTaskPanePinned = app.TaskPaneIsPinned;
                    hasTaskPanePinned = true;
                    if (prevTaskPanePinned)
                        app.TaskPaneIsPinned = false;
                }
                catch { }

                return action();
            }
            catch (Exception ex)
            {
                if (IsRpcDisconnectOrServerCrash(ex, out _, out _))
                    rpcDisconnected = true;
                throw;
            }
            finally
            {
                int remainingDepth = 0;
                if (enteredDepth)
                {
                    lock (_uiMaskLock)
                    {
                        if (_modalGuardDepth > 0)
                            _modalGuardDepth--;
                        remainingDepth = _modalGuardDepth;
                        if (rpcDisconnected)
                        {
                            _maskedControlBarHwnds.Clear();
                            _cachedMainFrameHwnd = IntPtr.Zero;
                        }
                    }
                }

                if (!rpcDisconnected)
                {
                    if (hasTaskPanePinned && prevTaskPanePinned) { try { app.TaskPaneIsPinned = true; } catch { } }
                    try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
                    if (hasAutoFlip) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swPromptForAutoMateFlip, prevAutoFlip); } catch { } }
                    if (hasShowErr) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swShowErrorsEveryRebuild, prevShowErr); } catch { } }
                    if (hasExtRef) { try { app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefNoPromptOrSave, prevExtRef); } catch { } }

                    if (remainingDepth == 0)
                        RestoreNonMdiControlBarsVisible(app, force: false);
                }
            }
        }

        public static bool IsRpcDisconnectOrServerCrash(Exception ex, out int hresult, out string hresultHex)
        {
            hresult = 0;
            hresultHex = null;
            Exception cur = ex;
            while (cur != null)
            {
                if (cur is ExternalException extEx)
                {
                    int hr = extEx.ErrorCode;
                    if (hr == unchecked((int)0x800706BA) ||
                        hr == unchecked((int)0x800706BE) ||
                        hr == unchecked((int)0x800706BF) ||
                        hr == unchecked((int)0x80010105) ||
                        hr == unchecked((int)0x80010108))
                    {
                        hresult = hr;
                        hresultHex = "0x" + unchecked((uint)hr).ToString("X8", CultureInfo.InvariantCulture);
                        return true;
                    }
                }

                string msg = cur.Message ?? string.Empty;
                if (msg.IndexOf("0x800706BA", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hresult = unchecked((int)0x800706BA);
                    hresultHex = "0x800706BA";
                    return true;
                }
                if (msg.IndexOf("0x800706BE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hresult = unchecked((int)0x800706BE);
                    hresultHex = "0x800706BE";
                    return true;
                }
                if (msg.IndexOf("0x800706BF", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hresult = unchecked((int)0x800706BF);
                    hresultHex = "0x800706BF";
                    return true;
                }
                if (msg.IndexOf("0x80010105", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hresult = unchecked((int)0x80010105);
                    hresultHex = "0x80010105";
                    return true;
                }
                if (msg.IndexOf("0x80010108", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hresult = unchecked((int)0x80010108);
                    hresultHex = "0x80010108";
                    return true;
                }

                cur = cur.InnerException;
            }
            return false;
        }

        public IModelDoc2 OpenOrImport(ISldWorks app, string filePath, out string warnings)
        {
            warnings = null;
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found: " + filePath);

            IModelDoc2 active = null;
            try { active = app.ActiveDoc as IModelDoc2; } catch { }
            if (active != null && MatchesDocumentIdentifier(active, filePath))
                return active;

            try
            {
                object[] docs = app.GetDocuments() as object[];
                if (docs != null)
                {
                    foreach (object dObj in docs)
                    {
                        if (dObj is IModelDoc2 existing && MatchesDocumentIdentifier(existing, filePath))
                        {
                            ActivateDocumentSafe(app, existing);
                            return existing;
                        }
                    }
                }
            }
            catch { }

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

            return RunWithModalGuard(_swApp, () =>
            {
                Tools.SwHelper.EnsureAppBound(_swApp, model);
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
                catch (Exception ex)
                {
                    if (IsRpcDisconnectOrServerCrash(ex, out _, out _))
                    {
                        _swApp = null;
                        throw;
                    }
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
            });
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

        public static bool TrySelectOriginPoint(IModelDoc2 model, bool append = false, int mark = 0)
        {
            if (model == null) return false;
            try
            {
                if (model.Extension.SelectByID2("Point1@Origin", "EXTSKETCHPOINT", 0, 0, 0, append, mark, null, 0))
                    return true;
                if (model.Extension.SelectByID2("", "EXTSKETCHPOINT", 0, 0, 0, append, mark, null, 0))
                    return true;
                if (model.Extension.SelectByID2("Origin", "ORIGIN", 0, 0, 0, append, mark, null, 0))
                    return true;
                IFeature f = model.FirstFeature() as IFeature;
                while (f != null)
                {
                    if (string.Equals(f.GetTypeName2(), "OriginProfileFeature", StringComparison.OrdinalIgnoreCase))
                        return f.Select2(append, mark);
                    f = f.GetNextFeature() as IFeature;
                }
            }
            catch { }
            return false;
        }

        public static bool TrySelectByToken(IModelDoc2 model, string token, bool append = false, int mark = 0)
        {
            if (model == null || string.IsNullOrWhiteSpace(token))
                return false;

            string trimmed = token.Trim();
            if (string.Equals(trimmed, "Origin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Point1@Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (TrySelectOriginPoint(model, append, mark))
                    return true;
            }

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

        /// <summary>
        /// Projects a 3D model-space point (meters) into active 2D sketch coordinates (meters)
        /// using cached ModelToSketchTransform.ArrayData math in pure C# (zero per-point COM calls).
        /// </summary>
        public static void MapPointToSketch2D(double[] modelToSketchArrayData, double mx, double my, double mz, out double sx, out double sy)
        {
            if (modelToSketchArrayData == null || modelToSketchArrayData.Length < 12)
            {
                sx = mx;
                sy = my;
                return;
            }
            double[] pt = AssemblyGeometryHelper.TransformPointFast(modelToSketchArrayData, mx, my, mz);
            sx = pt[0];
            sy = pt[1];
        }

        public static void MapPointToSketch2D(IMathUtility mathUtil, IMathTransform modelToSketch, double mx, double my, double mz, out double sx, out double sy)
        {
            double[] arr = modelToSketch != null ? modelToSketch.ArrayData as double[] : null;
            MapPointToSketch2D(arr, mx, my, mz, out sx, out sy);
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

            string userDefault = app != null ? app.GetUserPreferenceStringValue(pref) : null;
            if (!string.IsNullOrWhiteSpace(userDefault) && File.Exists(userDefault))
                return userDefault;

            string candidate = Path.Combine(swTemplateDir, file);
            if (File.Exists(candidate))
                return candidate;

            return string.Empty;
        }

        public static void ResolveSheetPaperGeometry(string sheetSize, out int paperEnum, out double widthM, out double heightM, out string defaultFormatFileName)
        {
            string s = (sheetSize ?? "tabloid_11x17").Trim().ToLowerInvariant();
            switch (s)
            {
                case "letter_8_5x11":
                case "a_landscape":
                case "a - landscape":
                    paperEnum = (int)swDwgPaperSizes_e.swDwgPaperAsize;
                    widthM = 0.2794;
                    heightM = 0.2159;
                    defaultFormatFileName = "a - landscape.slddrt";
                    break;
                case "letter_portrait":
                case "a_portrait":
                case "a - portrait":
                    paperEnum = (int)swDwgPaperSizes_e.swDwgPaperAsizeVertical;
                    widthM = 0.2159;
                    heightM = 0.2794;
                    defaultFormatFileName = "a - portrait.slddrt";
                    break;
                case "c_landscape":
                case "c - landscape":
                    paperEnum = (int)swDwgPaperSizes_e.swDwgPaperCsize;
                    widthM = 0.5588;
                    heightM = 0.4318;
                    defaultFormatFileName = "c - landscape.slddrt";
                    break;
                case "d_landscape":
                case "d - landscape":
                    paperEnum = (int)swDwgPaperSizes_e.swDwgPaperDsize;
                    widthM = 0.8636;
                    heightM = 0.5588;
                    defaultFormatFileName = "d - landscape.slddrt";
                    break;
                default:
                    paperEnum = (int)swDwgPaperSizes_e.swDwgPaperBsize;
                    widthM = 0.4318;
                    heightM = 0.2794;
                    defaultFormatFileName = "b - landscape.slddrt";
                    break;
            }
        }

        public static string ResolveDefaultSheetFormat(ISldWorks app, string sheetSize, string explicitFormatPath = null)
        {
            ResolveSheetPaperGeometry(sheetSize, out _, out _, out _, out string defaultFile);
            if (!string.IsNullOrWhiteSpace(explicitFormatPath) && File.Exists(explicitFormatPath))
                return explicitFormatPath;

            string targetFileName = !string.IsNullOrWhiteSpace(explicitFormatPath)
                ? (explicitFormatPath.EndsWith(".slddrt", StringComparison.OrdinalIgnoreCase) ? explicitFormatPath : (explicitFormatPath + ".slddrt"))
                : defaultFile;

            List<string> searchDirs = new List<string>();
            if (app != null)
            {
                try
                {
                    string prefDirs = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsSheetFormat);
                    if (!string.IsNullOrWhiteSpace(prefDirs))
                    {
                        foreach (string d in prefDirs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            searchDirs.Add(d.Trim());
                    }
                }
                catch { }
            }
            searchDirs.Add(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2026\lang\english\sheetformat");
            searchDirs.Add(@"C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\lang\english\sheetformat");
            searchDirs.Add(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\english\sheetformat");

            foreach (string dir in searchDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, Path.GetFileName(targetFileName));
                    if (File.Exists(candidate))
                        return candidate;
                    string fallback = Path.Combine(dir, defaultFile);
                    if (File.Exists(fallback))
                        return fallback;
                }
                catch { }
            }
            return string.Empty;
        }

        public static string ResolveDefaultBomTemplate(ISldWorks app, string explicitBomTemplatePath = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitBomTemplatePath) && File.Exists(explicitBomTemplatePath))
                return explicitBomTemplatePath;

            string targetFileName = !string.IsNullOrWhiteSpace(explicitBomTemplatePath)
                ? (explicitBomTemplatePath.EndsWith(".sldbomtbt", StringComparison.OrdinalIgnoreCase) ? explicitBomTemplatePath : (explicitBomTemplatePath + ".sldbomtbt"))
                : "bom-standard.sldbomtbt";

            List<string> searchDirs = new List<string>();
            if (app != null)
            {
                try
                {
                    string prefDirs = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsBOMTemplates);
                    if (!string.IsNullOrWhiteSpace(prefDirs))
                    {
                        foreach (string d in prefDirs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                            searchDirs.Add(d.Trim());
                    }
                }
                catch { }
            }
            searchDirs.Add(@"C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\lang\english");
            searchDirs.Add(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2026\lang\english");
            searchDirs.Add(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\lang\english");

            foreach (string dir in searchDirs)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, Path.GetFileName(targetFileName));
                    if (File.Exists(candidate))
                        return candidate;
                    string fallback = Path.Combine(dir, "bom-standard.sldbomtbt");
                    if (File.Exists(fallback))
                        return fallback;
                }
                catch { }
            }
            return string.Empty;
        }
    }
}
