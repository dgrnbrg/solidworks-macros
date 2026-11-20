using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class SketchAndEquationTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_manage_equations",
                Description = "Read, add, update, or link external shared equations.txt global variables for material thicknesses and tolerances across parts/assemblies.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "action", JsonHelper.Prop("string", "Operation to perform.", "get", new[] { "get", "set", "link_file", "sync" }) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "equations_txt_path", JsonHelper.Prop("string", "Path to shared external equations.txt.") },
                    { "variables", JsonHelper.ArrayProp("object", "List of {name: string, expression: string} global variables or equations (e.g. {name: 'Ply_3_4', expression: '0.718in'}).") }
                }, "action"),
                Handler = args => HandleManageEquations(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_2d_sketch",
                Description = "Create one 2D sketch (or a batch via 'sketches' array) on planes/faces using 3D model-space coordinates projected via ModelToSketchTransform inside AddToDB.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "plane_or_face", JsonHelper.Prop("string", "Target plane name or face token.", "Front Plane") },
                    { "sketch_name", JsonHelper.Prop("string", "Optional name for the created sketch.") },
                    { "units", JsonHelper.Prop("string", "Coordinate units: mm, in, or m (defaults to active doc units).") },
                    { "model_space_coords", JsonHelper.Prop("boolean", "If true (default), project 3D coordinates through ModelToSketchTransform; if false, use raw sketch 2D coordinates.", true) },
                    { "entities", JsonHelper.ArrayProp("object", "Array of sketch objects: {type: 'line'|'centerline', p1:[x,y,z], p2:[x,y,z], construction?:bool}, {type:'circle', center:[x,y,z], radius|diameter}, {type:'rect_center', center:[x,y,z], width, height}, {type:'rect_corner', p1:[x,y,z], p2:[x,y,z]}, {type:'arc_3pt', p1, p2, mid}, {type:'slot', p1, p2, width}, {type:'polygon', center, edge_pt|across_flats|radius|diameter, sides, inscribed?:bool, start_angle_deg?:num}, {type:'spline', points:[[x,y,z],...]}.") },
                    { "constraints", JsonHelper.ArrayProp("string", "Optional SOLIDWORKS constraint names (e.g. sgFIXED, sgHORIZ, sgVERT, sgTANGENT).") },
                    { "close_sketch", JsonHelper.Prop("boolean", "Exit sketch after creation.", true) },
                    { "sketches", JsonHelper.ArrayProp("object", "Batch mode: array of [{plane_or_face, sketch_name, entities, units, model_space_coords, close_sketch}] created inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleCreate2DSketch(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_3d_guide_sketch",
                Description = "Create a 3D sketch (or batch via 'sketches' array) containing lines or smooth splines for airflow sweeps and multi-section loft guide curves.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "sketch_name", JsonHelper.Prop("string", "Optional name for the 3D sketch.") },
                    { "units", JsonHelper.Prop("string", "Coordinate units: mm, in, or m.") },
                    { "curves", JsonHelper.ArrayProp("object", "Array of 3D curve objects: {type: 'spline'|'line', points: [[x,y,z], ...]}.") },
                    { "sketches", JsonHelper.ArrayProp("object", "Batch mode: array of [{sketch_name, units, curves}] created inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleCreate3DGuideSketch(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_create_reference_geometry",
                Description = "Create a reference plane (offset, angle, midplane, coincident), axis, or coordinate system (or a batch via 'items' array).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "kind", JsonHelper.Prop("string", "Reference type.", "plane", new[] { "plane", "axis", "coord_sys" }) },
                    { "references", JsonHelper.ArrayProp("string", "Reference names or face/edge tokens.") },
                    { "offset", JsonHelper.Prop("number", "Offset distance for planes.", 0.0) },
                    { "angle_deg", JsonHelper.Prop("number", "Angle in degrees for angled planes.", 0.0) },
                    { "flip", JsonHelper.Prop("boolean", "Flip offset/angle direction.", false) },
                    { "name", JsonHelper.Prop("string", "Optional name for the created reference feature.") },
                    { "units", JsonHelper.Prop("string", "Units for offset: mm, in, or m.") },
                    { "origin_xyz", JsonHelper.ArrayProp("number", "Origin [x,y,z] for coord_sys.") },
                    { "items", JsonHelper.ArrayProp("object", "Batch mode: array of [{kind, references, offset, angle_deg, flip, name, units, origin_xyz}] created inside one Undo transaction.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleCreateRefGeometry(swMgr, args)
            });
        }

        private static McpToolResult HandleManageEquations(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            return McpToolResult.JsonText(ManageEquationsInternal(doc, args));
        }

        internal static Dictionary<string, object> ManageEquationsInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            string action = JsonHelper.GetString(args, "action", "get").ToLowerInvariant();
            string eqPath = JsonHelper.GetString(args, "equations_txt_path");
            List<object> vars = JsonHelper.GetList(args, "variables");

            IEquationMgr eqMgr = doc.GetEquationMgr();
            if (eqMgr == null)
                throw new InvalidOperationException("Could not obtain IEquationMgr from active document.");

            if (action == "set" || action == "link_file")
            {
                if (!string.IsNullOrWhiteSpace(eqPath))
                {
                    string dir = Path.GetDirectoryName(eqPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (vars.Count > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        if (File.Exists(eqPath))
                            sb.Append(File.ReadAllText(eqPath));

                        foreach (object vObj in vars)
                        {
                            if (vObj is Dictionary<string, object> vd)
                            {
                                string name = JsonHelper.GetString(vd, "name");
                                string expr = JsonHelper.GetString(vd, "expression");
                                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(expr))
                                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\"{0}\" = {1}", name, expr));
                            }
                        }
                        File.WriteAllText(eqPath, sb.ToString());
                    }
                    else if (!File.Exists(eqPath))
                    {
                        File.WriteAllText(eqPath, string.Empty);
                    }

                    eqMgr.FilePath = eqPath;
                    eqMgr.LinkToFile = true;
                    eqMgr.UpdateValuesFromExternalEquationFile();
                }
                else
                {
                    foreach (object vObj in vars)
                    {
                        if (vObj is Dictionary<string, object> vd)
                        {
                            string name = JsonHelper.GetString(vd, "name");
                            string expr = JsonHelper.GetString(vd, "expression");
                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(expr))
                            {
                                string eqStr = string.Format(CultureInfo.InvariantCulture, "\"{0}\" = {1}", name, expr);
                                eqMgr.Add3(-1, eqStr, true, (int)swInConfigurationOpts_e.swAllConfiguration, null);
                            }
                        }
                    }
                }
                eqMgr.EvaluateAll();
                doc.EditRebuild3();
            }
            else if (action == "sync")
            {
                if (!string.IsNullOrWhiteSpace(eqPath))
                {
                    eqMgr.FilePath = eqPath;
                    eqMgr.LinkToFile = true;
                }
                eqMgr.UpdateValuesFromExternalEquationFile();
                eqMgr.EvaluateAll();
                doc.EditRebuild3();
            }

            int count = eqMgr.GetCount();
            List<object> current = new List<object>();
            for (int i = 0; i < count; i++)
            {
                current.Add(new
                {
                    index = i,
                    equation = eqMgr.get_Equation(i),
                    value = eqMgr.get_Value(i),
                    global_var = eqMgr.get_GlobalVariable(i)
                });
            }

            return new Dictionary<string, object>
            {
                { "file_path", eqMgr.FilePath },
                { "link_to_file", eqMgr.LinkToFile },
                { "count", count },
                { "equations", current }
            };
        }

        private static McpToolResult HandleCreate2DSketch(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            ISldWorks app = swMgr.GetApp();
            List<object> batchSketches = JsonHelper.GetList(args, "sketches");

            if (batchSketches.Count > 0)
            {
                return swMgr.ExecuteInUndoTransaction(doc, "Create 2D Sketches Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                        foreach (object skObj in batchSketches)
                        {
                            Dictionary<string, object> skArgs = skObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(skObj));
                            if (skArgs == null) continue;
                            if (!skArgs.ContainsKey("units")) skArgs["units"] = defaultUnits;
                            created.Add(Create2DSketchInternal(app, doc, skArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            sketches = created
                        });
                    }
                });
            }

            return swMgr.ExecuteInUndoTransaction(doc, "Create 2D Sketch", true, () =>
                McpToolResult.JsonText(Create2DSketchInternal(app, doc, args)));
        }

        internal static Dictionary<string, object> Create2DSketchInternal(ISldWorks app, IModelDoc2 doc, Dictionary<string, object> args)
        {
            string planeOrFace = JsonHelper.GetString(args, "plane_or_face", "Front Plane");
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            bool useModelCoords = JsonHelper.GetBool(args, "model_space_coords", true);
            bool closeSketch = JsonHelper.GetBool(args, "close_sketch", true);
            List<object> entities = JsonHelper.GetList(args, "entities");

            ISketchManager sm = doc.SketchManager;
            IFeatureManager fm = doc.FeatureManager;
            bool prevAddToDb = sm.AddToDB;
            bool prevFeatureTree = fm.EnableFeatureTree;
            fm.EnableFeatureTree = false;
            try
            {
                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, planeOrFace, false, 0))
                    throw new InvalidOperationException("Could not select plane or face: " + planeOrFace);

                sm.AddToDB = true;
                sm.InsertSketch(true);
                doc.ClearSelection2(true);
                ISketch activeSketch = sm.ActiveSketch;
                if (activeSketch == null)
                    throw new InvalidOperationException("Failed to open 2D sketch on: " + planeOrFace);

                IMathUtility mathUtil = app.GetMathUtility() as IMathUtility;
                IMathTransform xform = activeSketch.ModelToSketchTransform;

                void Map(double[] pt, out double sx, out double sy)
                {
                    double xM = ShopCatalogService.ToMeters(pt[0], units);
                    double yM = ShopCatalogService.ToMeters(pt[1], units);
                    double zM = pt.Length > 2 ? ShopCatalogService.ToMeters(pt[2], units) : 0.0;
                    if (useModelCoords && xform != null)
                        SwConnectionManager.MapPointToSketch2D(mathUtil, xform, xM, yM, zM, out sx, out sy);
                    else
                    {
                        sx = xM;
                        sy = yM;
                    }
                }

                int createdCount = 0;

                foreach (object eObj in entities)
                {
                    Dictionary<string, object> ed = eObj as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(eObj));
                    if (ed == null) continue;
                    string type = JsonHelper.GetString(ed, "type", "line").ToLowerInvariant();
                    bool construction = JsonHelper.GetBool(ed, "construction", false);

                    if (type == "line" || type == "centerline")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        ISketchSegment seg = type == "centerline"
                            ? sm.CreateCenterLine(x1, y1, 0, x2, y2, 0)
                            : sm.CreateLine(x1, y1, 0, x2, y2, 0);
                        if (seg != null && construction) seg.ConstructionGeometry = true;
                        createdCount++;
                    }
                    else if (type == "circle")
                    {
                        double[] center = JsonHelper.GetDoubleArray(ed, "center");
                        double radiusM = ShopCatalogService.ToMeters(
                            JsonHelper.GetNullableDouble(ed, "radius") ?? (0.5 * JsonHelper.GetDouble(ed, "diameter", 1.0)),
                            units);
                        Map(center, out double cx, out double cy);
                        ISketchSegment seg = sm.CreateCircleByRadius(cx, cy, 0, radiusM);
                        if (seg != null && construction) seg.ConstructionGeometry = true;
                        createdCount++;
                    }
                    else if (type == "rect_center")
                    {
                        double[] center = JsonHelper.GetDoubleArray(ed, "center");
                        double wM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "width", 1.0), units);
                        double hM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "height", 1.0), units);
                        Map(center, out double cx, out double cy);
                        sm.CreateCenterRectangle(cx, cy, 0, cx + 0.5 * wM, cy + 0.5 * hM, 0);
                        createdCount += 4;
                    }
                    else if (type == "rect_corner")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        sm.CreateCornerRectangle(x1, y1, 0, x2, y2, 0);
                        createdCount += 4;
                    }
                    else if (type == "arc_3pt")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        double[] pMid = JsonHelper.GetDoubleArray(ed, "mid");
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        Map(pMid, out double xm, out double ym);
                        sm.Create3PointArc(x1, y1, 0, x2, y2, 0, xm, ym, 0);
                        createdCount++;
                    }
                    else if (type == "polygon")
                    {
                        double[] center = JsonHelper.GetDoubleArray(ed, "center") ?? new[] { 0.0, 0.0, 0.0 };
                        double[] edgePt = JsonHelper.GetDoubleArray(ed, "edge_pt");
                        int sides = Math.Max(3, JsonHelper.GetInt(ed, "sides", 6));
                        bool inscribed = JsonHelper.GetBool(ed, "inscribed", true);
                        double? acrossFlats = JsonHelper.GetNullableDouble(ed, "across_flats");
                        double startAngleDeg = JsonHelper.GetDouble(ed, "start_angle_deg", 0.0);
                        Map(center, out double cx, out double cy);

                        double circumR;
                        double baseAng = startAngleDeg * Math.PI / 180.0;
                        if (acrossFlats.HasValue)
                        {
                            double inR = ShopCatalogService.ToMeters(acrossFlats.Value, units) * 0.5;
                            circumR = inR / Math.Cos(Math.PI / sides);
                            if (!ed.ContainsKey("start_angle_deg"))
                                baseAng = Math.PI / sides;
                        }
                        else if (ed.ContainsKey("radius") || ed.ContainsKey("diameter"))
                        {
                            double rM = ShopCatalogService.ToMeters(
                                JsonHelper.GetNullableDouble(ed, "radius") ?? (0.5 * JsonHelper.GetDouble(ed, "diameter", 5.0)),
                                units);
                            circumR = inscribed ? (rM / Math.Cos(Math.PI / sides)) : rM;
                            if (inscribed && !ed.ContainsKey("start_angle_deg"))
                                baseAng = Math.PI / sides;
                        }
                        else if (edgePt != null && edgePt.Length >= 2)
                        {
                            Map(edgePt, out double ex, out double ey);
                            double dx = ex - cx;
                            double dy = ey - cy;
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            double dirAng = Math.Atan2(dy, dx);
                            circumR = inscribed ? (dist / Math.Cos(Math.PI / sides)) : dist;
                            baseAng = inscribed ? (dirAng + Math.PI / sides) : dirAng;
                        }
                        else
                        {
                            double inR = ShopCatalogService.ToMeters(5.0, units);
                            circumR = inscribed ? (inR / Math.Cos(Math.PI / sides)) : inR;
                        }

                        double step = 2.0 * Math.PI / sides;
                        double[] vx = new double[sides];
                        double[] vy = new double[sides];
                        for (int i = 0; i < sides; i++)
                        {
                            double a = baseAng + i * step;
                            vx[i] = cx + circumR * Math.Cos(a);
                            vy[i] = cy + circumR * Math.Sin(a);
                        }
                        for (int i = 0; i < sides; i++)
                        {
                            int j = (i + 1) % sides;
                            ISketchSegment seg = sm.CreateLine(vx[i], vy[i], 0, vx[j], vy[j], 0);
                            if (seg != null && construction) seg.ConstructionGeometry = true;
                            createdCount++;
                        }
                    }
                    else if (type == "slot")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        double widthM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "width", 0.25), units);
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        sm.CreateSketchSlot(
                            (int)swSketchSlotCreationType_e.swSketchSlotCreationType_line,
                            (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter,
                            widthM, x1, y1, 0, x2, y2, 0, 0, 0, 0, 1, false);
                        createdCount += 4;
                    }
                    else if (type == "spline")
                    {
                        List<object> pts = JsonHelper.GetList(ed, "points");
                        double[] flat = new double[pts.Count * 3];
                        for (int i = 0; i < pts.Count; i++)
                        {
                            List<object> row = JsonHelper.AsObjectList(pts[i]);
                            double[] raw = new double[]
                            {
                                Convert.ToDouble(row[0], CultureInfo.InvariantCulture),
                                Convert.ToDouble(row[1], CultureInfo.InvariantCulture),
                                row.Count > 2 ? Convert.ToDouble(row[2], CultureInfo.InvariantCulture) : 0.0
                            };
                            Map(raw, out double sx, out double sy);
                            flat[i * 3] = sx;
                            flat[i * 3 + 1] = sy;
                            flat[i * 3 + 2] = 0.0;
                        }
                        sm.CreateSpline2(flat, false);
                        createdCount++;
                    }
                }

                IFeature skFeat = activeSketch as IFeature;
                if (skFeat != null && !string.IsNullOrWhiteSpace(sketchName))
                    skFeat.Name = sketchName;

                string finalName = skFeat != null ? skFeat.Name : "Sketch";
                int status = activeSketch.GetConstrainedStatus();

                if (closeSketch)
                    sm.InsertSketch(true);

                return new Dictionary<string, object>
                {
                    { "sketch_name", finalName },
                    { "entities_created", createdCount },
                    { "constrained_status", status },
                    { "closed", closeSketch }
                };
            }
            finally
            {
                try { sm.AddToDB = prevAddToDb; } catch { }
                try { fm.EnableFeatureTree = prevFeatureTree; } catch { }
            }
        }

        private static McpToolResult HandleCreate3DGuideSketch(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<object> batchSketches = JsonHelper.GetList(args, "sketches");

            if (batchSketches.Count > 0)
            {
                return swMgr.ExecuteInUndoTransaction(doc, "Create 3D Guide Sketches Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                        foreach (object skObj in batchSketches)
                        {
                            Dictionary<string, object> skArgs = skObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(skObj));
                            if (skArgs == null) continue;
                            if (!skArgs.ContainsKey("units")) skArgs["units"] = defaultUnits;
                            created.Add(Create3DGuideSketchInternal(doc, skArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            sketches = created
                        });
                    }
                });
            }

            return swMgr.ExecuteInUndoTransaction(doc, "Create 3D Guide Sketch", true, () =>
                McpToolResult.JsonText(Create3DGuideSketchInternal(doc, args)));
        }

        internal static Dictionary<string, object> Create3DGuideSketchInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            List<object> curves = JsonHelper.GetList(args, "curves");

            doc.ClearSelection2(true);
            ISketchManager sm = doc.SketchManager;
            sm.Insert3DSketch(true);
            ISketch activeSketch = sm.ActiveSketch;
            sm.AddToDB = true;

            int count = 0;
            foreach (object cObj in curves)
            {
                Dictionary<string, object> cd = cObj as Dictionary<string, object>
                    ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(cObj));
                if (cd == null) continue;
                string type = JsonHelper.GetString(cd, "type", "spline").ToLowerInvariant();
                List<object> pts = JsonHelper.GetList(cd, "points");
                if (type == "line" && pts.Count >= 2)
                {
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        double[] a = ToMetersVec(pts[i], units);
                        double[] b = ToMetersVec(pts[i + 1], units);
                        sm.CreateLine(a[0], a[1], a[2], b[0], b[1], b[2]);
                        count++;
                    }
                }
                else if (pts.Count >= 2)
                {
                    double[] flat = new double[pts.Count * 3];
                    for (int i = 0; i < pts.Count; i++)
                    {
                        double[] p = ToMetersVec(pts[i], units);
                        flat[i * 3] = p[0];
                        flat[i * 3 + 1] = p[1];
                        flat[i * 3 + 2] = p[2];
                    }
                    sm.CreateSpline2(flat, false);
                    count++;
                }
            }

            sm.AddToDB = false;
            IFeature skFeat = activeSketch as IFeature;
            if (skFeat != null && !string.IsNullOrWhiteSpace(sketchName))
                skFeat.Name = sketchName;

            string finalName = skFeat != null ? skFeat.Name : "3DSketch";
            sm.Insert3DSketch(true);

            return new Dictionary<string, object>
            {
                { "sketch_name", finalName },
                { "curves_created", count }
            };
        }

        private static double[] ToMetersVec(object ptObj, string units)
        {
            List<object> list = JsonHelper.AsObjectList(ptObj);
            return new[]
            {
                ShopCatalogService.ToMeters(Convert.ToDouble(list[0], CultureInfo.InvariantCulture), units),
                ShopCatalogService.ToMeters(Convert.ToDouble(list[1], CultureInfo.InvariantCulture), units),
                list.Count > 2 ? ShopCatalogService.ToMeters(Convert.ToDouble(list[2], CultureInfo.InvariantCulture), units) : 0.0
            };
        }

        private static McpToolResult HandleCreateRefGeometry(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            List<object> batchItems = JsonHelper.GetList(args, "items");

            if (batchItems.Count > 0)
            {
                return swMgr.ExecuteInUndoTransaction(doc, "Create Reference Geometry Batch", true, () =>
                {
                    using (IDisposable uiScope = SwHelper.SuppressUiUpdates(doc))
                    {
                        List<object> created = new List<object>();
                        string defaultUnits = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
                        foreach (object iObj in batchItems)
                        {
                            Dictionary<string, object> itemArgs = iObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(iObj));
                            if (itemArgs == null) continue;
                            if (!itemArgs.ContainsKey("units")) itemArgs["units"] = defaultUnits;
                            created.Add(CreateRefGeometryInternal(doc, itemArgs));
                        }
                        return McpToolResult.JsonText(new
                        {
                            batch_count = created.Count,
                            items = created
                        });
                    }
                });
            }

            return swMgr.ExecuteInUndoTransaction(doc, "Create Reference Geometry", true, () =>
                McpToolResult.JsonText(CreateRefGeometryInternal(doc, args)));
        }

        internal static Dictionary<string, object> CreateRefGeometryInternal(IModelDoc2 doc, Dictionary<string, object> args)
        {
            string kind = JsonHelper.GetString(args, "kind", "plane").ToLowerInvariant();
            List<string> refs = JsonHelper.GetStringList(args, "references");
            double offset = JsonHelper.GetDouble(args, "offset", 0.0);
            double angleDeg = JsonHelper.GetDouble(args, "angle_deg", 0.0);
            bool flip = JsonHelper.GetBool(args, "flip", false);
            string name = JsonHelper.GetString(args, "name");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));

            if (kind == "coord_sys")
            {
                double[] origin = JsonHelper.GetDoubleArray(args, "origin_xyz");
                double ox = origin != null && origin.Length > 0 ? ShopCatalogService.ToMeters(origin[0], units) : 0.0;
                double oy = origin != null && origin.Length > 1 ? ShopCatalogService.ToMeters(origin[1], units) : 0.0;
                double oz = origin != null && origin.Length > 2 ? ShopCatalogService.ToMeters(origin[2], units) : 0.0;
                IFeature csFeat = doc.FeatureManager.CreateCoordinateSystemUsingNumericalValues(true, ox, oy, oz, false, 0, 0, 0);
                if (csFeat != null && !string.IsNullOrWhiteSpace(name)) csFeat.Name = name;
                return new Dictionary<string, object> { { "kind", kind }, { "feature_name", csFeat?.Name } };
            }

            doc.ClearSelection2(true);
            for (int i = 0; i < refs.Count; i++)
            {
                if (!SwConnectionManager.TrySelectByToken(doc, refs[i], i > 0, 0))
                    throw new InvalidOperationException("Failed to select reference: " + refs[i]);
            }

            if (kind == "axis")
            {
                bool ok = doc.InsertAxis2(true);
                return new Dictionary<string, object> { { "kind", kind }, { "axis_created", ok } };
            }

            double distM = ShopCatalogService.ToMeters(offset, units);
            double angRad = ShopCatalogService.ToRadians(angleDeg);

            int c1 = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
            if (Math.Abs(distM) < 1e-9 && Math.Abs(angRad) < 1e-9)
                c1 = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident;
            else if (Math.Abs(angRad) > 1e-9)
                c1 = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle;

            if (flip) c1 |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;

            IFeature rpFeat = doc.FeatureManager.InsertRefPlane(c1, distM, 0, angRad, 0, 0) as IFeature;
            if (rpFeat == null)
                throw new InvalidOperationException("InsertRefPlane returned null for the supplied references/constraints.");
            if (!string.IsNullOrWhiteSpace(name))
                rpFeat.Name = name;

            return new Dictionary<string, object> { { "kind", kind }, { "feature_name", rpFeat.Name } };
        }
    }
}
