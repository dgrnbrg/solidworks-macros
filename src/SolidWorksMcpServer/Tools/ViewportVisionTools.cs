using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class ViewportVisionTools
    {
        public static readonly string[] DefaultStandardViews = new[]
        {
            "*Isometric",
            "*Top",
            "*Bottom",
            "*Left",
            "*Right",
            "*Front",
            "*Back"
        };

        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_capture_viewport_quad",
                Description = "Capture a multi-view composite PNG screenshot covering all 7 standard views at a minimum (*Isometric, *Top, *Bottom, *Left, *Right, *Front, *Back) for 3D parts/assemblies (or all drawing sheets if target_doc is a .SLDDRW drawing) with view labels and overall bounding dimensions for multimodal visual self-verification.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "views", JsonHelper.ArrayProp("string", "View names to capture (default: [*Isometric, *Top, *Bottom, *Left, *Right, *Front, *Back]). When include_all_standard_views is true, any omitted standard views from the 7-view minimum set are appended automatically.") },
                    { "include_all_standard_views", JsonHelper.Prop("boolean", "Ensure all 7 minimum standard views (*Isometric, *Top, *Bottom, *Left, *Right, *Front, *Back) are included even if a shorter views list is passed.", true) },
                    { "sheets", JsonHelper.ArrayProp("string", "Optional drawing sheet names or 1-based numbers when capturing a .SLDDRW document (defaults to all sheets).") },
                    { "highlight_tokens", JsonHelper.ArrayProp("string", "Optional entity tokens/feature names to highlight before capture.") },
                    { "panel_width_px", JsonHelper.Prop("integer", "Width of each view panel in pixels.", 640) },
                    { "panel_height_px", JsonHelper.Prop("integer", "Height of each view panel in pixels.", 480) },
                    { "save_png_path", JsonHelper.Prop("string", "Optional path to save the composite PNG.") }
                }),
                Handler = args => HandleCaptureQuad(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_capture_drawing_sheets",
                Description = "Capture sharp raster PNG screenshots of one or all sheets in a SOLIDWORKS Drawing (.SLDDRW) using maximized-frame SaveBMP(path, 0, 0) (avoiding the SOLIDWORKS 2026 Connected non-zero SaveBMP blank-white image bug) for 1-call multimodal drawing verification.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional .SLDDRW drawing title/path (defaults to active drawing).") },
                    { "sheets", JsonHelper.ArrayProp("string", "Optional sheet names or 1-based sheet indices to capture (defaults to all sheets in the drawing).") },
                    { "panel_width_px", JsonHelper.Prop("integer", "Width of each sheet panel in the composite PNG.", 1200) },
                    { "panel_height_px", JsonHelper.Prop("integer", "Height of each sheet panel in the composite PNG.", 850) },
                    { "save_png_path", JsonHelper.Prop("string", "Optional path to save the composite multi-sheet PNG.") },
                    { "save_per_sheet_pngs", JsonHelper.Prop("boolean", "When true and save_png_path is set, also write individual <stem>_<sheet>.png files.", false) }
                }),
                Handler = args => HandleCaptureDrawingSheets(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_insert_sketch_picture",
                Description = "Insert a reference photo or hand sketch onto a planar face/plane and scale it so two pixel points match a real-world distance.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "plane_or_face", JsonHelper.Prop("string", "Target plane or face token (e.g. Front Plane).", "Front Plane") },
                    { "image_path", JsonHelper.Prop("string", "Absolute path to PNG/JPG/BMP image.") },
                    { "point1_px", JsonHelper.ArrayProp("number", "First calibration pixel [x, y].") },
                    { "point2_px", JsonHelper.ArrayProp("number", "Second calibration pixel [x, y].") },
                    { "real_distance", JsonHelper.Prop("number", "Real-world distance between point1_px and point2_px.") },
                    { "units", JsonHelper.Prop("string", "Units for real_distance: mm or in.") },
                    { "transparency", JsonHelper.Prop("number", "Image transparency 0.0 to 1.0.", 0.35) }
                }, "image_path", "point1_px", "point2_px", "real_distance"),
                Handler = args => HandleInsertSketchPicture(swMgr, args)
            });
        }

        public static string NormalizeStandardViewName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return null;
            string trimmed = rawName.Trim();
            string bare = trimmed.TrimStart('*').Trim().ToLowerInvariant();
            switch (bare)
            {
                case "iso":
                case "isometric":
                    return "*Isometric";
                case "top":
                    return "*Top";
                case "bottom":
                case "bot":
                    return "*Bottom";
                case "left":
                    return "*Left";
                case "right":
                    return "*Right";
                case "front":
                    return "*Front";
                case "back":
                case "rear":
                    return "*Back";
                case "trimetric":
                    return "*Trimetric";
                case "dimetric":
                    return "*Dimetric";
                default:
                    return trimmed;
            }
        }

        public static List<string> ResolveCaptureViewList(List<string> rawViews, bool includeAllStandardViews = true)
        {
            List<string> resolved = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (rawViews != null)
            {
                foreach (string v in rawViews)
                {
                    string norm = NormalizeStandardViewName(v);
                    if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
                        resolved.Add(norm);
                }
            }

            if (resolved.Count == 0 || includeAllStandardViews)
            {
                foreach (string std in DefaultStandardViews)
                {
                    if (seen.Add(std))
                        resolved.Add(std);
                }
            }

            return resolved;
        }

        private static McpToolResult HandleCaptureQuad(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (doc is IDrawingDoc)
                return HandleCaptureDrawingSheets(swMgr, args);
            try { SwConnectionManager.RestoreNonMdiControlBarsVisible(swMgr.GetApp(false), force: true); } catch { }
            bool includeAllStandard = JsonHelper.GetBool(args, "include_all_standard_views", true);
            List<string> views = ResolveCaptureViewList(JsonHelper.GetStringList(args, "views"), includeAllStandard);

            List<string> highlights = JsonHelper.GetStringList(args, "highlight_tokens");
            int pw = Math.Max(320, JsonHelper.GetInt(args, "panel_width_px", 640));
            int ph = Math.Max(240, JsonHelper.GetInt(args, "panel_height_px", 480));
            string savePath = JsonHelper.GetString(args, "save_png_path");

            doc.ClearSelection2(true);
            for (int i = 0; i < highlights.Count; i++)
                SwConnectionManager.TrySelectByToken(doc, highlights[i], i > 0, 0);

            string units = SwConnectionManager.GetDocumentUnits(doc);
            string dimBanner = string.Format("{0} ({1})", doc.GetTitle(), units.ToUpperInvariant());
            double[] bb = null;
            if (doc is IPartDoc part)
            {
                try { bb = part.GetPartBox(true) as double[]; } catch { }
            }
            else if (doc is IAssemblyDoc assy)
            {
                try { bb = assy.GetBox(0) as double[]; } catch { }
            }
            if (bb != null && bb.Length >= 6)
            {
                dimBanner = string.Format("{0} | {1:F2} x {2:F2} x {3:F2} {4}",
                    doc.GetTitle(),
                    ShopCatalogService.FromMeters(bb[3] - bb[0], units),
                    ShopCatalogService.FromMeters(bb[4] - bb[1], units),
                    ShopCatalogService.FromMeters(bb[5] - bb[2], units),
                    units);
            }

            int viewCount = views.Count;
            int cols = viewCount <= 4 ? 2 : (viewCount <= 6 ? 3 : 4);
            int rows = Math.Max(1, (viewCount + cols - 1) / cols);
            int footerHeight = 38;
            int canvasW = pw * cols;
            int canvasH = ph * rows + footerHeight;

            string tempDir = Path.Combine(Path.GetTempPath(), "sw_mcp_quad_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (Bitmap composite = new Bitmap(canvasW, canvasH))
                using (Graphics g = Graphics.FromImage(composite))
                using (Font labelFont = new Font("Segoe UI", 12, FontStyle.Bold))
                using (Font dimFont = new Font("Segoe UI", 10, FontStyle.Regular))
                using (SolidBrush bgPill = new SolidBrush(Color.FromArgb(200, 24, 32, 44)))
                using (SolidBrush fgText = new SolidBrush(Color.FromArgb(245, 248, 250)))
                using (SolidBrush mutedText = new SolidBrush(Color.FromArgb(180, 196, 214)))
                using (Pen borderPen = new Pen(Color.FromArgb(100, 116, 139), 2))
                {
                    g.Clear(Color.FromArgb(24, 28, 36));

                    for (int i = 0; i < viewCount; i++)
                    {
                        string vName = views[i];
                        doc.ShowNamedView2(vName, -1);
                        doc.ViewZoomtofit2();

                        string bmpPath = Path.Combine(tempDir, "view_" + i + ".bmp");
                        doc.SaveBMP(bmpPath, pw, ph);

                        int col = i % cols;
                        int row = i / cols;
                        int x0 = col * pw;
                        int y0 = row * ph;

                        if (File.Exists(bmpPath))
                        {
                            using (Image img = Image.FromFile(bmpPath))
                            {
                                g.DrawImage(img, x0, y0, pw, ph);
                            }
                        }

                        g.DrawRectangle(borderPen, x0, y0, pw - 1, ph - 1);
                        g.FillRectangle(bgPill, x0 + 8, y0 + 8, 140, 26);
                        g.DrawString(vName.TrimStart('*'), labelFont, fgText, x0 + 14, y0 + 10);
                    }

                    int totalCells = cols * rows;
                    for (int cellIdx = viewCount; cellIdx < totalCells; cellIdx++)
                    {
                        int col = cellIdx % cols;
                        int row = cellIdx / cols;
                        int x0 = col * pw;
                        int y0 = row * ph;

                        g.DrawRectangle(borderPen, x0, y0, pw - 1, ph - 1);
                        g.FillRectangle(bgPill, x0 + 8, y0 + 8, pw - 16, 28);
                        g.DrawString("7-View Inspection Summary", labelFont, fgText, x0 + 14, y0 + 11);

                        string viewsSummary = "Views (" + viewCount + "): " + string.Join(", ", views.ConvertAll(v => v.TrimStart('*')));
                        g.DrawString("Doc: " + doc.GetTitle(), dimFont, fgText, new RectangleF(x0 + 16, y0 + 50, pw - 32, 44));
                        g.DrawString("BBox: " + dimBanner, dimFont, mutedText, new RectangleF(x0 + 16, y0 + 96, pw - 32, 48));
                        g.DrawString(viewsSummary, dimFont, mutedText, new RectangleF(x0 + 16, y0 + 148, pw - 32, 60));
                        g.DrawString("Triad: +X Red | +Y Green | +Z Blue", dimFont, fgText, new RectangleF(x0 + 16, y0 + 212, pw - 32, 32));
                    }

                    // Bottom dimensions banner in dedicated footer strip
                    int footerY = ph * rows + 6;
                    g.FillRectangle(bgPill, 8, footerY, canvasW - 16, 26);
                    g.DrawString(dimBanner + " | Views: " + viewCount + " (" + string.Join(", ", views.ConvertAll(v => v.TrimStart('*'))) + ") | Triad: +X Red, +Y Green, +Z Blue", dimFont, fgText, 16, footerY + 4);

                    byte[] pngBytes;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        composite.Save(ms, ImageFormat.Png);
                        pngBytes = ms.ToArray();
                    }

                    if (!string.IsNullOrWhiteSpace(savePath))
                    {
                        string dir = Path.GetDirectoryName(savePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(savePath, pngBytes);
                    }

                    McpToolResult res = McpToolResult.JsonText(new
                    {
                        title = doc.GetTitle(),
                        summary = dimBanner,
                        view_count = viewCount,
                        grid_layout = cols + "x" + rows,
                        views = views,
                        saved_png_path = savePath
                    });
                    res.AddImagePng(pngBytes);
                    return res;
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        public static List<string> ResolveDrawingSheetNames(string[] allSheetNames, List<string> requestedSheets)
        {
            List<string> available = new List<string>();
            if (allSheetNames != null)
            {
                foreach (string s in allSheetNames)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                        available.Add(s);
                }
            }
            if (available.Count == 0)
                return available;

            if (requestedSheets == null || requestedSheets.Count == 0)
                return available;

            List<string> resolved = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string req in requestedSheets)
            {
                if (string.IsNullOrWhiteSpace(req)) continue;
                string trimmed = req.Trim();
                string matched = null;
                foreach (string av in available)
                {
                    if (string.Equals(av, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = av;
                        break;
                    }
                }
                if (matched == null && int.TryParse(trimmed, out int oneBasedIdx) && oneBasedIdx >= 1 && oneBasedIdx <= available.Count)
                {
                    matched = available[oneBasedIdx - 1];
                }
                if (matched != null && seen.Add(matched))
                    resolved.Add(matched);
            }

            return resolved.Count > 0 ? resolved : available;
        }

        internal static McpToolResult HandleCaptureDrawingSheets(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            if (!(doc is IDrawingDoc drw))
                return McpToolResult.Error("sw_capture_drawing_sheets requires an active Drawing (.SLDDRW) document.");

            ISldWorks app = swMgr.GetApp(false);
            try { SwConnectionManager.RestoreNonMdiControlBarsVisible(app, force: true); } catch { }
            SwConnectionManager.ActivateDocumentSafe(app, doc);

            try { if (app != null) app.FrameState = (int)swWindowState_e.swWindowMaximized; } catch { }
            try
            {
                IModelView mv = doc.ActiveView as IModelView;
                if (mv != null) mv.FrameState = (int)swWindowState_e.swWindowMaximized;
            }
            catch { }

            string[] allSheets = drw.GetSheetNames() as string[];
            List<string> targetSheets = ResolveDrawingSheetNames(allSheets, JsonHelper.GetStringList(args, "sheets"));
            if (targetSheets.Count == 0)
            {
                ISheet cur = drw.GetCurrentSheet() as ISheet;
                if (cur != null && !string.IsNullOrWhiteSpace(cur.GetName()))
                    targetSheets.Add(cur.GetName());
            }
            if (targetSheets.Count == 0)
                return McpToolResult.Error("Drawing contains no sheets to capture.");

            int pw = Math.Max(480, JsonHelper.GetInt(args, "panel_width_px", 1200));
            int ph = Math.Max(360, JsonHelper.GetInt(args, "panel_height_px", 850));
            string savePath = JsonHelper.GetString(args, "save_png_path");
            bool savePerSheet = JsonHelper.GetBool(args, "save_per_sheet_pngs", false);

            int sheetCount = targetSheets.Count;
            int cols = sheetCount == 1 ? 1 : 2;
            int rows = Math.Max(1, (sheetCount + cols - 1) / cols);
            int footerHeight = 38;
            int canvasW = pw * cols;
            int canvasH = ph * rows + footerHeight;

            string tempDir = Path.Combine(Path.GetTempPath(), "sw_mcp_drw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            List<string> perSheetSavedPaths = new List<string>();

            try
            {
                using (Bitmap composite = new Bitmap(canvasW, canvasH))
                using (Graphics g = Graphics.FromImage(composite))
                using (Font labelFont = new Font("Segoe UI", 12, FontStyle.Bold))
                using (Font dimFont = new Font("Segoe UI", 10, FontStyle.Regular))
                using (SolidBrush bgPill = new SolidBrush(Color.FromArgb(210, 24, 32, 44)))
                using (SolidBrush fgText = new SolidBrush(Color.FromArgb(245, 248, 250)))
                using (Pen borderPen = new Pen(Color.FromArgb(100, 116, 139), 2))
                {
                    g.Clear(Color.FromArgb(24, 28, 36));

                    for (int i = 0; i < sheetCount; i++)
                    {
                        string sheetName = targetSheets[i];
                        try { drw.ActivateSheet(sheetName); } catch { }
                        doc.ClearSelection2(true);
                        try
                        {
                            IModelView mv = doc.ActiveView as IModelView;
                            if (mv != null) mv.FrameState = (int)swWindowState_e.swWindowMaximized;
                        }
                        catch { }
                        doc.ViewZoomtofit2();
                        doc.GraphicsRedraw2();

                        // SOLIDWORKS 2026 Connected bug workaround: non-zero SaveBMP(path, w, h) on drawings
                        // produces blank white images; SaveBMP(path, 0, 0) captures the maximized viewport cleanly.
                        string bmpPath = Path.Combine(tempDir, "sheet_" + i + ".bmp");
                        doc.SaveBMP(bmpPath, 0, 0);

                        int col = i % cols;
                        int row = i / cols;
                        int x0 = col * pw;
                        int y0 = row * ph;

                        if (File.Exists(bmpPath))
                        {
                            using (Image img = Image.FromFile(bmpPath))
                            {
                                g.DrawImage(img, x0, y0, pw, ph);
                                if (savePerSheet && !string.IsNullOrWhiteSpace(savePath))
                                {
                                    string dir = Path.GetDirectoryName(savePath);
                                    string stem = Path.GetFileNameWithoutExtension(savePath);
                                    string safeSheet = Regex.Replace(sheetName, @"[^\w\-]+", "_");
                                    string sheetPng = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, string.Format("{0}_{1}.png", stem, safeSheet));
                                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                        Directory.CreateDirectory(dir);
                                    img.Save(sheetPng, ImageFormat.Png);
                                    perSheetSavedPaths.Add(sheetPng);
                                }
                            }
                        }

                        g.DrawRectangle(borderPen, x0, y0, pw - 1, ph - 1);
                        string badge = string.Format("Sheet {0}/{1}: {2}", i + 1, sheetCount, sheetName);
                        int pillW = Math.Min(pw - 16, Math.Max(180, badge.Length * 9 + 24));
                        g.FillRectangle(bgPill, x0 + 8, y0 + 8, pillW, 28);
                        g.DrawString(badge, labelFont, fgText, x0 + 14, y0 + 10);
                    }

                    int footerY = ph * rows + 6;
                    g.FillRectangle(bgPill, 8, footerY, canvasW - 16, 26);
                    g.DrawString(
                        string.Format("{0} | Sheets Captured: {1} ({2}) | SaveBMP(0,0) Raster",
                            doc.GetTitle(), sheetCount, string.Join(", ", targetSheets)),
                        dimFont, fgText, 16, footerY + 4);

                    byte[] pngBytes;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        composite.Save(ms, ImageFormat.Png);
                        pngBytes = ms.ToArray();
                    }

                    if (!string.IsNullOrWhiteSpace(savePath))
                    {
                        string dir = Path.GetDirectoryName(savePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(savePath, pngBytes);
                    }

                    McpToolResult res = McpToolResult.JsonText(new
                    {
                        title = doc.GetTitle(),
                        sheet_count = sheetCount,
                        grid_layout = cols + "x" + rows,
                        sheets = targetSheets,
                        saved_png_path = savePath,
                        per_sheet_png_paths = perSheetSavedPaths
                    });
                    res.AddImagePng(pngBytes);
                    return res;
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static McpToolResult HandleInsertSketchPicture(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string planeOrFace = JsonHelper.GetString(args, "plane_or_face", "Front Plane");
            string imagePath = JsonHelper.GetString(args, "image_path");
            double[] p1 = JsonHelper.GetDoubleArray(args, "point1_px");
            double[] p2 = JsonHelper.GetDoubleArray(args, "point2_px");
            double realDist = JsonHelper.GetDouble(args, "real_distance", 1.0);
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            double transparency = JsonHelper.GetDouble(args, "transparency", 0.35);

            if (!File.Exists(imagePath))
                return McpToolResult.Error("Image file not found: " + imagePath);
            if (p1.Length < 2 || p2.Length < 2)
                return McpToolResult.Error("point1_px and point2_px must each have [x, y] pixel coordinates.");

            int imgW, imgH;
            using (Image img = Image.FromFile(imagePath))
            {
                imgW = img.Width;
                imgH = img.Height;
            }

            double dx = p2[0] - p1[0];
            double dy = p2[1] - p1[1];
            double pxDist = Math.Sqrt(dx * dx + dy * dy);
            if (pxDist < 1e-6)
                return McpToolResult.Error("Calibration points cannot be identical.");

            double realMeters = ShopCatalogService.ToMeters(realDist, units);
            double metersPerPx = realMeters / pxDist;
            double widthM = imgW * metersPerPx;
            double heightM = imgH * metersPerPx;

            doc.ClearSelection2(true);
            if (!SwConnectionManager.TrySelectByToken(doc, planeOrFace, false, 0))
                return McpToolResult.Error("Could not select plane/face: " + planeOrFace);

            ISketchManager sm = doc.SketchManager;
            sm.InsertSketchSafe(true);
            ISketchPicture pic = sm.InsertSketchPicture(imagePath);
            if (pic == null)
            {
                sm.InsertSketchSafe(true);
                return McpToolResult.Error("InsertSketchPicture failed.");
            }

            pic.SetSize(widthM, heightM, true);
            pic.SetOrigin(-widthM * 0.5, -heightM * 0.5);
            pic.SetTransparency((int)swSketchPictureTransparencyStyle_e.swSketchPictureTransparencyUserDefined, transparency, 0, 0.0);
            sm.InsertSketchSafe(true);

            return McpToolResult.JsonText(new
            {
                plane = planeOrFace,
                image_path = imagePath,
                image_pixels = new[] { imgW, imgH },
                calibrated_width = Math.Round(ShopCatalogService.FromMeters(widthM, units), 4),
                calibrated_height = Math.Round(ShopCatalogService.FromMeters(heightM, units), 4),
                units = units
            });
        }
    }
}
