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
    }
}
