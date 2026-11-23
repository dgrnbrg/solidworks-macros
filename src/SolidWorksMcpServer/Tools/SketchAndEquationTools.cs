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
                Description = "Create one 2D sketch (or a batch via 'sketches' array) on planes/faces using 3D model-space coordinates, design-intent geometric relations, named/equation driving dimensions, and automatic FullyDefineSketch completion so sketches reach FullyConstrained (3).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "plane_or_face", JsonHelper.Prop("string", "Target plane name or face token.", "Front Plane") },
                    { "sketch_name", JsonHelper.Prop("string", "Optional name for the created sketch (e.g. 'Base_Sk').") },
                    { "units", JsonHelper.Prop("string", "Coordinate & dimension value units: mm, in, or m (defaults to active doc units).") },
                    { "model_space_coords", JsonHelper.Prop("boolean", "If true (default), project 3D coordinates through ModelToSketchTransform; if false, use raw sketch 2D coordinates.", true) },
                    { "entities", JsonHelper.ArrayProp("object", "Array of sketch objects (optional 'id' for relations/dimensions, plus optional shorthands 'anchor_origin', 'equal_to', 'concentric_to', 'coincident_to', 'dim_name', 'dim_expression', 'width_name', 'width_expression', 'height_name', 'height_expression', 'length_name', 'length_expression'): {id?, type: 'line'|'centerline', p1:[x,y,z], p2:[x,y,z], construction?:bool}, {id?, type:'polyline', points:[[x,y,z],...], closed?:bool}, {id?, type:'circle', center:[x,y,z], radius|diameter}, {id?, type:'rect_center', center:[x,y,z], width, height}, {id?, type:'rect_corner', p1:[x,y,z], p2:[x,y,z]}, {id?, type:'arc_3pt', p1, p2, mid}, {id?, type:'slot', p1, p2, width, length_type?:'center_center'|'full_length'}, {id?, type:'polygon', center, edge_pt|across_flats|radius|diameter, sides, inscribed?:bool, start_angle_deg?:num}, {id?, type:'spline', points:[[x,y,z],...]}.") },
                    { "relations", JsonHelper.ArrayProp("object", "Design-intent geometric relations referencing entity ids/sub-handles (e.g. 'box1.top', 'hole1.center', 'Origin') or edge:/face: tokens: [{type: 'coincident'|'concentric'|'coradial'|'colinear'|'parallel'|'perpendicular'|'tangent'|'equal'|'symmetric'|'midpoint'|'horizontal'|'vertical'|'fix', entities: [id1, id2], centerline?: id, intent?: string}].") },
                    { "dimensions", JsonHelper.ArrayProp("object", "Named or equation-derived driving dimensions: [{type?: 'linear'|'horizontal'|'vertical'|'diameter'|'radius'|'angle', entity?: id, from?: id, to?: id, value?: number, expression?: string, name?: string, intent?: string}].") },
                    { "constraints", JsonHelper.ArrayProp("string", "Optional legacy SOLIDWORKS constraint names (e.g. sgFIXED, sgHORIZ, sgVERT, sgTANGENT) or relation objects.") },
                    { "auto_fully_define", JsonHelper.Prop("boolean", "Automatically run SOLIDWORKS FullyDefineSketch after applying explicit relations and dimensions so the sketch reaches FullyConstrained (3).", true) },
                    { "close_sketch", JsonHelper.Prop("boolean", "Exit sketch after creation.", true) },
                    { "sketches", JsonHelper.ArrayProp("object", "Batch mode: array of sketch specs [{plane_or_face, sketch_name, entities, relations, dimensions, auto_fully_define, units, model_space_coords, close_sketch}] created inside one Undo transaction.") },
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
            SwHelper.EnsureAppBound(app, doc);
            SwHelper.SuppressDimensionInputPopup(app);

            string planeOrFace = JsonHelper.GetString(args, "plane_or_face", "Front Plane");
            string sketchName = JsonHelper.GetString(args, "sketch_name");
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            bool useModelCoords = JsonHelper.GetBool(args, "model_space_coords", true);
            bool closeSketch = JsonHelper.GetBool(args, "close_sketch", true);
            bool autoFullyDefine = JsonHelper.GetBool(args, "auto_fully_define", true);
            List<object> entities = JsonHelper.GetList(args, "entities");
            List<object> relationsList = new List<object>(JsonHelper.GetList(args, "relations"));
            List<object> constraintsList = new List<object>(JsonHelper.GetList(args, "constraints"));
            List<object> dimensionsList = new List<object>(JsonHelper.GetList(args, "dimensions"));

            ISketchManager sm = doc.SketchManager;
            bool prevAddToDb = sm.AddToDB;
            try
            {
                doc.ClearSelection2(true);
                if (!SwConnectionManager.TrySelectByToken(doc, planeOrFace, false, 0))
                    throw new InvalidOperationException("Could not select plane or face: " + planeOrFace);

                sm.InsertSketchSafe(true);
                ISketch activeSketch = sm.ActiveSketch;
                if (activeSketch == null)
                    throw new InvalidOperationException("Failed to open 2D sketch on: " + planeOrFace);

                IFeature skFeat = activeSketch as IFeature;
                if (skFeat != null && !string.IsNullOrWhiteSpace(sketchName))
                    skFeat.Name = sketchName.Trim();

                double[] xformArr = null;
                if (useModelCoords)
                {
                    IMathTransform xform = activeSketch.ModelToSketchTransform;
                    xformArr = xform != null ? xform.ArrayData as double[] : null;
                }

                void Map(double[] pt, out double sx, out double sy)
                {
                    double xM = ShopCatalogService.ToMeters(pt[0], units);
                    double yM = ShopCatalogService.ToMeters(pt[1], units);
                    double zM = pt.Length > 2 ? ShopCatalogService.ToMeters(pt[2], units) : 0.0;
                    if (useModelCoords && xformArr != null)
                        SwConnectionManager.MapPointToSketch2D(xformArr, xM, yM, zM, out sx, out sy);
                    else
                    {
                        sx = xM;
                        sy = yM;
                    }
                }

                Dictionary<string, object> handleMap = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    { "Origin", "Origin" }
                };
                int createdCount = 0;

                for (int entIdx = 0; entIdx < entities.Count; entIdx++)
                {
                    object eObj = entities[entIdx];
                    Dictionary<string, object> ed = eObj as Dictionary<string, object>
                        ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(eObj));
                    if (ed == null) continue;

                    string entId = JsonHelper.GetString(ed, "id");
                    if (string.IsNullOrWhiteSpace(entId)) entId = "e" + entIdx.ToString(CultureInfo.InvariantCulture);
                    else entId = entId.Trim();

                    string type = JsonHelper.GetString(ed, "type", "line").ToLowerInvariant();
                    bool construction = JsonHelper.GetBool(ed, "construction", false);
                    bool anchorOrigin = JsonHelper.GetBool(ed, "anchor_origin", true);

                    if (type == "line" || type == "centerline")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        ISketchSegment seg = type == "centerline"
                            ? sm.CreateCenterLine(x1, y1, 0, x2, y2, 0)
                            : sm.CreateLine(x1, y1, 0, x2, y2, 0);
                        if (seg != null)
                        {
                            if (construction) seg.ConstructionGeometry = true;
                            handleMap[entId] = seg;
                            if (seg is ISketchLine skLine)
                            {
                                object sp = skLine.GetStartPoint2();
                                object ep = skLine.GetEndPoint2();
                                if (sp != null) handleMap[entId + ".start"] = sp;
                                if (ep != null) handleMap[entId + ".end"] = ep;
                            }
                            if (Math.Abs(y2 - y1) < 1e-6 && Math.Abs(x2 - x1) > 1e-6)
                                QueueRelation(relationsList, "horizontal", entId);
                            else if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) > 1e-6)
                                QueueRelation(relationsList, "vertical", entId);
                        }
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
                        if (seg != null)
                        {
                            if (construction) seg.ConstructionGeometry = true;
                            handleMap[entId] = seg;
                            if (seg is ISketchArc skCircle)
                            {
                                object cp = skCircle.GetCenterPoint2();
                                if (cp != null)
                                {
                                    handleMap[entId + ".center"] = cp;
                                    if (anchorOrigin && Math.Abs(cx) < 1e-6 && Math.Abs(cy) < 1e-6)
                                        QueueRelation(relationsList, "coincident", entId + ".center", "Origin");
                                }
                            }
                        }
                        createdCount++;
                    }
                    else if (type == "rect_center")
                    {
                        double[] center = JsonHelper.GetDoubleArray(ed, "center");
                        double wM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "width", 1.0), units);
                        double hM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "height", 1.0), units);
                        Map(center, out double cx, out double cy);
                        int preSegCount = GetSketchSegmentCount(activeSketch);
                        object rawRect = sm.CreateCenterRectangleSafe(cx, cy, 0, cx + 0.5 * wM, cy + 0.5 * hM, 0);
                        if (anchorOrigin && Math.Abs(cx) < 1e-6 && Math.Abs(cy) < 1e-6)
                            sm.CreatePoint(cx, cy, 0.0);
                        RegisterRectangleHandles(activeSketch, rawRect, preSegCount, entId, true, cx, cy, anchorOrigin, handleMap, relationsList, construction);
                        createdCount += 4;
                    }
                    else if (type == "rect_corner")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        int preSegCount = GetSketchSegmentCount(activeSketch);
                        object rawRect = sm.CreateCornerRectangleSafe(x1, y1, 0, x2, y2, 0);
                        RegisterRectangleHandles(activeSketch, rawRect, preSegCount, entId, false, 0.5 * (x1 + x2), 0.5 * (y1 + y2), anchorOrigin, handleMap, relationsList, construction);
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
                        ISketchSegment seg = sm.Create3PointArc(x1, y1, 0, x2, y2, 0, xm, ym, 0);
                        if (seg != null)
                        {
                            if (construction) seg.ConstructionGeometry = true;
                            handleMap[entId] = seg;
                            if (seg is ISketchArc skArc)
                            {
                                object cp = skArc.GetCenterPoint2();
                                object sp = skArc.GetStartPoint2();
                                object ep = skArc.GetEndPoint2();
                                if (cp != null) handleMap[entId + ".center"] = cp;
                                if (sp != null) handleMap[entId + ".start"] = sp;
                                if (ep != null) handleMap[entId + ".end"] = ep;
                            }
                        }
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
                        ISketchSegment prevSide = null;
                        ISketchSegment firstSide = null;
                        for (int i = 0; i < sides; i++)
                        {
                            int j = (i + 1) % sides;
                            ISketchSegment seg = sm.CreateLine(vx[i], vy[i], 0, vx[j], vy[j], 0);
                            if (seg != null)
                            {
                                if (construction) seg.ConstructionGeometry = true;
                                handleMap[entId + ".side" + i] = seg;
                                if (i == 0)
                                {
                                    handleMap[entId] = seg;
                                    firstSide = seg;
                                }
                                if (seg is ISketchLine sl)
                                {
                                    object vp = sl.GetStartPoint2();
                                    if (vp != null) handleMap[entId + ".v" + i] = vp;
                                }
                                if (prevSide != null)
                                    QueueRelation(relationsList, "equal", entId + ".side" + (i - 1), entId + ".side" + i);
                                prevSide = seg;
                            }
                            createdCount++;
                        }
                        if (sides >= 3 && firstSide != null && prevSide != null && Math.Abs(vy[1] - vy[0]) < 1e-6)
                            QueueRelation(relationsList, "horizontal", entId + ".side0");
                        if (anchorOrigin && Math.Abs(cx) < 1e-6 && Math.Abs(cy) < 1e-6)
                        {
                            ISketchPoint ctrPt = sm.CreatePoint(cx, cy, 0.0);
                            if (ctrPt != null)
                            {
                                handleMap[entId + ".center"] = ctrPt;
                                QueueRelation(relationsList, "coincident", entId + ".center", "Origin");
                            }
                        }
                    }
                    else if (type == "slot")
                    {
                        double[] p1 = JsonHelper.GetDoubleArray(ed, "p1");
                        double[] p2 = JsonHelper.GetDoubleArray(ed, "p2");
                        double widthM = ShopCatalogService.ToMeters(JsonHelper.GetDouble(ed, "width", 0.25), units);
                        string lenType = JsonHelper.GetString(ed, "length_type", "center_center").ToLowerInvariant();
                        bool fullLength = lenType == "full" || lenType == "full_length" || lenType == "overall";
                        Map(p1, out double x1, out double y1);
                        Map(p2, out double x2, out double y2);
                        int preSegCount = GetSketchSegmentCount(activeSketch);
                        int slotSegs = SwHelper.CreateSlot2D(sm, x1, y1, x2, y2, widthM, fullLength, construction);
                        createdCount += slotSegs;
                        RegisterSlotHandles(sm, activeSketch, preSegCount, entId, x1, y1, x2, y2, handleMap, relationsList);
                    }
                    else if (type == "polyline" || type == "loop" || type == "polygon_pts")
                    {
                        List<object> pts = JsonHelper.GetList(ed, "points");
                        bool closed = JsonHelper.GetBool(ed, "closed", true);
                        int minPts = closed ? 3 : 2;
                        if (pts.Count >= minPts)
                        {
                            double[] sxArr = new double[pts.Count];
                            double[] syArr = new double[pts.Count];
                            for (int i = 0; i < pts.Count; i++)
                            {
                                List<object> row = JsonHelper.AsObjectList(pts[i]);
                                double[] raw = new double[]
                                {
                                    Convert.ToDouble(row[0], CultureInfo.InvariantCulture),
                                    Convert.ToDouble(row[1], CultureInfo.InvariantCulture),
                                    row.Count > 2 ? Convert.ToDouble(row[2], CultureInfo.InvariantCulture) : 0.0
                                };
                                Map(raw, out sxArr[i], out syArr[i]);
                            }

                            int segCount = closed ? pts.Count : (pts.Count - 1);
                            for (int i = 0; i < segCount; i++)
                            {
                                int j = (i + 1) % pts.Count;
                                double x1 = sxArr[i], y1 = syArr[i];
                                double x2 = sxArr[j], y2 = syArr[j];
                                if (Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) < 1e-8)
                                    continue;

                                ISketchSegment seg = construction
                                    ? sm.CreateCenterLine(x1, y1, 0.0, x2, y2, 0.0)
                                    : sm.CreateLine(x1, y1, 0.0, x2, y2, 0.0);
                                if (seg != null)
                                {
                                    if (construction) seg.ConstructionGeometry = true;
                                    handleMap[entId + ".seg" + i] = seg;
                                    if (i == 0) handleMap[entId] = seg;
                                    if (seg is ISketchLine skLine)
                                    {
                                        object sp = skLine.GetStartPoint2();
                                        object ep = skLine.GetEndPoint2();
                                        if (sp != null) handleMap[entId + ".v" + i] = sp;
                                        if (ep != null && i == segCount - 1 && !closed)
                                            handleMap[entId + ".v" + j] = ep;
                                    }
                                    if (Math.Abs(y2 - y1) < 1e-6 && Math.Abs(x2 - x1) > 1e-6)
                                        QueueRelation(relationsList, "horizontal", entId + ".seg" + i);
                                    else if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) > 1e-6)
                                        QueueRelation(relationsList, "vertical", entId + ".seg" + i);
                                }
                                createdCount++;
                            }
                        }
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
                        ISketchSegment spSeg = sm.CreateSpline2(flat, false);
                        if (spSeg != null)
                        {
                            if (construction) spSeg.ConstructionGeometry = true;
                            handleMap[entId] = spSeg;
                        }
                        createdCount++;
                    }

                    AppendInlineEntityShorthands(ed, entId, type, relationsList, dimensionsList);
                }

                sm.AddToDB = false;
                SwHelper.SuppressDimensionInputPopup(app);

                ApplySketchRelationsAndDimensions(
                    doc,
                    activeSketch,
                    handleMap,
                    relationsList,
                    constraintsList,
                    dimensionsList,
                    units,
                    out int relationsApplied,
                    out List<string> dimensionsCreated);

                bool autoDefined = false;
                int status = activeSketch.GetConstrainedStatus();
                if (autoFullyDefine && closeSketch && status != (int)swConstrainedStatus_e.swFullyConstrained)
                {
                    status = SwHelper.FullyDefineActiveSketch(doc);
                    autoDefined = true;
                }

                string finalName = skFeat != null ? skFeat.Name : "Sketch";

                if (closeSketch)
                    sm.InsertSketchSafe(true);

                return new Dictionary<string, object>
                {
                    { "sketch_name", finalName },
                    { "entities_created", createdCount },
                    { "constrained_status", status },
                    { "constrained_status_label", SwHelper.GetConstrainedStatusLabel(status) },
                    { "is_fully_constrained", status == (int)swConstrainedStatus_e.swFullyConstrained },
                    { "relations_applied", relationsApplied },
                    { "dimensions_created", dimensionsCreated },
                    { "auto_fully_defined", autoDefined },
                    { "closed", closeSketch },
                    { "sketch_closed", closeSketch }
                };
            }
            finally
            {
                try { sm.AddToDB = prevAddToDb; } catch { }
            }
        }

        internal static void ApplySketchRelationsAndDimensions(
            IModelDoc2 doc,
            ISketch activeSketch,
            Dictionary<string, object> handleMap,
            IList<object> relationsList,
            IList<object> constraintsList,
            IList<object> dimensionsList,
            string units,
            out int relationsApplied,
            out List<string> dimensionsCreated)
        {
            relationsApplied = 0;
            dimensionsCreated = new List<string>();
            if (doc == null || activeSketch == null) return;

            ISketchManager sm = doc.SketchManager;
            bool prevDb = false;
            try { if (sm != null) prevDb = sm.AddToDB; } catch { }
            try
            {
                if (sm != null && prevDb)
                {
                    try { sm.AddToDB = false; } catch { }
                }

                if (constraintsList != null)
                {
                    foreach (object cObj in constraintsList)
                    {
                        if (cObj is string cStr && !string.IsNullOrWhiteSpace(cStr))
                        {
                            string relConst = SwHelper.NormalizeSketchRelationName(cStr);
                            if (!string.IsNullOrEmpty(relConst))
                            {
                                try { doc.SketchAddConstraints(relConst); relationsApplied++; } catch { }
                            }
                        }
                        else if (cObj != null)
                        {
                            Dictionary<string, object> cd = cObj as Dictionary<string, object>
                                ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(cObj));
                            if (cd != null && ApplySingleRelationDict(doc, handleMap, cd))
                                relationsApplied++;
                        }
                    }
                }

                if (relationsList != null)
                {
                    foreach (object rObj in relationsList)
                    {
                        Dictionary<string, object> rd = rObj as Dictionary<string, object>
                            ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(rObj));
                        if (rd != null && ApplySingleRelationDict(doc, handleMap, rd))
                            relationsApplied++;
                    }
                }

                if (dimensionsList != null)
                {
                    double textOffsetStep = 0.0;
                    foreach (object dObj in dimensionsList)
                    {
                        Dictionary<string, object> dd = dObj as Dictionary<string, object>
                            ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(dObj));
                        if (dd == null) continue;

                        string dimType = JsonHelper.GetString(dd, "type", "linear").ToLowerInvariant();
                        string entRef = JsonHelper.GetString(dd, "entity");
                        string fromRef = JsonHelper.GetString(dd, "from");
                        string toRef = JsonHelper.GetString(dd, "to");

                        object objA = ResolveHandle(handleMap, !string.IsNullOrWhiteSpace(fromRef) ? fromRef : entRef);
                        object objB = !string.IsNullOrWhiteSpace(toRef) ? ResolveHandle(handleMap, toRef) : null;
                        if (objA == null) continue;

                        double? valConv = null;
                        double? rawVal = JsonHelper.GetNullableDouble(dd, "value");
                        if (rawVal.HasValue)
                        {
                            valConv = (dimType == "angle")
                                ? ShopCatalogService.ToRadians(rawVal.Value)
                                : ShopCatalogService.ToMeters(rawVal.Value, units);
                        }

                        string dimName = JsonHelper.GetString(dd, "name");
                        string dimExpr = JsonHelper.GetString(dd, "expression");
                        string createdRef = SwHelper.AddSketchDimension(
                            doc,
                            objA,
                            objB,
                            dimType,
                            valConv,
                            dimName,
                            dimExpr,
                            0.015 + textOffsetStep,
                            0.015 + textOffsetStep);

                        if (!string.IsNullOrEmpty(createdRef))
                        {
                            dimensionsCreated.Add(createdRef);
                            textOffsetStep += 0.008;
                        }
                    }
                }
            }
            finally
            {
                if (sm != null && prevDb)
                {
                    try { sm.AddToDB = true; } catch { }
                }
            }
        }

        private static bool ApplySingleRelationDict(IModelDoc2 doc, Dictionary<string, object> handleMap, Dictionary<string, object> rd)
        {
            string rawType = JsonHelper.GetString(rd, "type", JsonHelper.GetString(rd, "relation", ""));
            string relConst = SwHelper.NormalizeSketchRelationName(rawType);
            if (string.IsNullOrEmpty(relConst)) return false;

            List<object> ents = JsonHelper.GetList(rd, "entities");
            string centerlineRef = JsonHelper.GetString(rd, "centerline");

            doc.ClearSelection2(true);
            int selCount = 0;

            if (string.Equals(relConst, "sgSYMMETRIC", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(centerlineRef))
            {
                object clObj = ResolveHandle(handleMap, centerlineRef);
                if (clObj != null && SwHelper.SelectSketchObject(doc, clObj, false, 0))
                    selCount++;
            }

            foreach (object item in ents)
            {
                if (item == null) continue;
                object resolved = ResolveHandle(handleMap, item.ToString());
                if (resolved != null && SwHelper.SelectSketchObject(doc, resolved, selCount > 0, 0))
                    selCount++;
            }

            if (selCount == 0)
            {
                doc.ClearSelection2(true);
                return false;
            }

            try
            {
                doc.SketchAddConstraints(relConst);
                doc.ClearSelection2(true);
                return true;
            }
            catch
            {
                doc.ClearSelection2(true);
                return false;
            }
        }

        private static object ResolveHandle(Dictionary<string, object> handleMap, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            string clean = key.Trim();
            if (handleMap != null && handleMap.TryGetValue(clean, out object mapped))
                return mapped;
            return clean;
        }

        private static void QueueRelation(List<object> relationsList, string relType, params string[] entityIds)
        {
            List<object> list = new List<object>();
            if (entityIds != null)
            {
                foreach (string s in entityIds)
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            if (list.Count == 0) return;
            relationsList.Add(new Dictionary<string, object>
            {
                { "type", relType },
                { "entities", list }
            });
        }

        private static void AppendInlineEntityShorthands(
            Dictionary<string, object> ed,
            string entId,
            string type,
            List<object> relationsList,
            List<object> dimensionsList)
        {
            string eqTo = JsonHelper.GetString(ed, "equal_to");
            if (!string.IsNullOrWhiteSpace(eqTo))
                QueueRelation(relationsList, "equal", entId, eqTo);

            string concTo = JsonHelper.GetString(ed, "concentric_to");
            if (!string.IsNullOrWhiteSpace(concTo))
                QueueRelation(relationsList, "concentric", entId, concTo);

            string coinTo = JsonHelper.GetString(ed, "coincident_to");
            if (!string.IsNullOrWhiteSpace(coinTo))
            {
                string src = (type == "circle" || type == "rect_center" || type == "polygon") ? (entId + ".center") : entId;
                QueueRelation(relationsList, "coincident", src, coinTo);
            }

            string symAbout = JsonHelper.GetString(ed, "symmetric_about");
            string symOther = JsonHelper.GetString(ed, "symmetric_with");
            if (!string.IsNullOrWhiteSpace(symAbout) && !string.IsNullOrWhiteSpace(symOther))
            {
                relationsList.Add(new Dictionary<string, object>
                {
                    { "type", "symmetric" },
                    { "centerline", symAbout },
                    { "entities", new List<object> { entId, symOther } }
                });
            }

            string dimName = JsonHelper.GetString(ed, "dim_name");
            string dimExpr = JsonHelper.GetString(ed, "dim_expression");
            if (!string.IsNullOrWhiteSpace(dimName) || !string.IsNullOrWhiteSpace(dimExpr))
            {
                string dType = type == "circle" ? "diameter" : (type == "arc_3pt" ? "radius" : "linear");
                dimensionsList.Add(new Dictionary<string, object>
                {
                    { "type", dType },
                    { "entity", entId },
                    { "name", dimName },
                    { "expression", dimExpr }
                });
            }

            string wName = JsonHelper.GetString(ed, "width_name");
            string wExpr = JsonHelper.GetString(ed, "width_expression");
            if (!string.IsNullOrWhiteSpace(wName) || !string.IsNullOrWhiteSpace(wExpr))
            {
                dimensionsList.Add(new Dictionary<string, object>
                {
                    { "type", "horizontal" },
                    { "entity", (type == "rect_center" || type == "rect_corner") ? (entId + ".top") : entId },
                    { "name", wName },
                    { "expression", wExpr }
                });
            }

            string hName = JsonHelper.GetString(ed, "height_name");
            string hExpr = JsonHelper.GetString(ed, "height_expression");
            if (!string.IsNullOrWhiteSpace(hName) || !string.IsNullOrWhiteSpace(hExpr))
            {
                dimensionsList.Add(new Dictionary<string, object>
                {
                    { "type", "vertical" },
                    { "entity", (type == "rect_center" || type == "rect_corner") ? (entId + ".left") : entId },
                    { "name", hName },
                    { "expression", hExpr }
                });
            }

            string lName = JsonHelper.GetString(ed, "length_name");
            string lExpr = JsonHelper.GetString(ed, "length_expression");
            if (!string.IsNullOrWhiteSpace(lName) || !string.IsNullOrWhiteSpace(lExpr))
            {
                dimensionsList.Add(new Dictionary<string, object>
                {
                    { "type", "linear" },
                    { "entity", type == "slot" ? (entId + ".axis") : entId },
                    { "name", lName },
                    { "expression", lExpr }
                });
            }
        }

        private static int GetSketchSegmentCount(ISketch sketch)
        {
            if (sketch == null) return 0;
            try
            {
                object[] segs = sketch.GetSketchSegments() as object[];
                return segs != null ? segs.Length : 0;
            }
            catch { return 0; }
        }

        private static void RegisterRectangleHandles(
            ISketch sketch,
            object rawRect,
            int preSegCount,
            string entId,
            bool isCenterRect,
            double cx,
            double cy,
            bool anchorOrigin,
            Dictionary<string, object> handleMap,
            List<object> relationsList,
            bool construction)
        {
            List<ISketchLine> solidLines = new List<ISketchLine>();
            List<ISketchSegment> allNew = new List<ISketchSegment>();

            if (rawRect is object[] rectArr)
            {
                foreach (object ro in rectArr)
                {
                    if (ro is ISketchSegment rs) allNew.Add(rs);
                }
            }
            if (allNew.Count == 0 && sketch != null)
            {
                object[] allSegs = sketch.GetSketchSegments() as object[];
                if (allSegs != null)
                {
                    for (int i = preSegCount; i < allSegs.Length; i++)
                    {
                        if (allSegs[i] is ISketchSegment rs) allNew.Add(rs);
                    }
                }
            }

            foreach (ISketchSegment seg in allNew)
            {
                if (construction) seg.ConstructionGeometry = true;
                if (!seg.ConstructionGeometry && seg is ISketchLine line)
                    solidLines.Add(line);
                else if (construction && seg is ISketchLine cLine && solidLines.Count < 4)
                    solidLines.Add(cLine);
            }

            if (solidLines.Count == 0) return;
            handleMap[entId] = solidLines[0];

            ISketchLine top = null, bottom = null, left = null, right = null;
            double bestTopY = double.MinValue, bestBotY = double.MaxValue;
            double bestLeftX = double.MaxValue, bestRightX = double.MinValue;

            foreach (ISketchLine line in solidLines)
            {
                ISketchPoint p1 = line.GetStartPoint2() as ISketchPoint;
                ISketchPoint p2 = line.GetEndPoint2() as ISketchPoint;
                if (p1 == null || p2 == null) continue;
                double dx = Math.Abs(p2.X - p1.X);
                double dy = Math.Abs(p2.Y - p1.Y);
                double midX = 0.5 * (p1.X + p2.X);
                double midY = 0.5 * (p1.Y + p2.Y);

                if (dx >= dy)
                {
                    if (midY > bestTopY) { bestTopY = midY; top = line; }
                    if (midY < bestBotY) { bestBotY = midY; bottom = line; }
                }
                else
                {
                    if (midX < bestLeftX) { bestLeftX = midX; left = line; }
                    if (midX > bestRightX) { bestRightX = midX; right = line; }
                }
            }

            if (top != null) { handleMap[entId + ".top"] = top; QueueRelation(relationsList, "horizontal", entId + ".top"); }
            if (bottom != null && bottom != top) { handleMap[entId + ".bottom"] = bottom; QueueRelation(relationsList, "horizontal", entId + ".bottom"); }
            if (left != null) { handleMap[entId + ".left"] = left; QueueRelation(relationsList, "vertical", entId + ".left"); }
            if (right != null && right != left) { handleMap[entId + ".right"] = right; QueueRelation(relationsList, "vertical", entId + ".right"); }

            // Register corner points (.tl, .tr, .br, .bl) and center point (.center)
            object[] skPts = sketch != null ? sketch.GetSketchPoints2() as object[] : null;
            if (skPts != null)
            {
                ISketchPoint centerPt = null;
                double minCtrDist = 1e-5;
                foreach (object pObj in skPts)
                {
                    if (!(pObj is ISketchPoint pt)) continue;
                    double distCtr = Math.Sqrt((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy));
                    if (isCenterRect && distCtr < minCtrDist)
                    {
                        minCtrDist = distCtr;
                        centerPt = pt;
                    }
                    if (pt.X <= cx - 1e-6 && pt.Y >= cy + 1e-6) handleMap[entId + ".tl"] = pt;
                    else if (pt.X >= cx + 1e-6 && pt.Y >= cy + 1e-6) handleMap[entId + ".tr"] = pt;
                    else if (pt.X >= cx + 1e-6 && pt.Y <= cy - 1e-6) handleMap[entId + ".br"] = pt;
                    else if (pt.X <= cx - 1e-6 && pt.Y <= cy - 1e-6) handleMap[entId + ".bl"] = pt;
                }
                if (centerPt != null)
                {
                    handleMap[entId + ".center"] = centerPt;
                    if (isCenterRect && anchorOrigin && Math.Abs(cx) < 1e-6 && Math.Abs(cy) < 1e-6)
                        QueueRelation(relationsList, "coincident", entId + ".center", "Origin");
                }
            }
        }

        private static void RegisterSlotHandles(
            ISketchManager sm,
            ISketch sketch,
            int preSegCount,
            string entId,
            double x1,
            double y1,
            double x2,
            double y2,
            Dictionary<string, object> handleMap,
            List<object> relationsList)
        {
            if (sketch == null) return;
            object[] allSegs = sketch.GetSketchSegments() as object[];
            if (allSegs == null) return;

            List<ISketchLine> lines = new List<ISketchLine>();
            List<ISketchArc> arcs = new List<ISketchArc>();
            for (int i = preSegCount; i < allSegs.Length; i++)
            {
                if (allSegs[i] is ISketchArc arc)
                {
                    arcs.Add(arc);
                    if (arcs.Count == 1) handleMap[entId] = arc;
                }
                else if (allSegs[i] is ISketchLine line)
                {
                    lines.Add(line);
                }
            }

            if (arcs.Count == 1 && lines.Count == 0)
            {
                object cp = arcs[0].GetCenterPoint2();
                if (cp != null) handleMap[entId + ".c1"] = cp;
                return;
            }

            for (int i = 0; i < lines.Count; i++)
                handleMap[entId + ".line" + (i + 1)] = lines[i];
            for (int i = 0; i < arcs.Count; i++)
            {
                handleMap[entId + ".arc" + (i + 1)] = arcs[i];
                object cp = arcs[i].GetCenterPoint2();
                if (cp != null) handleMap[entId + ".c" + (i + 1)] = cp;
            }

            if (lines.Count >= 2 && arcs.Count >= 2)
            {
                QueueRelation(relationsList, "equal", entId + ".line1", entId + ".line2");
                QueueRelation(relationsList, "equal", entId + ".arc1", entId + ".arc2");
                QueueRelation(relationsList, "tangent", entId + ".line1", entId + ".arc1");
                QueueRelation(relationsList, "tangent", entId + ".line2", entId + ".arc2");

                double c1x = 0, c1y = 0, c2x = 0, c2y = 0;
                if (arcs[0].GetCenterPoint2() is ISketchPoint p1 && arcs[1].GetCenterPoint2() is ISketchPoint p2)
                {
                    c1x = p1.X; c1y = p1.Y;
                    c2x = p2.X; c2y = p2.Y;
                }
                else
                {
                    c1x = x1; c1y = y1; c2x = x2; c2y = y2;
                }
                if (Math.Sqrt((c2x - c1x) * (c2x - c1x) + (c2y - c1y) * (c2y - c1y)) > 1e-6)
                {
                    ISketchSegment axisSeg = sm.CreateCenterLine(c1x, c1y, 0.0, c2x, c2y, 0.0);
                    if (axisSeg != null)
                    {
                        handleMap[entId + ".axis"] = axisSeg;
                        if (Math.Abs(c2y - c1y) < 1e-6)
                            QueueRelation(relationsList, "horizontal", entId + ".axis");
                        else if (Math.Abs(c2x - c1x) < 1e-6)
                            QueueRelation(relationsList, "vertical", entId + ".axis");
                    }
                }
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
