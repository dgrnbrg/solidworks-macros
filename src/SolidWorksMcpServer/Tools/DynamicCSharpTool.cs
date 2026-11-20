using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class DynamicCSharpTool
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_execute_csharp",
                Description = "Compile (Roslyn C# latest) and run arbitrary early-bound C# code against live SOLIDWORKS COM interfaces (swApp, swModel, swPart, swAssy, swDraw, log) plus built-in SwHelper utilities inside a modal-safe Undo transaction.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "code_body", JsonHelper.Prop("string", "C# statements inside a method returning object (supports C# 9+ syntax: out var/bool, tuples, local functions, target-typed new()). Available locals: ISldWorks swApp, IModelDoc2 swModel, IPartDoc swPart, IAssemblyDoc swAssy, IDrawingDoc swDraw, Action<string> log (prefer log(...) over local StringBuilder so traces survive exceptions); and SwHelper.* helpers (Activate, SuppressUiUpdates, BatchDeleteComponents, BatchDeleteMates, BatchDeleteMatesForComponents, GetVisibleSketchNames, HideSketches, MinimizeCefUiNoise, MakeOffsetPlane, FindPlanarFace, FindCylindricalFace, MoveComponentsToFolder, MateByPrincipalPlanes).") },
                    { "undo_name", JsonHelper.Prop("string", "Undo transaction label.", "MCP Dynamic C#") },
                    { "rollback_on_error", JsonHelper.Prop("boolean", "Automatically undo model edits if an exception occurs.", true) },
                    { "use_undo_transaction", JsonHelper.Prop("boolean", "Wrap execution in StartRecordingUndoObject on target_doc/ActiveDoc (auto-disabled when creating, opening, or switching documents).", true) },
                    { "additional_usings", JsonHelper.ArrayProp("string", "Optional extra C# namespaces.") },
                    { "compile_only", JsonHelper.Prop("boolean", "Compile-check the C# snippet without invoking SOLIDWORKS (useful for offline tests).", false) },
                    { "allow_disconnected", JsonHelper.Prop("boolean", "Run snippet even when SOLIDWORKS is not running (swApp/swModel will be null; useful for pure computation or tests).", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "code_body"),
                Handler = args => HandleExecuteCSharp(swMgr, args)
            });
        }

        private static string StripCSharpComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            string noBlock = Regex.Replace(code, @"/\*[\s\S]*?\*/", " ");
            return Regex.Replace(noBlock, @"//[^\r\n]*", " ");
        }

        private static string FindRoslynCscPath()
        {
            string[] candidates =
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\Roslyn\csc.exe",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static string BuildSource(string codeBody, List<string> extraUsings, bool forceTrailingReturn)
        {
            StringBuilder src = new StringBuilder();
            src.AppendLine("using System;");
            src.AppendLine("using System.Collections.Generic;");
            src.AppendLine("using System.IO;");
            src.AppendLine("using System.Linq;");
            src.AppendLine("using SolidWorks.Interop.sldworks;");
            src.AppendLine("using SolidWorks.Interop.swconst;");
            src.AppendLine("using SolidWorksMcpServer.Core;");
            src.AppendLine("using SolidWorksMcpServer.Tools;");
            foreach (string u in extraUsings)
            {
                if (!string.IsNullOrWhiteSpace(u))
                    src.AppendLine("using " + u.Trim().TrimEnd(';') + ";");
            }
            src.AppendLine("public static class DynamicSwScript");
            src.AppendLine("{");
            src.AppendLine("    public static object Run(ISldWorks swApp, IModelDoc2 swModel, IPartDoc swPart, IAssemblyDoc swAssy, IDrawingDoc swDraw, Action<string> log)");
            src.AppendLine("    {");
            src.AppendLine(codeBody);
            if (forceTrailingReturn || !Regex.IsMatch(codeBody ?? string.Empty, @"\breturn\b"))
                src.AppendLine("        return null;");
            src.AppendLine("    }");
            src.AppendLine("}");
            return src.ToString();
        }

        private static Assembly CompileScript(
            string source,
            out string compilerKind,
            out List<string> errors,
            out bool hasCs0161)
        {
            errors = new List<string>();
            hasCs0161 = false;

            List<string> refs = new List<string>();
            string fxDir = RuntimeEnvironment.GetRuntimeDirectory();
            foreach (string fxAsm in new[]
            {
                "mscorlib.dll",
                "System.dll",
                "System.Core.dll",
                "System.Drawing.dll",
                "System.Xml.dll",
                "System.Xml.Linq.dll",
                "Microsoft.CSharp.dll",
                "System.ValueTuple.dll",
                "netstandard.dll"
            })
            {
                string p = Path.Combine(fxDir, fxAsm);
                if (File.Exists(p)) refs.Add(p);
            }

            foreach (string path in new[]
            {
                typeof(ISldWorks).Assembly.Location,
                typeof(swDocumentTypes_e).Assembly.Location,
                typeof(SolidWorks.Interop.swpublished.ISwAddin).Assembly.Location,
                typeof(DynamicCSharpTool).Assembly.Location
            })
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !refs.Contains(path))
                    refs.Add(path);
            }

            string cscPath = FindRoslynCscPath();
            if (!string.IsNullOrEmpty(cscPath))
            {
                compilerKind = "roslyn_latest";
                string workDir = Path.Combine(Path.GetTempPath(), "sw_mcp_cs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);
                string csPath = Path.Combine(workDir, "DynamicSwScript.cs");
                string rspPath = Path.Combine(workDir, "compile.rsp");
                string dllPath = Path.Combine(workDir, "DynamicSwScript_" + Guid.NewGuid().ToString("N") + ".dll");
                try
                {
                    File.WriteAllText(csPath, source, new UTF8Encoding(false));
                    StringBuilder rsp = new StringBuilder();
                    rsp.AppendLine("/nologo");
                    rsp.AppendLine("/utf8output");
                    rsp.AppendLine("/target:library");
                    rsp.AppendLine("/langversion:latest");
                    rsp.AppendLine("/platform:x64");
                    rsp.AppendLine("/nostdlib+");
                    foreach (string r in refs)
                        rsp.AppendLine("\"/r:" + r + "\"");
                    rsp.AppendLine("\"/out:" + dllPath + "\"");
                    rsp.AppendLine("\"" + csPath + "\"");
                    File.WriteAllText(rspPath, rsp.ToString(), new UTF8Encoding(false));

                    ProcessStartInfo psi = new ProcessStartInfo(cscPath, "@\"" + rspPath + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (Process proc = Process.Start(psi))
                    {
                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();
                        if (!proc.WaitForExit(15000))
                        {
                            try { proc.Kill(); } catch { }
                            errors.Add("Roslyn csc.exe timed out after 15s.");
                            return null;
                        }

                        if (proc.ExitCode != 0 || !File.Exists(dllPath))
                        {
                            string combined = (stdout + "\n" + stderr).Trim();
                            foreach (string rawLine in combined.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string line = rawLine.Trim();
                                if (line.Contains("CS0161")) hasCs0161 = true;
                                if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0)
                                    errors.Add(line);
                            }
                            if (errors.Count == 0 && !string.IsNullOrWhiteSpace(combined))
                                errors.Add(combined);
                            return null;
                        }

                        byte[] rawBytes = File.ReadAllBytes(dllPath);
                        return Assembly.Load(rawBytes);
                    }
                }
                finally
                {
                    try { Directory.Delete(workDir, true); } catch { }
                }
            }

            // Fallback to CodeDom CSharpCodeProvider
            compilerKind = "codedom_legacy";
            CSharpCodeProvider provider = new CSharpCodeProvider();
            CompilerParameters cp = new CompilerParameters
            {
                GenerateInMemory = true,
                TreatWarningsAsErrors = false
            };
            foreach (string r in refs)
            {
                if (!r.EndsWith("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    cp.ReferencedAssemblies.Add(r);
            }
            CompilerResults cr = provider.CompileAssemblyFromSource(cp, source);
            if (cr.Errors.HasErrors)
            {
                foreach (CompilerError ce in cr.Errors)
                {
                    if (!ce.IsWarning)
                    {
                        if (string.Equals(ce.ErrorNumber, "CS0161", StringComparison.OrdinalIgnoreCase))
                            hasCs0161 = true;
                        errors.Add(string.Format("Line {0} ({1}): {2}", ce.Line, ce.ErrorNumber, ce.ErrorText));
                    }
                }
                return null;
            }
            return cr.CompiledAssembly;
        }

        private static McpToolResult HandleExecuteCSharp(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string codeBody = JsonHelper.GetString(args, "code_body", string.Empty);
            string undoName = JsonHelper.GetString(args, "undo_name", "MCP Dynamic C#");
            bool rollback = JsonHelper.GetBool(args, "rollback_on_error", true);
            bool compileOnly = JsonHelper.GetBool(args, "compile_only", false);
            bool allowDisconnected = JsonHelper.GetBool(args, "allow_disconnected", false);
            List<string> extraUsings = JsonHelper.GetStringList(args, "additional_usings");

            // Static guard against modal-dialog & footgun API calls
            string stripped = StripCSharpComments(codeBody);
            if (stripped.IndexOf("swUserDecision", StringComparison.Ordinal) >= 0)
            {
                return McpToolResult.Error(
                    "Blocked 'swUserDecision' in sw_execute_csharp because it can pop a modal rebuild confirmation dialog and hang the STA COM thread. Use SwHelper.Activate(docName) or swRebuildOnActivation_e.swDontRebuildActiveDoc instead.");
            }
            if (Regex.IsMatch(stripped, @"\.EditDelete\s*\("))
            {
                return McpToolResult.Error(
                    "Blocked '.EditDelete()' in sw_execute_csharp because it can trigger modal delete confirmation prompts. Use doc.Extension.DeleteSelection2(0) instead.");
            }
            if (stripped.IndexOf("CommandInProgress", StringComparison.Ordinal) >= 0)
            {
                return McpToolResult.Error(
                    "Blocked 'CommandInProgress' in sw_execute_csharp because setting CommandInProgress = true triggers an Access Violation in slduiu:uiModelDoc_c::conditionalBlockCmd during sketch/feature operations in SOLIDWORKS 2026 SP04.");
            }

            List<string> logs = new List<string>();
            if (Regex.IsMatch(stripped, @"\b(for|foreach|while)\b") &&
                stripped.IndexOf("ForceRebuild3", StringComparison.Ordinal) >= 0)
            {
                logs.Add("WARNING: ForceRebuild3 detected inside a loop; prefer doc.EditRebuild3() for assembly mate changes (<0.5s vs ~10s per call) to avoid 3m0s timeouts.");
            }

            string src = BuildSource(codeBody, extraUsings, false);
            Assembly compiledAsm = CompileScript(src, out string compilerKind, out List<string> errs, out bool hasCs0161);
            if (compiledAsm == null && hasCs0161)
            {
                // code_body likely had local functions with return statements or an early return; append trailing return null
                string srcRetry = BuildSource(codeBody, extraUsings, true);
                Assembly retryAsm = CompileScript(srcRetry, out compilerKind, out List<string> retryErrs, out _);
                if (retryAsm != null)
                {
                    compiledAsm = retryAsm;
                    errs = retryErrs;
                }
            }

            if (compiledAsm == null)
            {
                return McpToolResult.Error("C# compilation failed (" + compilerKind + ").", errs);
            }

            if (compileOnly)
            {
                return McpToolResult.JsonText(new
                {
                    compiled = true,
                    executed = false,
                    compiler = compilerKind,
                    logs = logs
                });
            }

            ISldWorks app = null;
            IModelDoc2 model = null;
            if (allowDisconnected)
            {
                if (swMgr.TryAttachOnly(out ISldWorks attached))
                {
                    app = attached;
                    string td = JsonHelper.GetString(args, "target_doc");
                    if (!string.IsNullOrWhiteSpace(td))
                    {
                        try { model = swMgr.RequireModel(td); } catch { }
                    }
                    else
                    {
                        try { model = app.ActiveDoc as IModelDoc2; } catch { }
                    }
                }
            }
            else
            {
                app = swMgr.GetApp(false);
                string targetDoc = JsonHelper.GetString(args, "target_doc");
                if (!string.IsNullOrWhiteSpace(targetDoc))
                    model = swMgr.RequireModel(targetDoc);
                else
                    model = app.ActiveDoc as IModelDoc2;
            }

            Action<string> logFn = msg => logs.Add(msg ?? string.Empty);

            bool touchesOtherDocs =
                stripped.IndexOf("NewDocument", StringComparison.Ordinal) >= 0 ||
                stripped.IndexOf("OpenDoc", StringComparison.Ordinal) >= 0 ||
                stripped.IndexOf("LoadFile", StringComparison.Ordinal) >= 0 ||
                stripped.IndexOf("CloseDoc", StringComparison.Ordinal) >= 0 ||
                stripped.IndexOf("ActivateDoc", StringComparison.Ordinal) >= 0 ||
                stripped.IndexOf("SwHelper.Activate", StringComparison.Ordinal) >= 0;

            bool hasExplicitUndo = args.ContainsKey("use_undo_transaction");
            bool useUndo = hasExplicitUndo
                ? JsonHelper.GetBool(args, "use_undo_transaction", true)
                : (rollback && model != null && !touchesOtherDocs);

            if (touchesOtherDocs && !hasExplicitUndo && model != null && rollback)
            {
                logs.Add("Auto-skipped StartRecordingUndoObject because code_body creates/opens/activates/closes documents.");
            }

            MethodInfo mi = compiledAsm.GetType("DynamicSwScript").GetMethod("Run");
            Func<object> invoke = () =>
            {
                return SwConnectionManager.RunWithModalGuard(app, () =>
                {
                    SwHelper.BindSession(app, model, logFn);
                    try
                    {
                        return mi.Invoke(null, new object[]
                        {
                            app,
                            model,
                            model as IPartDoc,
                            model as IAssemblyDoc,
                            model as IDrawingDoc,
                            logFn
                        });
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        throw tie.InnerException;
                    }
                    finally
                    {
                        SwHelper.ClearSession();
                    }
                });
            };

            try
            {
                object result = useUndo && model != null
                    ? swMgr.ExecuteInUndoTransaction(model, undoName, rollback, invoke)
                    : invoke();

                return McpToolResult.JsonText(new
                {
                    compiled = true,
                    executed = true,
                    compiler = compilerKind,
                    result = result,
                    logs = logs
                });
            }
            catch (Exception ex)
            {
                return McpToolResult.JsonText(new
                {
                    compiled = true,
                    executed = false,
                    compiler = compilerKind,
                    error = ex.Message,
                    exception_type = ex.GetType().FullName,
                    details = ex.ToString(),
                    logs = logs
                }, isError: true);
            }
        }
    }
}
