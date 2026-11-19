using System;
using System.Collections.Generic;
using System.IO;
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

        public void RunStdioLoop()
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
                        writer.WriteLine(response);
                    }
                }
            }
        }

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
                            "SOLIDWORKS 2026 MCP Server (early-bound COM Interop). " +
                            "Enforce MMGS for 3D printing, Gridfinity, and KiCad electronics; enforce IPS for woodworking, CNC jigs/furniture, residential electrical, and SendCutSend. " +
                            "Follow Tier 1-4 shop preference hierarchy (woodworking / Tough PLA Basic / Onefinity CNC first; 1/2\" Corian + MMA / Tough PLA Pro / acrylic second; SendCutSend deprioritized). " +
                            "Export Gate: NEVER export .3MF/.STL/.DXF/.STEP/.PDF files unless explicitly asked to fabricate/order; stop at .SLDPRT/.SLDASM save + sw_audit_dfm + sw_capture_viewport_quad. " +
                            "Call sw_inspect_topology_datums on imported STEP/SLDPRT models and sw_measure_clearance with explicit 'include_components' (unfiltered all-to-all interference checks are disabled). " +
                            "Call sw_capture_viewport_quad after geometry edits for multimodal visual self-verification. " +
                            "Use sw_execute_csharp (Roslyn C# latest, auto Undo-transactional with built-in SwHelper.*) for custom SOLIDWORKS API operations."
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

                    return MakeResult(id, new Dictionary<string, object>
                    {
                        { "content", toolRes.Content },
                        { "isError", toolRes.IsError }
                    });
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
