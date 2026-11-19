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
            OleMessageFilter.Register();
            try
            {
                if (TryHandle3dxCliArgs(args, out int cliExitCode))
                    return cliExitCode;

                McpStdioHost host = new McpStdioHost();
                SwConnectionManager swMgr = new SwConnectionManager();
                ShopCatalogService catalog = new ShopCatalogService();

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

                McpStdioHost.Log(string.Format("SolidWorksMcpServer started with {0} tools. Catalog dir: {1}",
                    host.GetTools().Count, catalog.DataDirectory));

                host.RunStdioLoop();
                return 0;
            }
            catch (Exception ex)
            {
                McpStdioHost.Log("Fatal SolidWorksMcpServer error: " + ex);
                return 1;
            }
            finally
            {
                OleMessageFilter.Revoke();
            }
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
