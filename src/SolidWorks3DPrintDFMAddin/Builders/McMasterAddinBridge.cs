using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace SolidWorks3DPrintDFMAddin.Builders
{
    /// <summary>
    /// In-process reflection bridge into the installed McMaster-Carr SOLIDWORKS Add-in
    /// (McMaster.SolidWorks.CustomerAddIn) running inside SLDWORKS.exe.
    /// Avoids a compile-time dependency on the external add-in DLL.
    /// </summary>
    public static class McMasterAddinBridge
    {
        public const string McMasterAddinGuid = "{4c042761-ffae-42fc-9835-92e3b4b8875d}";
        private const int SettingUserCadDownloadPath = 0;

        private const BindingFlags AnyFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static string ExecuteJson(ISldWorks swApp, string requestJson)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                Dictionary<string, object> req = ParseDict(serializer, requestJson);
                string action = GetString(req, "action", "status").ToLowerInvariant();
                string partNumber = (GetString(req, "mcmaster_pn", "") ?? "").Trim().ToUpperInvariant();
                string query = GetString(req, "query", partNumber) ?? "";
                string libraryDir = GetString(req, "library_dir", "");
                int timeoutMs = Math.Max(3000, Math.Min(60000, GetInt(req, "timeout_ms", 20000)));

                if (swApp == null)
                {
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "error", "ISldWorks reference is null inside DFM add-in bridge." }
                    });
                }

                string downloadsDir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                    "Downloads");

                // Fast path: check disk first if downloading by explicit part number
                if (action == "download" && !string.IsNullOrEmpty(partNumber))
                {
                    string existing = FindPartOnDisk(partNumber, libraryDir, downloadsDir, true);
                    if (!string.IsNullOrEmpty(existing))
                    {
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", true },
                            { "source", "local_cache" },
                            { "mcmaster_pn", partNumber },
                            { "part_path", existing }
                        });
                    }
                }

                object addinObj = null;
                try { addinObj = swApp.GetAddInObject(McMasterAddinGuid); } catch { }

                if (addinObj == null)
                {
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "addin_loaded", false },
                        { "error", "McMaster-Carr SOLIDWORKS Add-in is not loaded in this SOLIDWORKS session." }
                    });
                }

                bool isComProxy = Marshal.IsComObject(addinObj);
                object vm = ExtractViewModel(addinObj);
                if (vm == null)
                {
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "addin_loaded", true },
                        { "is_com_proxy", isComProxy },
                        { "error", "McMaster Add-in object is loaded, but its internal McMasterTaskpaneViewModel could not be reflected across boundary." }
                    });
                }

                bool isLoggedIn = InvokeBool(vm, "IsLoggedIn", false);
                object settingsMgr = GetFieldOrProp(vm, "mAddInSettingsManager");
                object dispatcher = GetFieldOrProp(vm, "mDispatcher");

                if (action == "status")
                {
                    string currentDownloadDir = TryGetSetting(settingsMgr, SettingUserCadDownloadPath);
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "addin_loaded", true },
                        { "is_com_proxy", isComProxy },
                        { "is_logged_in", isLoggedIn },
                        { "download_path", currentDownloadDir ?? downloadsDir }
                    });
                }

                if (!isLoggedIn)
                {
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "addin_loaded", true },
                        { "is_logged_in", false },
                        { "error", "Please sign in to the McMaster-Carr Taskpane Add-in inside SOLIDWORKS first." }
                    });
                }

                if (!string.IsNullOrEmpty(libraryDir))
                {
                    try
                    {
                        Directory.CreateDirectory(libraryDir);
                        if (settingsMgr != null)
                        {
                            InvokeMethod(settingsMgr, "SetSettingValue", SettingUserCadDownloadPath, libraryDir);
                        }
                    }
                    catch { }
                }

                if (action == "search")
                {
                    string searchTerm = !string.IsNullOrEmpty(query) ? query : partNumber;
                    if (string.IsNullOrEmpty(searchTerm))
                    {
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", "Provide query or mcmaster_pn for search." }
                        });
                    }

                    LaunchWebViewAndWait(vm, dispatcher, searchTerm, Math.Min(timeoutMs, 12000), out object webView, out string pageErr);
                    if (webView == null)
                    {
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", pageErr ?? "McMaster Add-in webview did not initialize in time." }
                        });
                    }

                    string script = @"(function(){
                        var items = [];
                        var seen = {};
                        var els = document.querySelectorAll('a, [data-partnumber], .PartNbr, .product-tile');
                        for (var i = 0; i < els.length && items.length < 15; i++) {
                            var txt = (els[i].innerText || els[i].getAttribute('data-partnumber') || '').trim();
                            var m = txt.match(/\b(\d{4,5}[aknt]\d{1,3})\b/i);
                            if (m) {
                                var pn = m[1].toUpperCase();
                                if (!seen[pn]) {
                                    seen[pn] = true;
                                    var row = els[i].closest('tr, li, div');
                                    var desc = row ? (row.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 140) : '';
                                    items.push({ part_number: pn, snippet: desc });
                                }
                            }
                        }
                        return JSON.stringify({ url: window.location.href, title: document.title, results: items });
                    })();";

                    string evalJson = null;
                    InvokeOnDispatcher(dispatcher, new Action(() => { evalJson = EvalWebViewScript(webView, script); }));
                    CleanupPoppedOut(vm, dispatcher);

                    Dictionary<string, object> parsedEval = ParseDict(serializer, evalJson);
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "source", "mcmaster_addin_webview" },
                        { "query", searchTerm },
                        { "webview_result", parsedEval }
                    });
                }

                if (action == "eval")
                {
                    string searchTerm = !string.IsNullOrEmpty(query) ? query : partNumber;
                    string customJs = GetString(req, "js", "document.title + ' | ' + window.location.href");
                    LaunchWebViewAndWait(vm, dispatcher, searchTerm, Math.Min(timeoutMs, 15000), out object webView, out string pageErr);
                    if (webView == null)
                    {
                        CleanupPoppedOut(vm, dispatcher);
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", pageErr ?? "McMaster Add-in webview did not initialize in time." }
                        });
                    }
                    // Brief wait for SPA hydration
                    DateTime hydrateUntil = DateTime.UtcNow.AddMilliseconds(1500);
                    while (DateTime.UtcNow < hydrateUntil)
                    {
                        PumpUiMessages(dispatcher);
                        Thread.Sleep(100);
                    }
                    string evalOut = null;
                    InvokeOnDispatcher(dispatcher, new Action(() => { evalOut = EvalWebViewScript(webView, customJs); }));
                    CleanupPoppedOut(vm, dispatcher);
                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "result", evalOut }
                    });
                }

                if (action == "download")
                {
                    if (string.IsNullOrEmpty(partNumber))
                    {
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", "mcmaster_pn is required for action=download." }
                        });
                    }

                    DateTime startedUtc = DateTime.UtcNow.AddSeconds(-2);
                    LaunchWebViewAndWait(vm, dispatcher, partNumber, Math.Min(timeoutMs, 15000), out object webView, out string pageErr);
                    if (webView == null)
                    {
                        CleanupPoppedOut(vm, dispatcher);
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", false },
                            { "error", pageErr ?? "Failed to open McMaster Add-in browser window." }
                        });
                    }

                    // Select 3-D SolidWorks CAD format if present and click the Add To Assembly / Save button
                    string customTriggerJs = GetString(req, "trigger_js", "");
                    string triggerCadJs = !string.IsNullOrEmpty(customTriggerJs) ? customTriggerJs : @"(function(){
                        var dd = document.querySelector('button[class*=""buttonDropdown""]');
                        var dl = document.querySelector('button[class*=""downloadButtonAddIn""]') || document.querySelector('a[class*=""downloadAnchor""]');
                        if (dl) {
                            if (dd && /select a cad/i.test(dd.innerText || '')) {
                                dd.click();
                                var items = document.querySelectorAll('li, button, a, div[role=""option""], [role=""menuitem""]');
                                for (var i = 0; i < items.length; i++) {
                                    var t = (items[i].innerText || '').trim();
                                    if (/^3-d\s+solidworks$/i.test(t) || /3-d.*solidworks/i.test(t)) {
                                        items[i].click();
                                        break;
                                    }
                                }
                                return 'selected_cad';
                            }
                            dl.click();
                            return 'clicked:' + (dl.innerText || 'Add To Assembly').trim();
                        }
                        var sels = document.querySelectorAll('select');
                        for (var s = 0; s < sels.length; s++) {
                            var sel = sels[s];
                            if (!sel.options) continue;
                            for (var k = 0; k < sel.options.length; k++) {
                                if (/3-?d.*solidworks|solidworks/i.test(sel.options[k].text)) {
                                    sel.selectedIndex = k;
                                    sel.dispatchEvent(new Event('change', { bubbles: true }));
                                    break;
                                }
                            }
                        }
                        return 'waiting_dom:' + window.location.href;
                    })();";

                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    bool triggered = false;
                    string triggerStatus = "";
                    string downloadedFile = null;

                    while (DateTime.UtcNow < deadline)
                    {
                        PumpUiMessages(dispatcher);

                        if (!triggered)
                        {
                            bool canEval = false;
                            bool isLoading = true;
                            InvokeOnDispatcher(dispatcher, new Action(() =>
                            {
                                canEval = GetBoolProp(webView, "CanEvalScript", true);
                                isLoading = GetBoolProp(webView, "IsLoading", false);
                                if (canEval && !isLoading)
                                {
                                    triggerStatus = EvalWebViewScript(webView, triggerCadJs) ?? "";
                                }
                            }));
                            if (canEval && !isLoading && triggerStatus.StartsWith("clicked", StringComparison.Ordinal))
                            {
                                triggered = true;
                            }
                        }

                        downloadedFile = FindNewestPartOnDisk(partNumber, startedUtc, libraryDir, downloadsDir, true);
                        if (!string.IsNullOrEmpty(downloadedFile))
                            break;

                        Thread.Sleep(150);
                    }

                    CleanupPoppedOut(vm, dispatcher);

                    if (!string.IsNullOrEmpty(downloadedFile))
                    {
                        return serializer.Serialize(new Dictionary<string, object>
                        {
                            { "ok", true },
                            { "source", "mcmaster_addin_download" },
                            { "mcmaster_pn", partNumber },
                            { "part_path", downloadedFile },
                            { "trigger_status", triggerStatus }
                        });
                    }

                    return serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "mcmaster_pn", partNumber },
                        { "trigger_status", triggerStatus },
                        { "error", "Timed out waiting for McMaster-Carr Add-in to save " + partNumber + ".SLDPRT into '" + (!string.IsNullOrEmpty(libraryDir) ? libraryDir : downloadsDir) + "'." }
                    });
                }

                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", "Unsupported action: " + action }
                });
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root is TargetInvocationException tie && tie.InnerException != null)
                    root = tie.InnerException;
                string st = root.StackTrace ?? "";
                if (st.Length > 800) st = st.Substring(0, 800);
                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", root.Message },
                    { "exception_type", root.GetType().FullName },
                    { "stack", st }
                });
            }
        }

        private static object ExtractViewModel(object mcmAddin)
        {
            object tp = GetFieldOrProp(mcmAddin, "mTaskpane");
            if (tp != null)
            {
                object vm = GetFieldOrProp(tp, "McMasterTaskpaneViewModel");
                if (vm != null) return vm;
            }

            // Fallback: inspect other McMaster Add-in fields in case internal layout changed
            try
            {
                foreach (FieldInfo fi in mcmAddin.GetType().GetFields(AnyFlags))
                {
                    object val = fi.GetValue(mcmAddin);
                    if (val == null) continue;
                    if (val.GetType().Name.IndexOf("TaskpaneViewModel", StringComparison.OrdinalIgnoreCase) >= 0)
                        return val;
                    object nested = GetFieldOrProp(val, "McMasterTaskpaneViewModel");
                    if (nested != null) return nested;
                }
            }
            catch { }

            return null;
        }

        private static void LaunchWebViewAndWait(object vm, object dispatcher, string searchTerm, int timeoutMs, out object webView, out string err)
        {
            webView = null;
            err = null;

            // Ensure BrowserFactory task has finished initializing if present
            object bfTask = GetFieldOrProp(vm, "mBrowserFactoryTask");
            if (bfTask != null)
            {
                DateTime bfDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < bfDeadline && !GetBoolProp(bfTask, "IsCompleted", true))
                {
                    PumpUiMessages(dispatcher);
                    Thread.Sleep(50);
                }
            }

            InvokeOnDispatcher(dispatcher, new Action(() =>
            {
                InvokeMethod(vm, "LaunchFindProductWebView", searchTerm);
            }));

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                PumpUiMessages(dispatcher);
                object candidate = null;
                bool isLoading = true;
                string url = "";
                InvokeOnDispatcher(dispatcher, new Action(() =>
                {
                    object win = GetFieldOrProp(vm, "mPoppedOutBrowserWindow");
                    if (win != null)
                    {
                        object dc = GetFieldOrProp(win, "DataContext");
                        object wc = dc != null ? GetFieldOrProp(dc, "WebViewControl") : null;
                        candidate = wc != null ? GetFieldOrProp(wc, "WebView") : null;
                        if (candidate != null)
                        {
                            isLoading = GetBoolProp(candidate, "IsLoading", true);
                            url = GetStringProp(candidate, "Url", "");
                        }
                    }
                }));
                if (candidate != null && !isLoading && !string.IsNullOrEmpty(url))
                {
                    webView = candidate;
                    return;
                }
                Thread.Sleep(120);
            }

            err = "Timed out waiting for McMaster Add-in web browser window to finish loading '" + searchTerm + "'.";
        }

        private static void CleanupPoppedOut(object vm, object dispatcher)
        {
            try
            {
                InvokeOnDispatcher(dispatcher, new Action(() =>
                {
                    try { InvokeMethod(vm, "CancelFileSave"); } catch { }
                    try { InvokeMethod(vm, "ClosePoppedOutWebView"); } catch { }
                }));
                PumpUiMessages(dispatcher);
            }
            catch { }
        }

        private static string EvalWebViewScript(object webView, string js)
        {
            try
            {
                MethodInfo m = webView.GetType().GetMethod("EvalScript", new[] { typeof(string) });
                if (m != null)
                {
                    object res = m.Invoke(webView, new object[] { js });
                    return res != null ? Convert.ToString(res) : null;
                }
            }
            catch { }
            return null;
        }

        private static string FindPartOnDisk(string partNumber, string libraryDir, string downloadsDir, bool moveFromDownloads)
        {
            if (!string.IsNullOrEmpty(libraryDir) && Directory.Exists(libraryDir))
            {
                string[] hits = Directory.GetFiles(libraryDir, partNumber + "*.SLDPRT", SearchOption.TopDirectoryOnly);
                if (hits.Length > 0) return hits[0];
            }

            if (!string.IsNullOrEmpty(downloadsDir) && Directory.Exists(downloadsDir))
            {
                string[] hits = Directory.GetFiles(downloadsDir, partNumber + "*.SLDPRT", SearchOption.TopDirectoryOnly);
                if (hits.Length > 0)
                {
                    string src = hits[0];
                    if (moveFromDownloads && !string.IsNullOrEmpty(libraryDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(libraryDir);
                            string dst = Path.Combine(libraryDir, Path.GetFileName(src));
                            if (!File.Exists(dst)) File.Copy(src, dst, false);
                            return dst;
                        }
                        catch { }
                    }
                    return src;
                }
            }
            return null;
        }

        private static bool IsValidCompleteSldprt(string filePath)
        {
            try
            {
                byte[] hdr = new byte[8];
                long len1;
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    len1 = fs.Length;
                    if (len1 < 8192) return false;
                    if (fs.Read(hdr, 0, 8) < 8) return false;
                }
                bool isOle = (hdr[0] == 0xD0 && hdr[1] == 0xCF && hdr[2] == 0x11 && hdr[3] == 0xE0);
                bool isSw2015 = (hdr[4] == 0x00 && hdr[5] == 0x00 && hdr[6] == 0x00 && hdr[7] == 0x04);
                if (!isOle && !isSw2015)
                    return false;
                Thread.Sleep(400);
                long len2 = new FileInfo(filePath).Length;
                return len2 == len1;
            }
            catch
            {
                return false;
            }
        }

        private static string FindNewestPartOnDisk(string partNumber, DateTime minUtc, string libraryDir, string downloadsDir, bool moveFromDownloads)
        {
            string[] dirs = new[] { libraryDir, downloadsDir };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] hits = Directory.GetFiles(dir, partNumber + "*.SLDPRT", SearchOption.TopDirectoryOnly);
                foreach (string h in hits)
                {
                    try
                    {
                        DateTime lw = File.GetLastWriteTimeUtc(h);
                        if (lw >= minUtc && IsValidCompleteSldprt(h))
                        {
                            if (moveFromDownloads && !string.IsNullOrEmpty(libraryDir) &&
                                !string.Equals(Path.GetDirectoryName(h), Path.GetFullPath(libraryDir), StringComparison.OrdinalIgnoreCase))
                            {
                                Directory.CreateDirectory(libraryDir);
                                string dst = Path.Combine(libraryDir, Path.GetFileName(h));
                                File.Copy(h, dst, true);
                                return dst;
                            }
                            return h;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static void PumpUiMessages(object dispatcher)
        {
            try { Application.DoEvents(); } catch { }
            if (dispatcher == null) return;
            try
            {
                // Flush WPF Dispatcher queue at Background priority
                MethodInfo[] methods = dispatcher.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (MethodInfo m in methods)
                {
                    if (m.Name != "Invoke") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(Delegate) && ps[1].ParameterType.Name == "DispatcherPriority")
                    {
                        object bg = Enum.Parse(ps[1].ParameterType, "Background");
                        m.Invoke(dispatcher, new object[] { new Action(() => { }), bg });
                        break;
                    }
                }
            }
            catch { }
        }

        private static void InvokeOnDispatcher(object dispatcher, Action action)
        {
            if (dispatcher == null)
            {
                action();
                return;
            }
            if (InvokeBool(dispatcher, "CheckAccess", false))
            {
                action();
                return;
            }
            Type dt = dispatcher.GetType();
            MethodInfo mAction = dt.GetMethod("Invoke", new[] { typeof(Action) });
            if (mAction != null)
            {
                mAction.Invoke(dispatcher, new object[] { action });
                return;
            }
            MethodInfo mDel = dt.GetMethod("Invoke", new[] { typeof(Delegate), typeof(object[]) });
            if (mDel != null)
            {
                mDel.Invoke(dispatcher, new object[] { action, new object[0] });
                return;
            }
            action();
        }

        private static object GetFieldOrProp(object target, string name)
        {
            if (target == null) return null;
            Type t = target.GetType();
            PropertyInfo p = t.GetProperty(name, AnyFlags);
            if (p != null) return p.GetValue(target, null);
            FieldInfo f = t.GetField(name, AnyFlags);
            if (f != null) return f.GetValue(target);
            return null;
        }

        private static object InvokeMethod(object target, string name, params object[] args)
        {
            if (target == null) return null;
            MethodInfo m = target.GetType().GetMethod(name, AnyFlags);
            if (m == null) return null;
            return m.Invoke(target, args);
        }

        private static bool InvokeBool(object target, string name, bool fallback)
        {
            try
            {
                object v = InvokeMethod(target, name);
                return v != null ? Convert.ToBoolean(v) : fallback;
            }
            catch { return fallback; }
        }

        private static bool GetBoolProp(object target, string name, bool fallback)
        {
            try
            {
                object v = GetFieldOrProp(target, name);
                return v != null ? Convert.ToBoolean(v) : fallback;
            }
            catch { return fallback; }
        }

        private static string GetStringProp(object target, string name, string fallback)
        {
            try
            {
                object v = GetFieldOrProp(target, name);
                return v != null ? Convert.ToString(v) : fallback;
            }
            catch { return fallback; }
        }

        private static string TryGetSetting(object settingsMgr, int settingId)
        {
            if (settingsMgr == null) return null;
            try
            {
                MethodInfo m = settingsMgr.GetType().GetMethod("TryGetSettingValue", AnyFlags);
                if (m == null) return null;
                object[] p = new object[] { settingId, null };
                object ok = m.Invoke(settingsMgr, p);
                if (ok != null && Convert.ToBoolean(ok))
                    return p[1] as string;
            }
            catch { }
            return null;
        }

        private static Dictionary<string, object> ParseDict(JavaScriptSerializer ser, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (ser.DeserializeObject(json) is Dictionary<string, object> d)
                    return new Dictionary<string, object>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static string GetString(IDictionary<string, object> d, string k, string fallback)
        {
            if (d != null && d.TryGetValue(k, out object v) && v != null)
                return Convert.ToString(v);
            return fallback;
        }

        private static int GetInt(IDictionary<string, object> d, string k, int fallback)
        {
            if (d != null && d.TryGetValue(k, out object v) && v != null)
            {
                if (int.TryParse(Convert.ToString(v), out int parsed))
                    return parsed;
            }
            return fallback;
        }
    }
}
