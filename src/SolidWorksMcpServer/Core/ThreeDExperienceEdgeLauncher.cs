using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using SolidWorks.Interop.sldworks;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    public class ThreeDExperienceLaunchResult
    {
        public bool Connected { get; set; }
        public bool LaunchedProcess { get; set; }
        public bool Restarted { get; set; }
        public string EdgeWindowTitle { get; set; }
        public string CurrentUrl { get; set; }
        public string ClickedTarget { get; set; }
        public string StatusMessage { get; set; }
        public List<string> Logs { get; set; } = new List<string>();
    }

    /// <summary>
    /// Automates 3DEXPERIENCE login (via saved Microsoft Edge credentials) and launches
    /// "SOLIDWORKS Design" under My Favorite Apps using Windows UIAutomation.
    /// </summary>
    public static class ThreeDExperienceEdgeLauncher
    {
        public const string DefaultLoginUrl =
            "https://eu1-ds-iam.3dexperience.3ds.com/cas/login?service=https%3A//usw1-makers-ifwe.3dexperience.3ds.com/";

        public const string DefaultAppName = "SOLIDWORKS Design";

        private static readonly string[] LauncherExeCandidates = new[]
        {
            @"C:\Program Files\Dassault Systemes\3DEXPERIENCE Launcher\3DEXPERIENCELauncher.exe"
        };

        private static readonly string[] EdgeExeCandidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        };

        #region Win32 & Accessibility Interop

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint dwId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const byte VK_RETURN = 0x0D;
        private const byte VK_ESCAPE = 0x1B;
        private const byte VK_DOWN = 0x28;
        private const uint OBJID_CLIENT = 0xFFFFFFFC;
        private static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        #endregion

        public static string ResolveEdgeExecutable()
        {
            foreach (string path in EdgeExeCandidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public static void EnsureLauncherDaemonRunning(List<string> logs)
        {
            try
            {
                Process[] backbone = Process.GetProcessesByName("3DEXPERIENCELauncherBackbone");
                Process[] launcher = Process.GetProcessesByName("3DEXPERIENCELauncher");
                if (backbone.Length > 0 || launcher.Length > 0)
                {
                    logs.Add("3DEXPERIENCELauncher service already running.");
                    return;
                }

                foreach (string candidate in LauncherExeCandidates)
                {
                    if (File.Exists(candidate))
                    {
                        Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                        logs.Add("Started 3DEXPERIENCELauncher.exe daemon.");
                        Thread.Sleep(1500);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                logs.Add("Note: could not pre-start 3DEXPERIENCELauncher: " + ex.Message);
            }
        }

        public static ThreeDExperienceLaunchResult LaunchOrRestartViaEdge(
            bool restartIfRunning,
            bool forceRestart,
            int waitTimeoutSeconds,
            string loginUrl = null,
            string appName = null)
        {
            ThreeDExperienceLaunchResult result = new ThreeDExperienceLaunchResult();
            string targetUrl = string.IsNullOrWhiteSpace(loginUrl) ? DefaultLoginUrl : loginUrl.Trim();
            string targetApp = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName.Trim();
            int waitSecs = Math.Max(30, waitTimeoutSeconds);

            // 1. Check existing SOLIDWORKS instance
            Process[] existingSw = Process.GetProcessesByName("SLDWORKS");
            if (existingSw.Length > 0)
            {
                if (!restartIfRunning)
                {
                    result.StatusMessage = string.Format(
                        "SLDWORKS.exe is already running (PID {0}).", existingSw[0].Id);
                    result.Logs.Add(result.StatusMessage);
                    result.LaunchedProcess = true;
                    return result;
                }

                CloseExistingSolidWorks(forceRestart, result.Logs);
                result.Restarted = true;
            }

            // 2. Ensure local 3DEXPERIENCE Launcher daemon is up
            EnsureLauncherDaemonRunning(result.Logs);

            // 3. Drive Edge on an MTA thread (reusing existing 3DEXPERIENCE tab if present, or opening login URL)
            Exception workerError = null;
            Thread uiaThread = new Thread(() =>
            {
                try
                {
                    RunEdgeAutomationFlow(
                        targetUrl,
                        targetApp,
                        waitSecs,
                        result);
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            });
            uiaThread.SetApartmentState(ApartmentState.MTA);
            uiaThread.IsBackground = true;
            uiaThread.Start();
            uiaThread.Join();

            if (workerError != null)
                throw workerError;

            return result;
        }

        public static string InspectEdgeWindowJson(string targetUrlHint = null)
        {
            List<string> logs = new List<string>();
            object payload = null;
            Exception workerError = null;

            Thread uiaThread = new Thread(() =>
            {
                try
                {
                    List<IntPtr> windows = FindVisibleEdgeWindows();
                    IntPtr hwnd = SelectExisting3DExperienceTab(windows, logs);
                    if (hwnd == IntPtr.Zero)
                        hwnd = PickBestEdgeWindow(windows, null, logs);

                    if (hwnd == IntPtr.Zero)
                    {
                        payload = new { found_edge_window = false, edge_window_count = windows.Count, logs };
                        return;
                    }

                    WakeUpChromiumAccessibility(hwnd, logs);
                    AutomationElement winEl = AutomationElement.FromHandle(hwnd);
                    string title = GetWindowTitleText(hwnd);
                    string url = TryReadEdgeAddressBar(winEl);
                    AutomationElement docEl = FindRootWebArea(winEl);
                    if (docEl != null)
                    {
                        try
                        {
                            if (TreeWalker.RawViewWalker.GetFirstChild(docEl) == null)
                            {
                                try { docEl.SetFocus(); } catch { }
                                Thread.Sleep(500);
                            }
                        }
                        catch { }
                    }

                    List<object> items = new List<object>();
                    if (docEl != null)
                    {
                        WalkRawDom(docEl, (el, cur, depth) =>
                        {
                            if (items.Count >= 160)
                                return false;

                            ControlType ct = cur.ControlType;
                            string name = cur.Name ?? "";
                            string cls = cur.ClassName ?? "";
                            string aid = cur.AutomationId ?? "";
                            bool isInteresting =
                                ct == ControlType.Button ||
                                ct == ControlType.Edit ||
                                ct == ControlType.Hyperlink ||
                                ct == ControlType.Text ||
                                ct == ControlType.ListItem ||
                                ct == ControlType.MenuItem ||
                                ct == ControlType.TabItem ||
                                !string.IsNullOrEmpty(name) ||
                                !string.IsNullOrEmpty(aid) ||
                                cls.IndexOf("item-view", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cls.IndexOf("favorite", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!isInteresting) return true;

                            Rect r = cur.BoundingRectangle;
                            if (r.IsEmpty || double.IsInfinity(r.Left) || (r.Width <= 0 && r.Height <= 0))
                                return true;

                            items.Add(new
                            {
                                depth,
                                control_type = ct != null ? ct.ProgrammaticName : "",
                                name,
                                automation_id = aid,
                                class_name = cls,
                                bounds = new
                                {
                                    left = Math.Round(r.Left, 1),
                                    top = Math.Round(r.Top, 1),
                                    width = Math.Round(r.Width, 1),
                                    height = Math.Round(r.Height, 1)
                                }
                            });
                            return true;
                        }, maxDepth: 28, maxNodes: 1000);
                    }

                    payload = new
                    {
                        found_edge_window = true,
                        hwnd = hwnd.ToInt64(),
                        title,
                        url,
                        document_found = docEl != null,
                        document_name = docEl != null ? (docEl.Current.Name ?? "") : "",
                        element_count = items.Count,
                        elements = items,
                        logs
                    };
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            });
            uiaThread.SetApartmentState(ApartmentState.MTA);
            uiaThread.IsBackground = true;
            uiaThread.Start();
            uiaThread.Join();

            if (workerError != null)
                throw workerError;

            return JsonHelper.Serialize(payload);
        }

        private static void CloseExistingSolidWorks(bool forceRestart, List<string> logs)
        {
            ISldWorks runningApp = null;
            try
            {
                runningApp = Marshal.GetActiveObject("SldWorks.Application") as ISldWorks;
            }
            catch { }

            if (runningApp != null && !forceRestart)
            {
                object[] docs = null;
                try { docs = runningApp.GetDocuments() as object[]; } catch { }
                if (docs != null)
                {
                    List<string> dirtyDocs = new List<string>();
                    foreach (object dObj in docs)
                    {
                        if (dObj is IModelDoc2 doc)
                        {
                            try
                            {
                                if (doc.GetSaveFlag())
                                {
                                    string title = doc.GetTitle();
                                    dirtyDocs.Add(string.IsNullOrWhiteSpace(title) ? "(unnamed)" : title);
                                }
                            }
                            catch { }
                        }
                    }

                    if (dirtyDocs.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "Cannot restart SOLIDWORKS automatically because open document(s) have unsaved changes: " +
                            string.Join(", ", dirtyDocs) +
                            ". Save them first via sw_save_document or pass force_restart=true.");
                    }
                }
            }

            foreach (Process proc in Process.GetProcessesByName("SLDWORKS"))
            {
                try { DismissSolidWorksChildPopups(proc.Id, proc.MainWindowHandle, logs); } catch { }
            }

            if (runningApp != null)
            {
                logs.Add("Requesting graceful SOLIDWORKS exit via ISldWorks.ExitApp()...");
                Thread exitThread = new Thread(() =>
                {
                    try { runningApp.ExitApp(); } catch { }
                });
                exitThread.SetApartmentState(ApartmentState.STA);
                exitThread.IsBackground = true;
                exitThread.Start();
            }
            else
            {
                foreach (Process proc in Process.GetProcessesByName("SLDWORKS"))
                {
                    try
                    {
                        logs.Add(string.Format("Requesting graceful SOLIDWORKS close via CloseMainWindow() on PID {0}...", proc.Id));
                        proc.CloseMainWindow();
                    }
                    catch { }
                }
            }

            // Wait up to 25s for SOLIDWORKS Connected to check its seat back into 3DEXPERIENCE and exit cleanly.
            // NEVER force-kill SLDWORKS.exe (Process.Kill / Stop-Process -Force / taskkill /F), which orphans the cloud license for 15-20 minutes.
            Stopwatch swExit = Stopwatch.StartNew();
            while (swExit.Elapsed.TotalSeconds < 25.0)
            {
                Process[] active = Process.GetProcessesByName("SLDWORKS");
                if (active.Length == 0)
                    break;

                foreach (Process proc in active)
                {
                    try { DismissSolidWorksChildPopups(proc.Id, proc.MainWindowHandle, logs); } catch { }
                }
                Thread.Sleep(500);
            }

            Process[] remaining = Process.GetProcessesByName("SLDWORKS");
            if (remaining.Length > 0)
            {
                string pids = string.Join(", ", Array.ConvertAll(remaining, p => p.Id.ToString()));
                throw new InvalidOperationException(
                    "SLDWORKS.exe (PID " + pids + ") did not finish exiting within 25s. " +
                    "Refusing to force-kill SLDWORKS.exe so the 3DEXPERIENCE cloud license seat can check in cleanly without a 15-20 minute lock. " +
                    "Resolve or save any open prompt in SOLIDWORKS and retry.");
            }

            Thread.Sleep(1500);
            logs.Add("Previous SOLIDWORKS process exited gracefully and checked in its 3DEXPERIENCE seat.");
        }

        private static void DismissSolidWorksChildPopups(int pid, IntPtr mainWindowHandle, List<string> logs)
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint winPid);
                    if ((int)winPid != pid)
                        return true;

                    StringBuilder clsSb = new StringBuilder(128);
                    GetClassName(hWnd, clsSb, clsSb.Capacity);
                    string cls = clsSb.ToString();

                    bool isModalDialog = string.Equals(cls, "#32770", StringComparison.Ordinal);
                    bool isSecondaryPopup = mainWindowHandle != IntPtr.Zero && hWnd != mainWindowHandle;
                    if (!isModalDialog && !isSecondaryPopup)
                        return true;

                    string title = GetWindowTitleText(hWnd);
                    logs.Add(string.Format(
                        "Dismissing SOLIDWORKS child popup HWND 0x{0:X} (class='{1}', title='{2}') via Esc + WM_CLOSE.",
                        hWnd.ToInt64(), cls, title));
                    PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                    PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_ESCAPE, IntPtr.Zero);
                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        private static void RunEdgeAutomationFlow(
            string targetUrl,
            string targetApp,
            int waitTimeoutSeconds,
            ThreeDExperienceLaunchResult result)
        {
            List<string> logs = result.Logs;
            IntPtr targetHwnd = SelectExisting3DExperienceTabOrLaunchEdge(targetUrl, logs);

            if (targetHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Microsoft Edge launched, but no visible Edge window handle could be located in this desktop session.");
            }

            result.EdgeWindowTitle = GetWindowTitleText(targetHwnd);
            logs.Add(string.Format("Attached to Edge window HWND 0x{0:X} ('{1}').", targetHwnd.ToInt64(), result.EdgeWindowTitle));

            BringWindowToFront(targetHwnd);
            WakeUpChromiumAccessibility(targetHwnd, logs);

            bool loginSubmittedOnce = false;
            int loginAttempts = 0;
            bool clickedSolidWorksApp = false;
            DateTime lastAppClickUtc = DateTime.MinValue;
            DateTime lastCompassClickUtc = DateTime.MinValue;
            DateTime lastWakeupUtc = DateTime.UtcNow;

            Stopwatch flowTimer = Stopwatch.StartNew();
            while (flowTimer.Elapsed.TotalSeconds < 65.0)
            {
                // Check if SOLIDWORKS process already started
                Process[] swProcs = Process.GetProcessesByName("SLDWORKS");
                if (swProcs.Length > 0)
                {
                    result.LaunchedProcess = true;
                    logs.Add(string.Format("Detected SLDWORKS.exe startup (PID {0}) after {1:F1}s.",
                        swProcs[0].Id, flowTimer.Elapsed.TotalSeconds));
                    break;
                }

                // Periodically re-poke Chromium renderer accessibility across page navigations
                if ((DateTime.UtcNow - lastWakeupUtc).TotalSeconds > 2.5)
                {
                    WakeUpChromiumAccessibility(targetHwnd, null);
                    lastWakeupUtc = DateTime.UtcNow;
                }

                AutomationElement winEl = AutomationElement.FromHandle(targetHwnd);
                if (winEl == null)
                {
                    Thread.Sleep(500);
                    continue;
                }

                // Keep the 3DEXPERIENCE tab selected if user or browser shifted tab focus
                string winTitle = GetWindowTitleText(targetHwnd);
                result.EdgeWindowTitle = winTitle;
                if (winTitle.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) < 0 &&
                    winTitle.IndexOf("3DDashboard", StringComparison.OrdinalIgnoreCase) < 0 &&
                    winTitle.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) < 0 &&
                    winTitle.IndexOf("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    SelectTabInWindow(winEl, logs);
                }

                string currentUrl = TryReadEdgeAddressBar(winEl);
                if (!string.IsNullOrEmpty(currentUrl))
                    result.CurrentUrl = currentUrl;

                // 1. Check for Edge external protocol confirmation dialog ("Open" / "Allow")
                if (TryAcceptProtocolPrompt(winEl, logs))
                {
                    Thread.Sleep(1500);
                    continue;
                }

                AutomationElement docEl = FindRootWebArea(winEl);
                if (docEl == null)
                {
                    Thread.Sleep(600);
                    continue;
                }

                // Ensure web document focus triggers full Chromium accessibility tree
                try
                {
                    AutomationElement firstChild = TreeWalker.RawViewWalker.GetFirstChild(docEl);
                    if (firstChild == null)
                    {
                        try { docEl.SetFocus(); } catch { }
                        Thread.Sleep(500);
                    }
                }
                catch { }

                // 2. Check for CAPTCHA / 2FA blocks before acting (never bypass per policy)
                string securityGate = DetectSecurityGate(docEl);
                if (!string.IsNullOrEmpty(securityGate))
                {
                    throw new InvalidOperationException(
                        string.Format("3DEXPERIENCE login paused at a manual verification gate ({0}). Complete verification in the Microsoft Edge window, then run again.", securityGate));
                }

                // 3. Handle CAS Login page
                bool isLoginUrl = !string.IsNullOrEmpty(currentUrl) &&
                                  (currentUrl.IndexOf("cas/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   currentUrl.IndexOf("ds-iam", StringComparison.OrdinalIgnoreCase) >= 0);

                LoginFormControls loginControls = FindLoginFormControls(docEl);
                if (isLoginUrl || (loginControls.PasswordEdit != null && loginControls.LoginButton != null))
                {
                    if (!clickedSolidWorksApp)
                    {
                        if (loginAttempts >= 2)
                        {
                            string pageMsg = ExtractLikelyErrorBanner(docEl);
                            throw new InvalidOperationException(
                                string.Format(
                                    "Attempted saved-credential login {0} times on 3DEXPERIENCE CAS page without redirecting to dashboard.{1}",
                                    loginAttempts,
                                    string.IsNullOrWhiteSpace(pageMsg)
                                        ? " Verify Microsoft Edge has your username/password saved for eu1-ds-iam.3dexperience.3ds.com."
                                        : " Page message: " + pageMsg));
                        }

                        BringWindowToFront(targetHwnd);
                        bool submitted = PerformSavedCredentialLogin(loginControls, logs);
                        if (submitted)
                        {
                            loginSubmittedOnce = true;
                            loginAttempts++;
                            Thread.Sleep(3500);
                            WakeUpChromiumAccessibility(targetHwnd, logs);
                            continue;
                        }
                    }

                    Thread.Sleep(700);
                    continue;
                }

                // 4. On 3DEXPERIENCE Platform Dashboard -> find and launch SOLIDWORKS Design
                bool onDashboard = !string.IsNullOrEmpty(currentUrl) &&
                                   (currentUrl.IndexOf("ifwe.3dexperience.3ds.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    winTitle.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0);

                if (onDashboard || loginSubmittedOnce)
                {
                    AutomationElement appEl = FindFavoriteAppElement(docEl, targetApp)
                                           ?? FindElementByNameContains(docEl, targetApp)
                                           ?? FindElementByNameContains(docEl, "SOLIDWORKS Connected");

                    if (appEl != null)
                    {
                        if (!clickedSolidWorksApp || (DateTime.UtcNow - lastAppClickUtc).TotalSeconds > 15.0)
                        {
                            if (TryClickAppElement(appEl, targetApp, targetHwnd, logs, out string clickDesc))
                            {
                                clickedSolidWorksApp = true;
                                lastAppClickUtc = DateTime.UtcNow;
                                result.ClickedTarget = clickDesc;
                                Thread.Sleep(4500);
                                continue;
                            }
                        }
                    }
                    else if (!clickedSolidWorksApp)
                    {
                        AutomationElement favTab = FindElementByNameContains(docEl, "My Favorite Apps")
                                                ?? FindElementByNameContains(docEl, "Favorite Apps");
                        if (favTab != null && !IsElementOnScreen(favTab) && (DateTime.UtcNow - lastCompassClickUtc).TotalSeconds > 3.0)
                        {
                            try
                            {
                                if (favTab.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object sp))
                                {
                                    ((ScrollItemPattern)sp).ScrollIntoView();
                                    logs.Add("Scrolled 'Favorite Apps' section into view.");
                                    lastCompassClickUtc = DateTime.UtcNow;
                                    Thread.Sleep(1000);
                                    continue;
                                }
                            }
                            catch { }
                        }

                        if (!IsCompassDrawerOpen(docEl) && (DateTime.UtcNow - lastCompassClickUtc).TotalSeconds > 4.0)
                        {
                            if (TryOpenCompassMenu(docEl, targetHwnd, logs))
                            {
                                lastCompassClickUtc = DateTime.UtcNow;
                                Thread.Sleep(2500);
                                continue;
                            }
                        }
                    }
                }

                Thread.Sleep(800);
            }

            if (!result.LaunchedProcess)
            {
                if (clickedSolidWorksApp)
                {
                    result.StatusMessage = string.Format(
                        "Clicked '{0}' in Edge, waiting for SLDWORKS.exe process...",
                        result.ClickedTarget ?? targetApp);
                }
                else
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "Timed out after 65s trying to log into 3DEXPERIENCE and click '{0}' in Edge (last URL: {1}).",
                            targetApp,
                            result.CurrentUrl ?? "(unknown)"));
                }
            }

            // 5. Wait for SLDWORKS.exe and COM ROT registration
            Stopwatch comWait = Stopwatch.StartNew();
            while (comWait.Elapsed.TotalSeconds < waitTimeoutSeconds)
            {
                Process[] swProcs = Process.GetProcessesByName("SLDWORKS");
                if (swProcs.Length > 0)
                {
                    result.LaunchedProcess = true;
                    try
                    {
                        object active = Marshal.GetActiveObject("SldWorks.Application");
                        if (active is ISldWorks swApp)
                        {
                            try { swApp.Visible = true; } catch { }
                            WaitFor3DExperienceAddinReady(swProcs[0], logs);
                            result.Connected = true;
                            result.StatusMessage = string.Format(
                                "SOLIDWORKS launched via 3DEXPERIENCE and registered in COM ROT (version {0}).",
                                swApp.RevisionNumber());
                            logs.Add(result.StatusMessage);
                            return;
                        }
                    }
                    catch (COMException)
                    {
                        // Still booting add-ins
                    }
                }

                // Also watch for late Edge protocol prompt ("Open")
                AutomationElement winLater = AutomationElement.FromHandle(targetHwnd);
                if (winLater != null)
                    TryAcceptProtocolPrompt(winLater, logs);

                Thread.Sleep(1000);
            }

            if (result.LaunchedProcess)
            {
                result.StatusMessage = string.Format(
                    "SLDWORKS.exe launched via 3DEXPERIENCE, but COM ROT registration timed out after {0}s (still loading add-ins or startup splash/dialog).",
                    waitTimeoutSeconds);
                logs.Add(result.StatusMessage);
            }
            else
            {
                throw new InvalidOperationException(
                    "Clicked SOLIDWORKS Design in 3DEXPERIENCE, but SLDWORKS.exe did not start within the wait timeout.");
            }
        }

        private static void WaitFor3DExperienceAddinReady(Process swProc, List<string> logs)
        {
            if (swProc == null) return;
            DateTime procStart;
            try
            {
                procStart = swProc.StartTime;
                if ((DateTime.Now - procStart).TotalSeconds >= 45.0)
                    return;
            }
            catch
            {
                return;
            }

            string logPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                @"SolidWorks\SOLIDWORKS 2026\3dexperience-log.json");

            Stopwatch sw = Stopwatch.StartNew();
            bool loggedIn = false;
            while (sw.Elapsed.TotalSeconds < 25.0)
            {
                try
                {
                    if (File.Exists(logPath))
                    {
                        DateTime writeTime = File.GetLastWriteTime(logPath);
                        if (writeTime >= procStart.AddSeconds(-2.0))
                        {
                            string content;
                            using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (StreamReader sr = new StreamReader(fs))
                            {
                                content = sr.ReadToEnd();
                            }

                            if (!string.IsNullOrEmpty(content) &&
                                content.IndexOf("\"operation\":\"Login\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                content.IndexOf("\"state\":\"done\"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                loggedIn = true;
                                break;
                            }
                        }
                    }
                }
                catch { }

                Thread.Sleep(500);
            }

            if (loggedIn)
            {
                logs.Add(string.Format(
                    "3DEXPERIENCE cloud Login completed after {0:F1}s; waiting 3s for SWCEFComWrapper UI settle.",
                    sw.Elapsed.TotalSeconds));
                Thread.Sleep(3000);
            }
            else
            {
                logs.Add("3DEXPERIENCE login settlement wait timed out after 25s; continuing.");
            }
        }

        private static IntPtr SelectExisting3DExperienceTabOrLaunchEdge(string targetUrl, List<string> logs)
        {
            List<IntPtr> currentWindows = FindVisibleEdgeWindows();
            IntPtr existingTabHwnd = SelectExisting3DExperienceTab(currentWindows, logs);
            if (existingTabHwnd != IntPtr.Zero)
                return existingTabHwnd;

            string edgePath = ResolveEdgeExecutable();
            if (string.IsNullOrEmpty(edgePath))
            {
                throw new InvalidOperationException(
                    "Microsoft Edge (msedge.exe) was not found in standard installation folders.");
            }

            HashSet<IntPtr> preExisting = new HashSet<IntPtr>(currentWindows);
            string edgeArgs = string.Format("--force-renderer-accessibility \"{0}\"", targetUrl);
            Process.Start(new ProcessStartInfo(edgePath, edgeArgs) { UseShellExecute = true });
            logs.Add("Launched Microsoft Edge to 3DEXPERIENCE login URL.");

            Stopwatch winWait = Stopwatch.StartNew();
            while (winWait.Elapsed.TotalSeconds < 15.0)
            {
                List<IntPtr> afterWindows = FindVisibleEdgeWindows();
                IntPtr selected = SelectExisting3DExperienceTab(afterWindows, logs);
                if (selected != IntPtr.Zero)
                    return selected;

                IntPtr best = PickBestEdgeWindow(afterWindows, preExisting, logs);
                if (best != IntPtr.Zero)
                    return best;

                Thread.Sleep(400);
            }

            return IntPtr.Zero;
        }

        private static IntPtr SelectExisting3DExperienceTab(List<IntPtr> windows, List<string> logs)
        {
            foreach (IntPtr hwnd in windows)
            {
                try
                {
                    AutomationElement winEl = AutomationElement.FromHandle(hwnd);
                    if (winEl != null && SelectTabInWindow(winEl, logs))
                        return hwnd;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        private static bool SelectTabInWindow(AutomationElement winEl, List<string> logs)
        {
            try
            {
                Condition tabCond = new PropertyCondition(AutomationElement.ClassNameProperty, "EdgeTab");
                AutomationElementCollection tabs = winEl.FindAll(TreeScope.Descendants, tabCond);
                AutomationElement secondaryMatch = null;

                foreach (AutomationElement tab in tabs)
                {
                    string tName = tab.Current.Name ?? string.Empty;
                    bool isDirectDash =
                        tName.IndexOf("3DEXPERIENCE SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tName.IndexOf("3DDashboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tName.IndexOf("3DPassport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tName.IndexOf("eu1-ds-iam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tName.IndexOf("makers-ifwe", StringComparison.OrdinalIgnoreCase) >= 0;

                    bool isLoginOrGeneric =
                        tName.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        tName.IndexOf("Edu SPACE", StringComparison.OrdinalIgnoreCase) < 0;

                    if (isDirectDash)
                    {
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                        {
                            SelectionItemPattern pattern = (SelectionItemPattern)sip;
                            if (!pattern.Current.IsSelected)
                            {
                                pattern.Select();
                                logs.Add(string.Format("Selected existing Edge tab '{0}'.", tName));
                                Thread.Sleep(800);
                            }
                            else
                            {
                                logs.Add(string.Format("Reusing active Edge tab '{0}'.", tName));
                            }
                            return true;
                        }
                    }
                    else if (isLoginOrGeneric && secondaryMatch == null)
                    {
                        secondaryMatch = tab;
                    }
                }

                if (secondaryMatch != null &&
                    secondaryMatch.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object secSip))
                {
                    SelectionItemPattern pattern = (SelectionItemPattern)secSip;
                    string secName = secondaryMatch.Current.Name ?? string.Empty;
                    if (!pattern.Current.IsSelected)
                    {
                        pattern.Select();
                        logs.Add(string.Format("Selected Edge tab '{0}'.", secName));
                        Thread.Sleep(800);
                    }
                    return true;
                }
            }
            catch { }

            return false;
        }

        #region Window & Accessibility Discovery

        private static List<IntPtr> FindVisibleEdgeWindows()
        {
            HashSet<int> edgePids = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName("msedge"))
                edgePids.Add(p.Id);

            List<IntPtr> result = new List<IntPtr>();
            if (edgePids.Count == 0)
                return result;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (!edgePids.Contains((int)pid))
                    return true;

                StringBuilder cls = new StringBuilder(128);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "Chrome_WidgetWin_1")
                    result.Add(hWnd);

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static IntPtr PickBestEdgeWindow(
            List<IntPtr> currentWindows,
            HashSet<IntPtr> preExistingWindows,
            List<string> logs)
        {
            IntPtr newlyCreated = IntPtr.Zero;
            IntPtr titleMatch = IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;

            foreach (IntPtr hwnd in currentWindows)
            {
                string title = GetWindowTitleText(hwnd);
                bool isNew = preExistingWindows != null && !preExistingWindows.Contains(hwnd);
                bool looks3dx =
                    title.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("3ds.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Dassault", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isNew && looks3dx)
                    return hwnd;
                if (isNew && newlyCreated == IntPtr.Zero)
                    newlyCreated = hwnd;
                if (looks3dx && titleMatch == IntPtr.Zero)
                    titleMatch = hwnd;
                if (fallback == IntPtr.Zero && !string.IsNullOrWhiteSpace(title))
                    fallback = hwnd;
            }

            if (newlyCreated != IntPtr.Zero) return newlyCreated;
            if (titleMatch != IntPtr.Zero) return titleMatch;
            return fallback;
        }

        private static string GetWindowTitleText(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void BringWindowToFront(IntPtr hwnd)
        {
            IntPtr fgHwnd = GetForegroundWindow();
            uint dummyPid;
            uint fgTid = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out dummyPid) : 0;
            uint curTid = GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (fgTid != 0 && fgTid != curTid)
                    attached = AttachThreadInput(curTid, fgTid, true);

                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                else
                    ShowWindow(hwnd, SW_SHOW);

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            catch { }
            finally
            {
                if (attached)
                    AttachThreadInput(curTid, fgTid, false);
            }

            Thread.Sleep(300);
        }

        private static void WakeUpChromiumAccessibility(IntPtr topHwnd, List<string> logs)
        {
            try
            {
                Guid iid = IID_IAccessible;
                object acc;
                AccessibleObjectFromWindow(topHwnd, OBJID_CLIENT, ref iid, out acc);
                AutomationElement winEl = AutomationElement.FromHandle(topHwnd);

                int childCount = 0;
                EnumChildWindows(topHwnd, (childHwnd, lParam) =>
                {
                    StringBuilder cls = new StringBuilder(128);
                    GetClassName(childHwnd, cls, cls.Capacity);
                    if (cls.ToString() == "Chrome_RenderWidgetHostHWND")
                    {
                        childCount++;
                        Guid ciid = IID_IAccessible;
                        object childAcc;
                        AccessibleObjectFromWindow(childHwnd, OBJID_CLIENT, ref ciid, out childAcc);
                        try { AutomationElement.FromHandle(childHwnd); } catch { }
                    }
                    return true;
                }, IntPtr.Zero);

                // Also locate RootWebArea in UIA tree (Chromium hosts it under MultiContentsView)
                AutomationElement webArea = winEl != null ? FindRootWebArea(winEl) : null;
                if (webArea != null && logs != null)
                {
                    logs.Add(string.Format("Enabled Chromium UIAutomation ({0} child host(s), active RootWebArea: '{1}').",
                        childCount, webArea.Current.Name ?? ""));
                }
                else if (logs != null)
                {
                    logs.Add(string.Format("Enabled Chromium UIAutomation on {0} render host window(s).", childCount));
                }
            }
            catch (Exception ex)
            {
                if (logs != null)
                    logs.Add("Accessibility wake-up warning: " + ex.Message);
            }
        }

        private static string TryReadEdgeAddressBar(AutomationElement winEl)
        {
            try
            {
                AutomationElement omni = winEl.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"));

                if (omni != null && omni.TryGetCurrentPattern(ValuePattern.Pattern, out object pat))
                    return ((ValuePattern)pat).Current.Value ?? string.Empty;

                Condition editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                AutomationElementCollection edits = winEl.FindAll(TreeScope.Descendants, editCond);
                foreach (AutomationElement edit in edits)
                {
                    string name = edit.Current.Name ?? string.Empty;
                    if (name.IndexOf("Address", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object pat2))
                            return ((ValuePattern)pat2).Current.Value ?? string.Empty;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static AutomationElement FindRootWebArea(AutomationElement winEl)
        {
            if (winEl == null) return null;
            try
            {
                AutomationElementCollection roots = winEl.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea"));

                AutomationElement bestFallback = null;
                foreach (AutomationElement root in roots)
                {
                    try
                    {
                        AutomationElement.AutomationElementInformation cur = root.Current;
                        string name = cur.Name ?? string.Empty;
                        if (name.Equals("Communication iFrame", StringComparison.OrdinalIgnoreCase))
                            continue;

                        Rect r = cur.BoundingRectangle;
                        bool visible = !cur.IsOffscreen && !r.IsEmpty && !double.IsInfinity(r.Left) && r.Width > 50.0;
                        if (visible && (
                            name.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("3DDashboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("3DPassport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return root;
                        }

                        if (!string.IsNullOrWhiteSpace(name) && (visible || bestFallback == null))
                            bestFallback = root;
                    }
                    catch { }
                }

                if (bestFallback != null)
                    return bestFallback;

                // RawViewWalker fallback inside MultiContentsView
                AutomationElement mcv = winEl.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "MultiContentsView"));
                if (mcv != null)
                {
                    AutomationElement foundRaw = null;
                    WalkRawDom(mcv, (el, cur, depth) =>
                    {
                        if (cur.AutomationId == "RootWebArea" &&
                            !string.Equals(cur.Name, "Communication iFrame", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(cur.Name))
                        {
                            foundRaw = el;
                            return false;
                        }
                        return true;
                    }, maxDepth: 12, maxNodes: 150);
                    if (foundRaw != null)
                        return foundRaw;
                }
            }
            catch { }

            return null;
        }

        private static void WalkRawDom(
            AutomationElement root,
            Func<AutomationElement, AutomationElement.AutomationElementInformation, int, bool> visitor,
            int maxDepth = 28,
            int maxNodes = 800)
        {
            if (root == null || visitor == null) return;
            TreeWalker walker = TreeWalker.RawViewWalker;
            int visited = 0;
            bool stop = false;

            void Recurse(AutomationElement node, int depth)
            {
                if (stop || node == null || depth > maxDepth || visited >= maxNodes)
                    return;

                AutomationElement.AutomationElementInformation cur;
                try
                {
                    cur = node.Current;
                }
                catch
                {
                    return;
                }

                // Skip embedded communication iframe subtree
                if (depth > 0 && string.Equals(cur.Name, "Communication iFrame", StringComparison.OrdinalIgnoreCase))
                    return;

                visited++;
                if (!visitor(node, cur, depth))
                {
                    stop = true;
                    return;
                }

                AutomationElement child = null;
                try { child = walker.GetFirstChild(node); } catch { child = null; }
                while (child != null && !stop && visited < maxNodes)
                {
                    Recurse(child, depth + 1);
                    try { child = walker.GetNextSibling(child); } catch { child = null; }
                }
            }

            Recurse(root, 0);
        }

        #endregion

        #region CAS Login & 3DEXPERIENCE Dashboard Automation

        private class LoginFormControls
        {
            public AutomationElement UsernameEdit;
            public AutomationElement PasswordEdit;
            public AutomationElement LoginButton;
        }

        private static LoginFormControls FindLoginFormControls(AutomationElement docEl)
        {
            LoginFormControls res = new LoginFormControls();
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                if (!IsOnScreenAndVisible(cur))
                    return true;

                ControlType ct = cur.ControlType;
                string name = (cur.Name ?? string.Empty).Trim();
                string aid = cur.AutomationId ?? string.Empty;

                if (ct == ControlType.Edit)
                {
                    if (cur.IsPassword ||
                        name.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        aid.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (res.PasswordEdit == null)
                            res.PasswordEdit = el;
                    }
                    else
                    {
                        if (res.UsernameEdit == null &&
                            name.IndexOf("Search", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            res.UsernameEdit = el;
                        }
                    }
                }
                else if (ct == ControlType.Button && res.LoginButton == null)
                {
                    if (name.Equals("Log in", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Sign in", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Submit", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        res.LoginButton = el;
                    }
                }
                return true;
            }, maxDepth: 25, maxNodes: 500);

            return res;
        }

        private static bool PerformSavedCredentialLogin(LoginFormControls controls, List<string> logs)
        {
            if (controls.PasswordEdit == null && controls.UsernameEdit == null)
                return false;

            if (controls.UsernameEdit != null && TryGetClickableCenter(controls.UsernameEdit, out Point userPt))
            {
                ClickScreenPoint((int)userPt.X, (int)userPt.Y);
                logs.Add("Focused username field in Edge.");
                Thread.Sleep(500);

                string userVal = TryGetValueText(controls.UsernameEdit);
                string passVal = TryGetValueText(controls.PasswordEdit);
                if (string.IsNullOrWhiteSpace(userVal) || string.IsNullOrWhiteSpace(passVal))
                {
                    SendKey(VK_DOWN);
                    Thread.Sleep(250);
                    SendKey(VK_RETURN);
                    logs.Add("Selected saved credential from Edge autofill dropdown (Down + Enter).");
                    Thread.Sleep(600);
                }
            }

            if (controls.PasswordEdit != null && TryGetClickableCenter(controls.PasswordEdit, out Point passPt))
            {
                string passBefore = TryGetValueText(controls.PasswordEdit);
                if (string.IsNullOrWhiteSpace(passBefore))
                {
                    ClickScreenPoint((int)passPt.X, (int)passPt.Y);
                    Thread.Sleep(400);
                    string passAfterClick = TryGetValueText(controls.PasswordEdit);
                    if (string.IsNullOrWhiteSpace(passAfterClick))
                    {
                        SendKey(VK_DOWN);
                        Thread.Sleep(250);
                        SendKey(VK_RETURN);
                        logs.Add("Triggered password autofill selection (Down + Enter).");
                        Thread.Sleep(500);
                    }
                }
            }

            if (controls.LoginButton != null)
            {
                if (controls.LoginButton.TryGetCurrentPattern(InvokePattern.Pattern, out object pat))
                {
                    try
                    {
                        ((InvokePattern)pat).Invoke();
                        logs.Add("Invoked 'Log in' button.");
                        return true;
                    }
                    catch { }
                }

                if (TryGetClickableCenter(controls.LoginButton, out Point btnPt))
                {
                    ClickScreenPoint((int)btnPt.X, (int)btnPt.Y);
                    logs.Add("Clicked 'Log in' button.");
                    return true;
                }
            }

            SendKey(VK_RETURN);
            logs.Add("Pressed Enter to submit login form.");
            return true;
        }

        private static bool TryAcceptProtocolPrompt(AutomationElement winEl, List<string> logs)
        {
            try
            {
                AutomationElement docEl = FindRootWebArea(winEl);
                AutomationElementCollection buttons = winEl.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement btn in buttons)
                {
                    AutomationElement.AutomationElementInformation cur = btn.Current;
                    if (!IsOnScreenAndVisible(cur)) continue;

                    string name = (cur.Name ?? string.Empty).Trim();
                    bool isPromptButton =
                        name.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("Open 3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isPromptButton) continue;

                    if (docEl != null)
                    {
                        try
                        {
                            Rect docRect = docEl.Current.BoundingRectangle;
                            Rect btnRect = cur.BoundingRectangle;
                            if (docRect.Contains(btnRect))
                            {
                                AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(btn);
                                string parentName = parent != null ? (parent.Current.Name ?? "") : "";
                                bool looksLikeBrowserDialog =
                                    parentName.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    parentName.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("3DEXPERIENCE", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!looksLikeBrowserDialog)
                                    continue;
                            }
                        }
                        catch { }
                    }

                    if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out object pat))
                    {
                        ((InvokePattern)pat).Invoke();
                        logs.Add(string.Format("Accepted Edge protocol dialog button '{0}'.", name));
                        return true;
                    }
                    if (TryGetClickableCenter(btn, out Point pt))
                    {
                        ClickScreenPoint((int)pt.X, (int)pt.Y);
                        logs.Add(string.Format("Clicked Edge protocol dialog button '{0}'.", name));
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsCompassDrawerOpen(AutomationElement docEl)
        {
            bool open = false;
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                if (cur.AutomationId == "my-apps-ct" && IsElementOnScreen(el))
                {
                    open = true;
                    return false;
                }
                string cls = cur.ClassName ?? string.Empty;
                if (cls.IndexOf("compass-ct open", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    open = true;
                    return false;
                }
                return true;
            }, maxDepth: 20, maxNodes: 300);
            return open;
        }

        private static bool TryOpenCompassMenu(AutomationElement docEl, IntPtr targetHwnd, List<string> logs)
        {
            if (IsCompassDrawerOpen(docEl))
                return false;

            AutomationElement compassTarget = null;
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                if (!IsOnScreenAndVisible(cur)) return true;
                string aid = cur.AutomationId ?? string.Empty;
                string cls = cur.ClassName ?? string.Empty;
                if (aid == "compass_ctn" || cls.IndexOf("compass-small", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    compassTarget = el;
                    return false;
                }
                return true;
            }, maxDepth: 16, maxNodes: 200);

            if (compassTarget != null && TryGetClickableCenter(compassTarget, out Point pt))
            {
                BringWindowToFront(targetHwnd);
                ClickScreenPoint((int)pt.X, (int)pt.Y);
                logs.Add(string.Format("Clicked 3DEXPERIENCE Compass at ({0:F0}, {1:F0}).", pt.X, pt.Y));
                return true;
            }

            try
            {
                Rect r = docEl.Current.BoundingRectangle;
                if (!r.IsEmpty && r.Width > 200 && r.Height > 200)
                {
                    int cx = (int)Math.Round(r.Left + 44.0);
                    int cy = (int)Math.Round(r.Top + 26.0);
                    BringWindowToFront(targetHwnd);
                    ClickScreenPoint(cx, cy);
                    logs.Add(string.Format("Clicked top-left 3DEXPERIENCE Compass fallback ({0}, {1}).", cx, cy));
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static AutomationElement FindFavoriteAppElement(AutomationElement docEl, string appName)
        {
            AutomationElement favSection = null;
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                string cls = cur.ClassName ?? string.Empty;
                if (cls.IndexOf("my-favorite-apps-section", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    favSection = el;
                    return false;
                }
                return true;
            }, maxDepth: 25, maxNodes: 600);

            if (favSection != null)
            {
                AutomationElement inFav = FindElementByNameContains(favSection, appName);
                if (inFav != null)
                    return inFav;
            }
            return null;
        }

        private static bool TryClickAppElement(
            AutomationElement appEl,
            string targetApp,
            IntPtr targetHwnd,
            List<string> logs,
            out string description)
        {
            description = targetApp;
            try
            {
                if (appEl.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object scrollPat))
                {
                    try { ((ScrollItemPattern)scrollPat).ScrollIntoView(); Thread.Sleep(250); } catch { }
                }

                Rect labelRect = appEl.Current.BoundingRectangle;
                Rect tileRect = labelRect;

                // Walk up RawViewWalker to enclosing item-view-v3 tile (avoiding the right-edge '' menu caret)
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement parent = walker.GetParent(appEl);
                int hops = 0;
                while (parent != null && hops < 6)
                {
                    string pCls = parent.Current.ClassName ?? string.Empty;
                    if (pCls.IndexOf("item-view-v3", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Rect pRect = parent.Current.BoundingRectangle;
                        if (!pRect.IsEmpty && !double.IsInfinity(pRect.Left) && pRect.Width > 30 && pRect.Height > 30)
                            tileRect = pRect;
                        break;
                    }
                    parent = walker.GetParent(parent);
                    hops++;
                }

                // Target the text label or icon/title portion on the left half of the tile (never the right-side caret)
                double clickX;
                double clickY;
                if (!labelRect.IsEmpty && !double.IsInfinity(labelRect.Left) && labelRect.Width > 5 && labelRect.Height > 5)
                {
                    clickX = labelRect.Left + labelRect.Width * 0.5;
                    clickY = labelRect.Top + labelRect.Height * 0.5;
                }
                else if (!tileRect.IsEmpty && !double.IsInfinity(tileRect.Left) && tileRect.Width > 5 && tileRect.Height > 5)
                {
                    clickX = tileRect.Left + Math.Min(90.0, tileRect.Width * 0.45);
                    clickY = tileRect.Top + tileRect.Height * 0.5;
                }
                else
                {
                    return false;
                }

                string elName = appEl.Current.Name ?? targetApp;
                description = elName;

                BringWindowToFront(targetHwnd);
                ClickScreenPoint((int)Math.Round(clickX), (int)Math.Round(clickY));
                logs.Add(string.Format("Clicked '{0}' at ({1:F0}, {2:F0}) [tile={3:F0},{4:F0},{5:F0}x{6:F0}].",
                    elName, clickX, clickY, tileRect.Left, tileRect.Top, tileRect.Width, tileRect.Height));
                return true;
            }
            catch (Exception ex)
            {
                logs.Add("Click attempt warning on '" + targetApp + "': " + ex.Message);
                return false;
            }
        }

        private static string DetectSecurityGate(AutomationElement docEl)
        {
            string[] tokens = new[]
            {
                "captcha", "recaptcha", "turnstile", "verify you are human",
                "verification code", "two-factor", "2-step verification", "authenticator app"
            };

            string found = null;
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                if (!IsOnScreenAndVisible(cur)) return true;
                string combined = ((cur.Name ?? "") + " " + (cur.AutomationId ?? "")).ToLowerInvariant();
                foreach (string t in tokens)
                {
                    if (combined.Contains(t))
                    {
                        found = t;
                        return false;
                    }
                }
                return true;
            }, maxDepth: 20, maxNodes: 300);

            return found;
        }

        private static string ExtractLikelyErrorBanner(AutomationElement docEl)
        {
            string found = string.Empty;
            WalkRawDom(docEl, (el, cur, depth) =>
            {
                if (cur.ControlType != ControlType.Text || !IsOnScreenAndVisible(cur))
                    return true;
                string s = (cur.Name ?? "").Trim();
                if (s.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("incorrect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = s;
                    return false;
                }
                return true;
            }, maxDepth: 20, maxNodes: 300);
            return found;
        }

        #endregion

        #region UIAutomation Element Helpers & Input Simulation

        private static AutomationElement FindElementByNameContains(AutomationElement root, string substring)
        {
            if (root == null || string.IsNullOrWhiteSpace(substring))
                return null;

            AutomationElement exactMatch = null;
            AutomationElement interactiveMatch = null;
            AutomationElement fallbackMatch = null;

            WalkRawDom(root, (el, cur, depth) =>
            {
                if (!IsOnScreenAndVisible(cur)) return true;
                string name = (cur.Name ?? string.Empty).Trim();
                if (name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                if (name.Equals(substring, StringComparison.OrdinalIgnoreCase))
                {
                    exactMatch = el;
                    return false;
                }

                ControlType ct = cur.ControlType;
                if (interactiveMatch == null && (
                    ct == ControlType.Button ||
                    ct == ControlType.Hyperlink ||
                    ct == ControlType.MenuItem ||
                    ct == ControlType.ListItem ||
                    ct == ControlType.TabItem))
                {
                    interactiveMatch = el;
                }

                if (fallbackMatch == null)
                    fallbackMatch = el;

                return true;
            }, maxDepth: 28, maxNodes: 1000);

            return exactMatch ?? interactiveMatch ?? fallbackMatch;
        }

        private static bool IsElementOnScreen(AutomationElement el)
        {
            if (el == null) return false;
            try
            {
                return IsOnScreenAndVisible(el.Current);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOnScreenAndVisible(AutomationElement.AutomationElementInformation cur)
        {
            if (cur.IsOffscreen)
                return false;
            Rect r = cur.BoundingRectangle;
            return !r.IsEmpty &&
                   !double.IsInfinity(r.Left) &&
                   !double.IsInfinity(r.Top) &&
                   r.Width > 3.0 &&
                   r.Height > 3.0;
        }

        private static bool TryGetClickableCenter(AutomationElement el, out Point center)
        {
            center = new Point();
            if (el == null) return false;
            try
            {
                Rect r = el.Current.BoundingRectangle;
                if (r.IsEmpty || double.IsInfinity(r.Left) || r.Width < 3.0 || r.Height < 3.0)
                    return false;
                center = new Point(r.Left + r.Width * 0.5, r.Top + r.Height * 0.5);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string TryGetValueText(AutomationElement el)
        {
            if (el == null) return string.Empty;
            try
            {
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object pat))
                    return ((ValuePattern)pat).Current.Value ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }

        private static void ClickScreenPoint(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(70);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static void SendKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        #endregion
    }
}
