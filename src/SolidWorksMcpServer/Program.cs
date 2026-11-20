using System;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;
using SolidWorksMcpServer.Tools;

namespace SolidWorksMcpServer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (TryHandle3dxCliArgs(args, out int cliExitCode))
                return cliExitCode;

            bool workerMode = HasArg(args, "--worker");
            if (workerMode)
            {
                OleMessageFilter.Register();
                try
                {
                    McpStdioHost host = new McpStdioHost();
                    SwConnectionManager swMgr = new SwConnectionManager();
                    ShopCatalogService catalog = new ShopCatalogService();
                    RegisterAllTools(host, swMgr, catalog);

                    McpStdioHost.Log(string.Format("SolidWorksMcpServer --worker started (PID={0}, {1} tools).",
                        System.Diagnostics.Process.GetCurrentProcess().Id, host.GetTools().Count));

                    host.RunStdioLoop(workerFramedOutput: true);
                    return 0;
                }
                catch (Exception ex)
                {
                    McpStdioHost.Log("Fatal SolidWorksMcpServer --worker error: " + ex);
                    return 1;
                }
                finally
                {
                    OleMessageFilter.Revoke();
                }
            }

            try
            {
                McpStdioHost metadataHost = new McpStdioHost();
                SwConnectionManager dummySwMgr = new SwConnectionManager();
                ShopCatalogService catalog = new ShopCatalogService();
                RegisterAllTools(metadataHost, dummySwMgr, catalog);

                McpStdioHost.Log(string.Format("SolidWorksMcpServer supervisor started with {0} tools. Catalog dir: {1}",
                    metadataHost.GetTools().Count, catalog.DataDirectory));

                McpSupervisorHost.Run(metadataHost);
                return 0;
            }
            catch (Exception ex)
            {
                McpStdioHost.Log("Fatal SolidWorksMcpServer supervisor error: " + ex);
                return 1;
            }
        }

        internal static void RegisterAllTools(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            DocumentAndSessionTools.Register(host, swMgr, catalog);
            TopologyDatumExtractor.Register(host, swMgr);
            ViewportVisionTools.Register(host, swMgr);
            SketchAndEquationTools.Register(host, swMgr);
            FeatureAndFixtureTools.Register(host, swMgr, catalog);
            DrawingAndMcMasterTools.Register(host, swMgr);
            SheetMetalSendCutSendTools.Register(host, swMgr, catalog);
            FeatureTreeAndAssemblyTools.Register(host, swMgr);
            DfmAuditAndFabricationTools.Register(host, swMgr, catalog);
            KinematicsAndRoutingTools.Register(host, swMgr);
            DynamicCSharpTool.Register(host, swMgr);
        }

        private static bool HasArg(string[] args, string flag)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals((args[i] ?? string.Empty).Trim(), flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryHandle3dxCliArgs(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0)
                return false;

            bool inspect = false;
            bool launch = false;
            bool restart = false;
            bool force = false;
            int timeoutSeconds = 90;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? string.Empty).Trim();
                if (a.Equals("--inspect-3dx", StringComparison.OrdinalIgnoreCase))
                    inspect = true;
                else if (a.Equals("--launch-3dx", StringComparison.OrdinalIgnoreCase))
                    launch = true;
                else if (a.Equals("--restart-3dx", StringComparison.OrdinalIgnoreCase))
                    restart = true;
                else if (a.Equals("--force", StringComparison.OrdinalIgnoreCase))
                    force = true;
                else if (a.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[++i], out parsed) && parsed > 0)
                        timeoutSeconds = parsed;
                }
            }

            if (!inspect && !launch && !restart)
                return false;

            try
            {
                if (inspect)
                {
                    Console.WriteLine(ThreeDExperienceEdgeLauncher.InspectEdgeWindowJson());
                    return true;
                }

                SwConnectionManager swMgr = new SwConnectionManager();
                ThreeDExperienceLaunchResult res = swMgr.LaunchOrRestartVia3DExperience(restart, force, timeoutSeconds);
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    connected = res.Connected,
                    launched_process = res.LaunchedProcess,
                    restarted = res.Restarted,
                    edge_window_title = res.EdgeWindowTitle,
                    current_url = res.CurrentUrl,
                    clicked_target = res.ClickedTarget,
                    message = res.StatusMessage,
                    logs = res.Logs
                }));
                exitCode = res.Connected || res.LaunchedProcess ? 0 : 1;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    connected = false,
                    error = ex.Message
                }));
                exitCode = 1;
                return true;
            }
        }
    }
}
