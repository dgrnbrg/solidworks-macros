using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class ViewportVisionTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_capture_viewport_quad",
                Description = "Capture a composite 4-up PNG screenshot (Isometric, Top, Front, Right) with view labels and overall bounding dimensions for multimodal visual self-verification.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "views", JsonHelper.ArrayProp("string", "4 view names (default: [*Isometric, *Top, *Front, *Right]).") },
                    { "highlight_tokens", JsonHelper.ArrayProp("string", "Optional entity tokens/feature names to highlight before capture.") },
                    { "panel_width_px", JsonHelper.Prop("integer", "Width of each quadrant in pixels.", 640) },
                    { "panel_height_px", JsonHelper.Prop("integer", "Height of each quadrant in pixels.", 480) },
                    { "save_png_path", JsonHelper.Prop("string", "Optional path to save the 4-up PNG.") }
                }),
                Handler = args => HandleCaptureQuad(swMgr, args)
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

        private static McpToolResult HandleCaptureQuad(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<string> views = JsonHelper.GetStringList(args, "views");
            if (views.Count < 4)
                views = new List<string> { "*Isometric", "*Top", "*Front", "*Right" };

            List<string> highlights = JsonHelper.GetStringList(args, "highlight_tokens");
            int pw = Math.Max(320, JsonHelper.GetInt(args, "panel_width_px", 640));
            int ph = Math.Max(240, JsonHelper.GetInt(args, "panel_height_px", 480));
            string savePath = JsonHelper.GetString(args, "save_png_path");

            doc.ClearSelection2(true);
            for (int i = 0; i < highlights.Count; i++)
                SwConnectionManager.TrySelectByToken(doc, highlights[i], i > 0, 0);

            string units = SwConnectionManager.GetDocumentUnits(doc);
            string dimBanner = string.Format("{0} ({1})", doc.GetTitle(), units.ToUpperInvariant());
            if (doc is IPartDoc part)
            {
                double[] bb = part.GetPartBox(true) as double[];
                if (bb != null && bb.Length >= 6)
                {
                    dimBanner = string.Format("{0} | {1:F2} x {2:F2} x {3:F2} {4}",
                        doc.GetTitle(),
                        ShopCatalogService.FromMeters(bb[3] - bb[0], units),
                        ShopCatalogService.FromMeters(bb[4] - bb[1], units),
                        ShopCatalogService.FromMeters(bb[5] - bb[2], units),
                        units);
                }
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "sw_mcp_quad_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (Bitmap composite = new Bitmap(pw * 2, ph * 2))
                using (Graphics g = Graphics.FromImage(composite))
                using (Font labelFont = new Font("Segoe UI", 12, FontStyle.Bold))
                using (Font dimFont = new Font("Segoe UI", 10, FontStyle.Regular))
                using (SolidBrush bgPill = new SolidBrush(Color.FromArgb(200, 24, 32, 44)))
                using (SolidBrush fgText = new SolidBrush(Color.FromArgb(245, 248, 250)))
                using (Pen borderPen = new Pen(Color.FromArgb(100, 116, 139), 2))
                {
                    g.Clear(Color.FromArgb(24, 28, 36));

                    for (int i = 0; i < 4; i++)
                    {
                        string vName = views[i];
                        doc.ShowNamedView2(vName, -1);
                        doc.ViewZoomtofit2();

                        string bmpPath = Path.Combine(tempDir, "view_" + i + ".bmp");
                        doc.SaveBMP(bmpPath, pw, ph);

                        int col = i % 2;
                        int row = i / 2;
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
                        g.FillRectangle(bgPill, x0 + 8, y0 + 8, 130, 26);
                        g.DrawString(vName.TrimStart('*'), labelFont, fgText, x0 + 14, y0 + 10);
                    }

                    // Bottom dimensions banner
                    g.FillRectangle(bgPill, 8, (ph * 2) - 34, (pw * 2) - 16, 26);
                    g.DrawString(dimBanner + " | Triad: +X Red, +Y Green, +Z Blue", dimFont, fgText, 16, (ph * 2) - 30);

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
