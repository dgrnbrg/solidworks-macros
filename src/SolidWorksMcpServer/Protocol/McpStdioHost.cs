using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;

namespace SolidWorksMcpServer.Protocol
{
    public class McpToolResult
    {
        public bool IsError { get; set; }
        public List<Dictionary<string, object>> Content { get; } = new List<Dictionary<string, object>>();

        public static McpToolResult JsonText(object payload, bool isError = false)
        {
            McpToolResult res = new McpToolResult { IsError = isError };
            res.Content.Add(new Dictionary<string, object>
            {
                { "type", "text" },
                { "text", JsonHelper.Serialize(payload) }
            });
            return res;
        }

        public static McpToolResult Error(string message, object details = null)
        {
            return JsonText(new
            {
                error = message,
                details = details
            }, isError: true);
        }

        public void AddImagePng(byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0)
                return;
            Content.Add(new Dictionary<string, object>
            {
                { "type", "image" },
                { "data", Convert.ToBase64String(pngBytes) },
                { "mimeType", "image/png" }
            });
        }
    }

    public class McpToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> InputSchema { get; set; }
        public Func<Dictionary<string, object>, McpToolResult> Handler { get; set; }
    }

    public class McpStdioHost
    {
        public const string WorkerFramePrefix = "@@MCP_JSON@@";
        private readonly Dictionary<string, McpToolDefinition> _tools = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly List<McpToolDefinition> _orderedTools = new List<McpToolDefinition>();

        public static void Log(string message)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
                Console.Error.WriteLine(line);
                string dir = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "SolidWorksMcpServer");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "mcp_debug.log"), line + System.Environment.NewLine);
            }
            catch { }
        }

        public void RegisterTool(McpToolDefinition tool)
        {
            if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                throw new ArgumentException("Tool name cannot be empty.");
            if (!_tools.ContainsKey(tool.Name))
                _orderedTools.Add(tool);
            _tools[tool.Name] = tool;
        }

        public IList<McpToolDefinition> GetTools() => _orderedTools;

        public void RunStdioLoop(bool workerFramedOutput = false)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);

            using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string response = ProcessRawLine(line);
                    if (!string.IsNullOrEmpty(response))
                    {
                        writer.WriteLine(workerFramedOutput ? (WorkerFramePrefix + response) : response);
                    }
                }
            }
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        public string ProcessRawLine(string rawJson)
        {
            object id = null;
            try
            {
                Dictionary<string, object> req = JsonHelper.DeserializeDict(rawJson);
                req.TryGetValue("id", out id);
                string method = JsonHelper.GetString(req, "method", string.Empty);

                if (string.IsNullOrEmpty(method))
                {
                    if (id == null) return null;
                    return MakeError(id, -32600, "Invalid Request: missing method");
                }

                // Notifications do not have an id and expect no response
                if (id == null || method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                Dictionary<string, object> paramsDict = JsonHelper.GetDict(req, "params") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    string requestedProtocol = JsonHelper.GetString(paramsDict, "protocolVersion", "2024-11-05");
                    return MakeResult(id, new Dictionary<string, object>
                    {
                        { "protocolVersion", requestedProtocol },
                        { "capabilities", new Dictionary<string, object>
                            {
                                { "tools", new Dictionary<string, object> { { "listChanged", false } } }
                            }
                        },
                        { "serverInfo", new Dictionary<string, object>
                            {
                                { "name", "solidworks-mcp" },
                                { "version", "1.0.0" }
                            }
                        },
                        { "instructions",
                            "SOLIDWORKS 2026 MCP Server (52 tools, early-bound COM Interop inside a self-healing worker process). " +
                            "Prefer batch-first mutations: use sw_build_part_features (with optional reset_first) to build/rebuild whole parts in 1 call, batch arrays (sketches/operations/items/routes/target_docs) on feature/sketch/save tools, and sw_manage_assembly_components(action='pose_and_mate_batch'|'batch') for assemblies. " +
                            "Enforce MMGS for 3D printing, Gridfinity, and KiCad electronics; enforce IPS for woodworking, CNC jigs/furniture, residential electrical, and SendCutSend. " +
                            "Follow Tier 1-4 shop preference hierarchy (woodworking / Tough PLA Basic / Onefinity CNC first; 1/2\" Corian + MMA / Tough PLA Pro / acrylic second; SendCutSend deprioritized). " +
                            "Export Gate: NEVER export .3MF/.STL/.DXF/.STEP/.PDF files unless explicitly asked to fabricate/order; stop at .SLDPRT/.SLDASM save + sw_audit_dfm + sw_capture_viewport_quad. " +
                            "Zero Yellow/Red Mates Gate: NEVER leave SOLIDWORKS assembly mates in yellow (warning) or red (error) states. Intermediate tools attach fast <5ms top-level mate_health; call sw_audit_assembly_mates(include_subassemblies=true) once before finishing an assembly edit until clean=true (red_mate_count=0, yellow_mate_count=0). " +
                            "Clean Viewport Gate: Never leave newly created 2D/3D sketches (including reference/cable/sweep path sketches) visible when finishing unless they were already visible beforehand or the sketch itself is the requested deliverable; hide them with BlankSketch() / SwHelper.HideSketches(...). " +
                            "Scope verification to what changed: skip sw_verify_motion_stroke when outside the swept envelope; during iterative design use model-discovered abbreviated motion stops (1-2 endpoints, plus 1 intermediate point for rotating/U-loop parts) and save exhaustive full sweeps for sign-off/fabrication. " +
                            "Call sw_inspect_topology_datums on imported STEP/SLDPRT models and sw_measure_clearance with explicit 'include_components' (exclude_same_subassembly_pairs=true default). " +
                            "Call sw_capture_viewport_quad after geometry edits for multimodal visual self-verification. " +
                            "Use sw_execute_csharp (Roslyn C# latest, SHA-256 cached, auto Undo-transactional with SwHelper.*) for custom SOLIDWORKS API operations."
                        }
                    });
                }

                if (string.Equals(method, "ping", StringComparison.Ordinal))
                {
                    return MakeResult(id, new Dictionary<string, object>());
                }

                if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    List<object> toolSchemas = new List<object>(_orderedTools.Count);
                    foreach (McpToolDefinition t in _orderedTools)
                    {
                        toolSchemas.Add(new Dictionary<string, object>
                        {
                            { "name", t.Name },
                            { "description", t.Description },
                            { "inputSchema", t.InputSchema ?? JsonHelper.Schema(null) }
                        });
                    }
                    return MakeResult(id, new Dictionary<string, object> { { "tools", toolSchemas } });
                }

                if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                {
                    string toolName = JsonHelper.GetString(paramsDict, "name", string.Empty);
                    Dictionary<string, object> args = JsonHelper.GetDict(paramsDict, "arguments") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                    if (!_tools.TryGetValue(toolName, out McpToolDefinition def))
                    {
                        return MakeError(id, -32602, "Unknown tool: " + toolName);
                    }

                    McpToolResult toolRes;
                    try
                    {
                        toolRes = def.Handler(args);
                    }
                    catch (Exception ex)
                    {
                        Log("Unhandled exception in tool " + toolName + ": " + ex);
                        toolRes = McpToolResult.Error("Tool execution exception: " + ex.Message, ex.ToString());
                    }

                    return MakeToolCallResultJson(id, toolRes);
                }

                return MakeError(id, -32601, "Method not found: " + method);
            }
            catch (Exception ex)
            {
                Log("JSON-RPC parse/dispatch error: " + ex);
                if (id == null)
                    return MakeError(null, -32700, "Parse error: " + ex.Message);
                return MakeError(id, -32603, "Internal error: " + ex.Message);
            }
        }

        public static string MakeToolCallResultJson(object id, McpToolResult toolRes)
        {
            McpToolResult safeRes = toolRes ?? McpToolResult.Error("Null tool result.");
            return MakeResult(id, new Dictionary<string, object>
            {
                { "content", safeRes.Content },
                { "isError", safeRes.IsError }
            });
        }

        private static string MakeResult(object id, object result)
        {
            return JsonHelper.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            });
        }

        private static string MakeError(object id, int code, string message)
        {
            return JsonHelper.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            });
        }
    }
}
