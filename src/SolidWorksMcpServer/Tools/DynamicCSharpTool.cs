using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
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
                Description = "Compile and run arbitrary early-bound C# code against live SOLIDWORKS COM interfaces (swApp, swModel, swPart, swAssy, swDraw, log) inside an automatic Undo transaction for 100% SOLIDWORKS API coverage.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "code_body", JsonHelper.Prop("string", "C# statements inside a method returning object (or null). Available locals: ISldWorks swApp, IModelDoc2 swModel, IPartDoc swPart, IAssemblyDoc swAssy, IDrawingDoc swDraw, Action<string> log.") },
                    { "undo_name", JsonHelper.Prop("string", "Undo transaction label.", "MCP Dynamic C#") },
                    { "rollback_on_error", JsonHelper.Prop("boolean", "Automatically undo model edits if an exception occurs.", true) },
                    { "use_undo_transaction", JsonHelper.Prop("boolean", "Wrap execution in StartRecordingUndoObject on target_doc/ActiveDoc (auto-disabled when creating, opening, or switching documents).", true) },
                    { "additional_usings", JsonHelper.ArrayProp("string", "Optional extra C# namespaces.") },
                    { "compile_only", JsonHelper.Prop("boolean", "Compile-check the C# snippet without invoking SOLIDWORKS (useful for offline tests).", false) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "code_body"),
                Handler = args => HandleExecuteCSharp(swMgr, args)
            });
        }

        private static McpToolResult HandleExecuteCSharp(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            string codeBody = JsonHelper.GetString(args, "code_body", string.Empty);
            string undoName = JsonHelper.GetString(args, "undo_name", "MCP Dynamic C#");
            bool rollback = JsonHelper.GetBool(args, "rollback_on_error", true);
            bool compileOnly = JsonHelper.GetBool(args, "compile_only", false);
            List<string> extraUsings = JsonHelper.GetStringList(args, "additional_usings");

            StringBuilder src = new StringBuilder();
            src.AppendLine("using System;");
            src.AppendLine("using System.Collections.Generic;");
            src.AppendLine("using System.IO;");
            src.AppendLine("using System.Linq;");
            src.AppendLine("using SolidWorks.Interop.sldworks;");
            src.AppendLine("using SolidWorks.Interop.swconst;");
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
            if (!codeBody.Contains("return "))
                src.AppendLine("        return null;");
            src.AppendLine("    }");
            src.AppendLine("}");

            CSharpCodeProvider provider = new CSharpCodeProvider();
            CompilerParameters cp = new CompilerParameters
            {
                GenerateInMemory = true,
                TreatWarningsAsErrors = false
            };

            cp.ReferencedAssemblies.Add("System.dll");
            cp.ReferencedAssemblies.Add("System.Core.dll");
            cp.ReferencedAssemblies.Add("System.Drawing.dll");
            cp.ReferencedAssemblies.Add("Microsoft.CSharp.dll");
            cp.ReferencedAssemblies.Add(typeof(ISldWorks).Assembly.Location);
            cp.ReferencedAssemblies.Add(typeof(swDocumentTypes_e).Assembly.Location);

            CompilerResults cr = provider.CompileAssemblyFromSource(cp, src.ToString());
            if (cr.Errors.HasErrors)
            {
                List<string> errs = new List<string>();
                foreach (CompilerError ce in cr.Errors)
                {
                    if (!ce.IsWarning)
                        errs.Add(string.Format("Line {0}: {1}", ce.Line, ce.ErrorText));
                }
                return McpToolResult.Error("C# compilation failed.", errs);
            }

            if (compileOnly)
            {
                return McpToolResult.JsonText(new { compiled = true, executed = false });
            }

            ISldWorks app = swMgr.GetApp(false);
            IModelDoc2 model = null;
            string targetDoc = JsonHelper.GetString(args, "target_doc");
            if (!string.IsNullOrWhiteSpace(targetDoc))
                model = swMgr.RequireModel(targetDoc);
            else
                model = app.ActiveDoc as IModelDoc2;

            List<string> logs = new List<string>();
            Action<string> logFn = msg => logs.Add(msg);

            bool touchesOtherDocs =
                codeBody.IndexOf("NewDocument", StringComparison.Ordinal) >= 0 ||
                codeBody.IndexOf("OpenDoc", StringComparison.Ordinal) >= 0 ||
                codeBody.IndexOf("LoadFile", StringComparison.Ordinal) >= 0 ||
                codeBody.IndexOf("CloseDoc", StringComparison.Ordinal) >= 0 ||
                codeBody.IndexOf("ActivateDoc", StringComparison.Ordinal) >= 0;

            bool hasExplicitUndo = args.ContainsKey("use_undo_transaction");
            bool useUndo = hasExplicitUndo
                ? JsonHelper.GetBool(args, "use_undo_transaction", true)
                : (rollback && model != null && !touchesOtherDocs);

            if (touchesOtherDocs && !hasExplicitUndo && model != null && rollback)
            {
                logs.Add("Auto-skipped StartRecordingUndoObject because code_body creates/opens/activates/closes documents.");
            }

            MethodInfo mi = cr.CompiledAssembly.GetType("DynamicSwScript").GetMethod("Run");
            Func<object> invoke = () =>
            {
                bool prevCmd = false;
                try
                {
                    prevCmd = app.CommandInProgress;
                    app.CommandInProgress = true;
                }
                catch { }

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
                    try { app.CommandInProgress = prevCmd; } catch { }
                    try { app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART); } catch { }
                }
            };

            object result = useUndo && model != null
                ? swMgr.ExecuteInUndoTransaction(model, undoName, rollback, invoke)
                : invoke();

            return McpToolResult.JsonText(new
            {
                compiled = true,
                executed = true,
                result = result,
                logs = logs
            });
        }
    }
}
