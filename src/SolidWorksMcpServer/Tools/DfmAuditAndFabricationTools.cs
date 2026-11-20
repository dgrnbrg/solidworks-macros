using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class DfmAuditAndFabricationTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_audit_dfm",
                Description = "Audit active SOLIDWORKS geometry against shop_profile.json & sendcutsend_catalog.json for 3D printing (Prusa MK3.5S+ MMGS: envelope + unsupported >45deg overhangs + bed chamfer vs fillet + <1.2mm thin-web raycast), CNC carving (Onefinity 8-bit library), SendCutSend sheet metal, or woodworking (IPS).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "process", JsonHelper.Prop("string", "Manufacturing process to audit.", "3d_printing", new[] { "3d_printing", "cnc_carving", "sheet_metal_sendcutsend", "woodworking" }) },
                    { "material", JsonHelper.Prop("string", "Optional material name/key for tighter checks.") },
                    { "thickness_in", JsonHelper.Prop("number", "Optional sheet thickness in inches for SendCutSend checks.") },
                    { "build_direction", JsonHelper.ArrayProp("number", "Print bed upward normal vector for 3d_printing overhang & chamfer audit (default [0, 0, 1]).") },
                    { "min_web_thickness_mm", JsonHelper.Prop("number", "Minimum web/wall thickness threshold in mm for 3d_printing B-Rep raycast check (default 1.2).", 1.2) },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }, "process"),
                Handler = args => HandleAuditDfm(swMgr, catalog, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_generate_cutlist_and_templates",
                Description = "Generate a kerf-aware woodworking/sheet cutlist, compute router guide-bushing template offsets, and plan clamp-staged glue-ups (<=4 parallel, <=6 pipe clamps).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "kerf_in", JsonHelper.Prop("number", "Table saw blade kerf in inches (0.125 full or 0.094 thin).", 0.125) },
                    { "router_bushing_od_in", JsonHelper.Prop("number", "Optional router guide bushing OD in inches.", 0.375) },
                    { "router_bit_dia_in", JsonHelper.Prop("number", "Optional router bit diameter in inches.", 0.250) },
                    { "output_dir", JsonHelper.Prop("string", "Optional output directory to write cutlist_and_plan.md.") },
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") }
                }),
                Handler = args => HandleCutlistAndTemplates(swMgr, args)
            });
        }

        private static McpToolResult HandleAuditDfm(SwConnectionManager swMgr, ShopCatalogService catalog, Dictionary<string, object> args)
        {
            string process = JsonHelper.GetString(args, "process", "3d_printing").ToLowerInvariant();
            string material = JsonHelper.GetString(args, "material", string.Empty);
            double? thickIn = JsonHelper.GetNullableDouble(args, "thickness_in");

            // Allow offline catalog-only audit if SOLIDWORKS isn't running and no active doc is needed for SendCutSend check
            IModelDoc2 doc = null;
            string targetDoc = JsonHelper.GetString(args, "target_doc");
            if (swMgr.TryAttachOnly(out ISldWorks app))
            {
                doc = !string.IsNullOrWhiteSpace(targetDoc)
                    ? swMgr.RequireModel(targetDoc)
                    : app.ActiveDoc as IModelDoc2;
            }
            else if (!string.IsNullOrWhiteSpace(targetDoc))
            {
                doc = swMgr.RequireModel(targetDoc);
            }

            List<object> findings = new List<object>();
            string docUnits = doc != null ? SwConnectionManager.GetDocumentUnits(doc) : "unknown";

            if (process == "3d_printing")
            {
                if (doc != null && docUnits != "mm")
                    findings.Add(new { severity = "WARN", rule = "unit_system", message = "3D-printed parts should use MMGS (currently " + docUnits + ")." });

                if (doc is IPartDoc part)
                {
                    double[] bb = part.GetPartBox(true) as double[];
                    if (bb != null && bb.Length >= 6)
                    {
                        double dx = (bb[3] - bb[0]) * 1000.0;
                        double dy = (bb[4] - bb[1]) * 1000.0;
                        double dz = (bb[5] - bb[2]) * 1000.0;
                        bool fits = ((dx <= 250 && dy <= 210) || (dy <= 250 && dx <= 210)) && dz <= 210;
                        findings.Add(new
                        {
                            severity = fits ? "PASS" : "FAIL",
                            rule = "prusa_mk35s_build_volume",
                            message = string.Format(CultureInfo.InvariantCulture, "Part span {0:F1} x {1:F1} x {2:F1} mm vs Prusa MK3.5S+ (250 x 210 x 210 mm).", dx, dy, dz)
                        });
                    }

                    double[] rawBuildDir = JsonHelper.GetDoubleArray(args, "build_direction");
                    double[] uBuild = (rawBuildDir != null && rawBuildDir.Length >= 3)
                        ? AssemblyGeometryHelper.Normalize(rawBuildDir)
                        : new[] { 0.0, 0.0, 1.0 };
                    double minWebMm = Math.Max(0.4, JsonHelper.GetDouble(args, "min_web_thickness_mm", 1.2));

                    Audit3DPrintBRep(doc, part, uBuild, minWebMm, findings);

                    findings.Add(new
                    {
                        severity = "INFO",
                        rule = "filament_and_nozzle",
                        message = "Default filament: MatterHackers Tough PLA (Basic) on fixed 0.4mm nozzle (walls in multiples of 0.4mm: 1.2, 1.6, 2.0mm)."
                    });
                }
            }
            else if (process == "cnc_carving")
            {
                if (doc is IPartDoc part)
                {
                    double[] bb = part.GetPartBox(true) as double[];
                    if (bb != null && bb.Length >= 6)
                    {
                        double dx = (bb[3] - bb[0]) / 0.0254;
                        double dy = (bb[4] - bb[1]) / 0.0254;
                        double dz = (bb[5] - bb[2]) / 0.0254;
                        bool fits = ((dx <= 48.0 && dy <= 32.0) || (dy <= 48.0 && dx <= 32.0)) && dz <= 6.5;
                        findings.Add(new
                        {
                            severity = fits ? "PASS" : "FAIL",
                            rule = "onefinity_journeyman_envelope",
                            message = string.Format(CultureInfo.InvariantCulture, "Part span {0:F2}\" x {1:F2}\" x {2:F2}\" vs Onefinity Elite Journeyman Gen 2 (48\" x 32\" x 6.5\").", dx, dy, dz)
                        });
                    }

                    // Scan small concave cylindrical fillets (< 0.130" radius cannot be cut with 1/4" minimum endmill!)
                    int sharpFilletCount = 0;
                    object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                    if (bodies != null)
                    {
                        foreach (object bObj in bodies)
                        {
                            if (!(bObj is IBody2 b)) continue;
                            object[] faces = b.GetFaces() as object[];
                            if (faces == null) continue;
                            foreach (object fObj in faces)
                            {
                                if (fObj is IFace2 f && f.GetSurface() is ISurface s && s.IsCylinder() && f.FaceInSurfaceSense())
                                {
                                    double[] cp = s.CylinderParams as double[];
                                    if (cp != null && cp.Length >= 7)
                                    {
                                        double rIn = cp[6] / 0.0254;
                                        double axZ = Math.Abs(cp[5]);
                                        if (axZ > 0.95 && rIn < 0.124) // vertical internal corner smaller than 1/8" radius
                                            sharpFilletCount++;
                                    }
                                }
                            }
                        }
                    }

                    findings.Add(new
                    {
                        severity = sharpFilletCount == 0 ? "PASS" : "FAIL",
                        rule = "min_quarter_inch_endmill_corner_radius",
                        message = sharpFilletCount == 0
                            ? "No vertical internal corners smaller than 1/4\" cutter radius detected (recommend R >= 0.135\"-0.150\" or 1/4\" dogbones)."
                            : string.Format("Found {0} vertical concave cylindrical faces with R < 0.125\"; smallest owned straight/compression endmill is 1/4\".", sharpFilletCount)
                    });
                }
            }
            else if (process == "sheet_metal_sendcutsend")
            {
                findings.Add(new
                {
                    severity = "WARN",
                    rule = "tier3_cost_lead_time_gate",
                    message = "SendCutSend is Tier 3 DEPRIORITIZED (cost + shipping lead time). Confirm Woodworking, Tough PLA, 1/2\" Corian + MMA, or CNC Acrylic cannot fulfill this part."
                });

                if (!string.IsNullOrWhiteSpace(material) && thickIn.HasValue &&
                    catalog.TryGetSendCutSendGauge(material, thickIn.Value, out var matInfo, out var gauge))
                {
                    findings.Add(new
                    {
                        severity = "INFO",
                        rule = "sendcutsend_gauge_spec",
                        message = string.Format(CultureInfo.InvariantCulture,
                            "{0} @ {1:F3}\": bendable={2}, bend_radius={3:F3}\", k_factor={4}, min_flange_after_bend={5:F3}\", die_width={6:F3}\".",
                            JsonHelper.GetString(matInfo, "display_name"),
                            JsonHelper.GetDouble(gauge, "thickness_in"),
                            JsonHelper.GetBool(matInfo, "bendable"),
                            JsonHelper.GetDouble(gauge, "effective_bend_radius_in"),
                            JsonHelper.GetDouble(gauge, "k_factor"),
                            JsonHelper.GetDouble(gauge, "min_flange_after_bend_in"),
                            JsonHelper.GetDouble(gauge, "die_width_in"))
                    });
                }
            }
            else if (process == "woodworking")
            {
                if (doc != null && docUnits != "in")
                    findings.Add(new { severity = "WARN", rule = "unit_system", message = "Woodworking parts should use IPS (currently " + docUnits + ")." });

                if (doc is IPartDoc part)
                {
                    double[] bb = part.GetPartBox(true) as double[];
                    if (bb != null && bb.Length >= 6)
                    {
                        double dx = (bb[3] - bb[0]) / 0.0254;
                        double dy = (bb[4] - bb[1]) / 0.0254;
                        double minSpan = Math.Min(dx, dy);
                        if (minSpan > 24.0)
                            findings.Add(new { severity = "WARN", rule = "drum_sander_24in_width", message = string.Format("Part width {0:F2}\" exceeds 24\" Grizzly G1066Z drum sander (plan hand/ROS finishing or CNC surfacing).", minSpan) });
                        else if (minSpan > 12.0)
                            findings.Add(new { severity = "INFO", rule = "planer_12in_width", message = string.Format("Part width {0:F2}\" exceeds 12\" planer; thickness via 24\" Grizzly G1066Z dual drum sander.", minSpan) });
                        else if (minSpan > 8.0)
                            findings.Add(new { severity = "INFO", rule = "jointer_8in_width", message = string.Format("Part width {0:F2}\" exceeds 8\" jointer; glue up from <=7.5\" staves or flatten on planer sled.", minSpan) });
                    }
                }

                findings.Add(new
                {
                    severity = "INFO",
                    rule = "woodworking_shop_defaults",
                    message = "Verify actual plywood thicknesses (0.718\", 0.468\", 0.218\") + 0.008\" glue clearance, preferred 4/5/8mm Dominos, 10-30mm metric Forstner bores, and <=4 parallel + <=6 pipe clamps per glue-up stage."
                });
            }

            return McpToolResult.JsonText(new
            {
                process = process,
                document = doc?.GetTitle(),
                document_units = docUnits,
                findings_count = findings.Count,
                findings = findings
            });
        }

        private static McpToolResult HandleCutlistAndTemplates(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            double kerfIn = JsonHelper.GetDouble(args, "kerf_in", 0.125);
            double bushingOd = JsonHelper.GetDouble(args, "router_bushing_od_in", 0.375);
            double bitDia = JsonHelper.GetDouble(args, "router_bit_dia_in", 0.250);
            string outDir = JsonHelper.GetString(args, "output_dir");

            double templateOffsetIn = 0.5 * (bushingOd - bitDia);
            List<object> cutRows = new List<object>();

            if (doc is IPartDoc part)
            {
                object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
                if (bodies != null)
                {
                    foreach (object bObj in bodies)
                    {
                        if (!(bObj is IBody2 b)) continue;
                        double[] bb = b.GetBodyBox() as double[];
                        if (bb != null && bb.Length >= 6)
                        {
                            double[] dims = new[]
                            {
                                (bb[3] - bb[0]) / 0.0254,
                                (bb[4] - bb[1]) / 0.0254,
                                (bb[5] - bb[2]) / 0.0254
                            };
                            Array.Sort(dims); // dims[0]=T, dims[1]=W, dims[2]=L
                            cutRows.Add(new
                            {
                                part = b.Name,
                                thickness_in = Math.Round(dims[0], 4),
                                width_in = Math.Round(dims[1], 3),
                                length_in = Math.Round(dims[2], 3),
                                rough_width_with_kerf_in = Math.Round(dims[1] + kerfIn, 3),
                                rough_length_with_kerf_in = Math.Round(dims[2] + kerfIn, 3)
                            });
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(outDir))
            {
                Directory.CreateDirectory(outDir);
                StringBuilder md = new StringBuilder();
                md.AppendLine("# Shop Cut List & Glue-Up Plan: " + doc.GetTitle());
                md.AppendLine();
                md.AppendLine("- **Table Saw Kerf**: " + kerfIn.ToString("F3", CultureInfo.InvariantCulture) + "\"");
                md.AppendLine("- **Router Guide-Bushing Template Offset**: " + templateOffsetIn.ToString("F4", CultureInfo.InvariantCulture) + "\" (Bushing OD " + bushingOd + "\" - Bit " + bitDia + "\") / 2");
                md.AppendLine("- **Clamp Limits per Stage**: <= 4 Parallel Bar Clamps, <= 6 Pipe Clamps (45x F-Clamps, 12x C-Clamps available)");
                md.AppendLine();
                md.AppendLine("| Part | Thickness (in) | Width (in) | Length (in) | Width + Kerf | Length + Kerf |");
                md.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: |");
                foreach (Dictionary<string, object> r in cutRows.ConvertAll(x => JsonHelper.DeserializeDict(JsonHelper.Serialize(x))))
                {
                    md.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "| {0} | {1} | {2} | {3} | {4} | {5} |",
                        r["part"], r["thickness_in"], r["width_in"], r["length_in"], r["rough_width_with_kerf_in"], r["rough_length_with_kerf_in"]));
                }
                File.WriteAllText(Path.Combine(outDir, "cutlist_and_plan.md"), md.ToString());
            }

            return McpToolResult.JsonText(new
            {
                document = doc.GetTitle(),
                kerf_in = kerfIn,
                router_template_radial_offset_in = Math.Round(templateOffsetIn, 4),
                clamp_limits = new { parallel_bar = 4, pipe = 6, f_clamps = 45, c_clamps = 12 },
                cut_list = cutRows
            });
        }

        private class RayWallSample
        {
            public double[] PointM;
            public double[] OutwardNormal;
        }

        private static void Audit3DPrintBRep(
            IModelDoc2 doc,
            IPartDoc part,
            double[] uBuild,
            double minWebMm,
            List<object> findings)
        {
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null || bodies.Length == 0) return;

            double[] pBox = part.GetPartBox(true) as double[];
            if (pBox == null || pBox.Length < 6) return;

            ProjectBoxAlongAxis(pBox, uBuild, out double sMin, out _);

            List<object> overhangItems = new List<object>();
            double worstOverhangDeg = 0.0;
            int overhangFaceCount = 0;

            int bedChamferFaces = 0;
            int bedRoundFilletFaces = 0;
            int bedBoundaryEdges = 0;

            double globalThinMm = double.MaxValue;
            double[] globalThinLocMm = null;
            int subMinWebCount = 0;
            int severeThinCount = 0;
            List<object> thinWebLocations = new List<object>();

            for (int bIdx = 0; bIdx < bodies.Length; bIdx++)
            {
                if (!(bodies[bIdx] is IBody2 body)) continue;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;

                List<RayWallSample> samples = new List<RayWallSample>();

                foreach (object fObj in faces)
                {
                    if (!(fObj is IFace2 face)) continue;
                    ISurface surf = face.GetSurface() as ISurface;
                    if (surf == null) continue;

                    double areaM2 = face.GetArea();
                    double[] fBox = face.GetBox() as double[];
                    double fsMin = sMin;
                    if (fBox != null && fBox.Length >= 6)
                        ProjectBoxAlongAxis(fBox, uBuild, out fsMin, out _);

                    if (surf.IsPlane())
                    {
                        if (!AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(face, surf, out double[] nOut, out double[] pPt))
                            continue;

                        double downDot = AssemblyGeometryHelper.Dot(nOut, uBuild);

                        // Overhang check (> 1.0mm above bed, area > 2.0 mm^2, downward > 45 deg from vertical)
                        if (fsMin > sMin + 0.001 && areaM2 > 2.0e-6 && downDot < -0.7071)
                        {
                            double overhangDeg = 90.0 - Math.Acos(Math.Max(-1.0, Math.Min(1.0, -downDot))) * 180.0 / Math.PI;
                            overhangFaceCount++;
                            if (overhangDeg > worstOverhangDeg) worstOverhangDeg = overhangDeg;
                            if (overhangItems.Count < 10)
                            {
                                double[] cenM = (fBox != null && fBox.Length >= 6)
                                    ? new[] { 0.5 * (fBox[0] + fBox[3]), 0.5 * (fBox[1] + fBox[4]), 0.5 * (fBox[2] + fBox[5]) }
                                    : pPt;
                                overhangItems.Add(new
                                {
                                    token = SwConnectionManager.ComputeFaceToken(face, bIdx),
                                    surface = "plane",
                                    overhang_deg_from_vertical = Math.Round(overhangDeg, 1),
                                    area_mm2 = Math.Round(areaM2 * 1e6, 2),
                                    center_mm = new[] { Math.Round(cenM[0] * 1000.0, 2), Math.Round(cenM[1] * 1000.0, 2), Math.Round(cenM[2] * 1000.0, 2) }
                                });
                            }
                        }

                        // Bed chamfer check near bed bottom (sMin .. sMin + 1.8mm)
                        if (fsMin <= sMin + 0.0018 && downDot <= -0.60 && downDot >= -0.80)
                            bedChamferFaces++;

                        if (fsMin <= sMin + 0.0005 && downDot < -0.95)
                        {
                            object[] edges = face.GetEdges() as object[];
                            if (edges != null) bedBoundaryEdges += edges.Length;
                        }

                        // Sample interior point(s) for thin-web raycast
                        if (samples.Count < 400 && areaM2 > 1.0e-6)
                            CollectSurfaceUvSamples(face, surf, 2, 2, false, samples, maxPerFace: 6);
                    }
                    else if (surf.IsCylinder() || surf.IsCone())
                    {
                        bool isCyl = surf.IsCylinder();
                        if (!isCyl && fsMin <= sMin + 0.0018)
                        {
                            // Check if cone near bed is ~45 deg bed chamfer
                            double[] uvB = face.GetUVBounds() as double[];
                            if (uvB != null && uvB.Length >= 4)
                            {
                                if (EvaluateSurfacePointAndNormal(face, surf, 0.5 * (uvB[0] + uvB[1]), 0.5 * (uvB[2] + uvB[3]), out _, out double[] cn))
                                {
                                    double cd = AssemblyGeometryHelper.Dot(cn, uBuild);
                                    if (cd <= -0.60 && cd >= -0.80) bedChamferFaces++;
                                }
                            }
                        }

                        if (!face.FaceInSurfaceSense() && fsMin <= sMin + 0.0006 && areaM2 > 0.5e-6)
                        {
                            // Check convex cylinder touching bed at bottom tangency
                            double[] uvB = face.GetUVBounds() as double[];
                            if (uvB != null && uvB.Length >= 4)
                            {
                                double worstD = 0.0;
                                for (int iu = 0; iu <= 2; iu++)
                                {
                                    double u = uvB[0] + (iu / 2.0) * (uvB[1] - uvB[0]);
                                    for (int iv = 0; iv <= 2; iv++)
                                    {
                                        double v = uvB[2] + (iv / 2.0) * (uvB[3] - uvB[2]);
                                        if (EvaluateSurfacePointAndNormal(face, surf, u, v, out _, out double[] fn))
                                            worstD = Math.Min(worstD, AssemblyGeometryHelper.Dot(fn, uBuild));
                                    }
                                }
                                if (worstD < -0.4 && fsMin <= sMin + 0.00025)
                                    bedRoundFilletFaces++;
                            }
                        }

                        // Overhang UV sampling on curved faces strictly above bed
                        if (fsMin > sMin + 0.001 && areaM2 > 2.0e-6)
                        {
                            double worstDown = 1.0;
                            double[] worstPt = null;
                            double[] uvB = face.GetUVBounds() as double[];
                            if (uvB != null && uvB.Length >= 4)
                            {
                                for (int iu = 1; iu <= 3; iu++)
                                {
                                    double u = uvB[0] + (iu / 4.0) * (uvB[1] - uvB[0]);
                                    for (int iv = 1; iv <= 5; iv++)
                                    {
                                        double v = uvB[2] + (iv / 6.0) * (uvB[3] - uvB[2]);
                                        if (EvaluateSurfacePointAndNormal(face, surf, u, v, out double[] pt, out double[] fn))
                                        {
                                            double ptS = AssemblyGeometryHelper.Dot(pt, uBuild);
                                            if (ptS <= sMin + 0.001) continue;
                                            double d = AssemblyGeometryHelper.Dot(fn, uBuild);
                                            if (d < worstDown)
                                            {
                                                worstDown = d;
                                                worstPt = pt;
                                            }
                                        }
                                    }
                                }
                            }

                            // For cylindrical horizontal bores <= 5mm (e.g. M3/M4 horizontal holes), bridges/small holes often self-bridge cleanly
                            bool smallHorizontalHole = isCyl && face.FaceInSurfaceSense() && IsSmallCylinder(surf, 0.005);
                            if (worstDown < -0.7071 && !smallHorizontalHole)
                            {
                                double overhangDeg = 90.0 - Math.Acos(Math.Max(-1.0, Math.Min(1.0, -worstDown))) * 180.0 / Math.PI;
                                overhangFaceCount++;
                                if (overhangDeg > worstOverhangDeg) worstOverhangDeg = overhangDeg;
                                if (overhangItems.Count < 10 && worstPt != null)
                                {
                                    overhangItems.Add(new
                                    {
                                        token = SwConnectionManager.ComputeFaceToken(face, bIdx),
                                        surface = isCyl ? "cylinder" : "cone",
                                        overhang_deg_from_vertical = Math.Round(overhangDeg, 1),
                                        area_mm2 = Math.Round(areaM2 * 1e6, 2),
                                        center_mm = new[] { Math.Round(worstPt[0] * 1000.0, 2), Math.Round(worstPt[1] * 1000.0, 2), Math.Round(worstPt[2] * 1000.0, 2) }
                                    });
                                }
                            }
                        }

                        if (samples.Count < 400 && areaM2 > 1.0e-6)
                            CollectSurfaceUvSamples(face, surf, 3, 4, true, samples, maxPerFace: 6);
                    }
                    else if (surf.IsTorus() && !face.FaceInSurfaceSense() && fsMin <= sMin + 0.0005 && areaM2 > 0.5e-6)
                    {
                        bedRoundFilletFaces++;
                    }
                }

                // Pure-C# tessellated mesh raycast thin-web check (zero IModelDoc2.RayIntersections COM calls)
                if (samples.Count > 0)
                {
                    float[] tris = ExtractBodyTessTriangles(body, faces);
                    if (tris != null && tris.Length >= 9)
                    {
                        List<double[]> samplePts = new List<double[]>(samples.Count);
                        List<double[]> sampleNorms = new List<double[]>(samples.Count);
                        for (int i = 0; i < samples.Count; i++)
                        {
                            samplePts.Add(samples[i].PointM);
                            sampleNorms.Add(samples[i].OutwardNormal);
                        }

                        MeasureThinWebsFromTriangles(
                            tris,
                            samplePts,
                            sampleNorms,
                            Math.Max(0.050, minWebMm * 0.004),
                            out double[] perRayMinMm,
                            out double[][] perRayLocMm);

                        for (int i = 0; i < samples.Count; i++)
                        {
                            double tMm = perRayMinMm[i];
                            if (tMm < minWebMm)
                            {
                                subMinWebCount++;
                                if (tMm < 0.8) severeThinCount++;
                                if (tMm < globalThinMm)
                                {
                                    globalThinMm = tMm;
                                    globalThinLocMm = perRayLocMm[i];
                                }
                                if (thinWebLocations.Count < 8)
                                {
                                    thinWebLocations.Add(new
                                    {
                                        thickness_mm = Math.Round(tMm, 3),
                                        midpoint_mm = perRayLocMm[i]
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        findings.Add(new
                        {
                            severity = "WARN",
                            rule = "min_web_thickness_mesh_unavailable",
                            message = string.Format(CultureInfo.InvariantCulture,
                                "Tessellation triangles unavailable on body {0}; skipping thin-web check for this body.",
                                bIdx)
                        });
                    }
                }
            }

            findings.Add(new
            {
                severity = overhangFaceCount == 0 ? "PASS" : "WARN",
                rule = "unsupported_overhangs",
                overhang_face_count = overhangFaceCount,
                worst_overhang_deg = Math.Round(worstOverhangDeg, 1),
                samples = overhangItems,
                message = overhangFaceCount == 0
                    ? "Zero unsupported >45deg overhangs detected above the print bed."
                    : string.Format(CultureInfo.InvariantCulture,
                        "Found {0} face(s) above bed with >45deg overhang from vertical (worst {1:F1}deg); add <=45deg chamfers, gable roofs, or orient flat on bed.",
                        overhangFaceCount, worstOverhangDeg)
            });

            findings.Add(new
            {
                severity = bedRoundFilletFaces > 0 ? "WARN" : "PASS",
                rule = "bed_adhesion_edges",
                bed_chamfer_faces = bedChamferFaces,
                bed_rounded_fillets = bedRoundFilletFaces,
                message = bedRoundFilletFaces > 0
                    ? string.Format("Found {0} rounded fillet(s) touching the print bed; replace bottom bed fillets with 45deg chamfers (0.6-1.5mm) to eliminate overhang curl.", bedRoundFilletFaces)
                    : (bedChamferFaces > 0
                        ? string.Format("Detected {0} bed chamfer face(s) and zero rounded bottom fillets.", bedChamferFaces)
                        : string.Format("No rounded bottom fillets touching the bed ({0} bed boundary edges; optional 0.6mm x 45deg chamfer recommended against elephant's foot).", bedBoundaryEdges))
            });

            findings.Add(new
            {
                severity = severeThinCount > 0 ? "FAIL" : (subMinWebCount > 0 ? "WARN" : "PASS"),
                rule = "min_web_thickness",
                min_measured_thickness_mm = globalThinMm < double.MaxValue ? (object)Math.Round(globalThinMm, 3) : null,
                threshold_mm = minWebMm,
                worst_location_mm = globalThinLocMm,
                samples = thinWebLocations,
                message = subMinWebCount == 0
                    ? string.Format(CultureInfo.InvariantCulture, "All raycast wall/web samples meet >= {0:F2}mm minimum web thickness.", minWebMm)
                    : string.Format(CultureInfo.InvariantCulture,
                        "Detected {0} thin web/wall sample(s) below {1:F2}mm (minimum {2:F3}mm near [{3}]).",
                        subMinWebCount, minWebMm, globalThinMm,
                        globalThinLocMm != null ? string.Join(", ", Array.ConvertAll(globalThinLocMm, v => v.ToString("F2", CultureInfo.InvariantCulture))) : "")
            });
        }

        private struct TessTriangle
        {
            public double V0x, V0y, V0z;
            public double E1x, E1y, E1z;
            public double E2x, E2y, E2z;
            public double Nx, Ny, Nz;
            public double MinX, MinY, MinZ;
            public double MaxX, MaxY, MaxZ;
        }

        public static float[] ExtractBodyTessTriangles(IBody2 body, object[] fallbackFaces)
        {
            object[] faceArr = (fallbackFaces != null && fallbackFaces.Length > 0)
                ? fallbackFaces
                : (body != null ? body.GetFaces() as object[] : null);

            if (faceArr == null || faceArr.Length == 0)
                return new float[0];

            List<float> combined = new List<float>();
            foreach (object fObj in faceArr)
            {
                if (!(fObj is IFace2 face)) continue;
                try
                {
                    float[] faceTris = face.GetTessTriangles(true) as float[];
                    if (faceTris == null || faceTris.Length < 9)
                        continue;

                    float[] faceNorms = null;
                    try { faceNorms = face.GetTessNorms() as float[]; } catch { }

                    double[] planarNorm = null;
                    if (faceNorms == null || faceNorms.Length < faceTris.Length)
                    {
                        try
                        {
                            ISurface surf = face.GetSurface() as ISurface;
                            if (surf != null && surf.IsPlane())
                                AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(face, surf, out planarNorm, out _);
                            else
                            {
                                double[] rawN = face.Normal as double[];
                                if (rawN != null && rawN.Length >= 3)
                                    planarNorm = rawN;
                            }
                        }
                        catch { }
                    }

                    int triCount = faceTris.Length / 9;
                    for (int t = 0; t < triCount; t++)
                    {
                        int b = t * 9;
                        float v0x = faceTris[b], v0y = faceTris[b + 1], v0z = faceTris[b + 2];
                        float v1x = faceTris[b + 3], v1y = faceTris[b + 4], v1z = faceTris[b + 5];
                        float v2x = faceTris[b + 6], v2y = faceTris[b + 7], v2z = faceTris[b + 8];

                        double rnx = 0.0, rny = 0.0, rnz = 0.0;
                        bool hasRefNorm = false;
                        if (faceNorms != null && b + 8 < faceNorms.Length)
                        {
                            rnx = (double)faceNorms[b] + faceNorms[b + 3] + faceNorms[b + 6];
                            rny = (double)faceNorms[b + 1] + faceNorms[b + 4] + faceNorms[b + 7];
                            rnz = (double)faceNorms[b + 2] + faceNorms[b + 5] + faceNorms[b + 8];
                            if (rnx * rnx + rny * rny + rnz * rnz > 1e-12)
                                hasRefNorm = true;
                        }
                        if (!hasRefNorm && planarNorm != null && planarNorm.Length >= 3)
                        {
                            rnx = planarNorm[0]; rny = planarNorm[1]; rnz = planarNorm[2];
                            if (rnx * rnx + rny * rny + rnz * rnz > 1e-12)
                                hasRefNorm = true;
                        }

                        bool flipWinding = false;
                        if (hasRefNorm)
                        {
                            double e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
                            double e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;
                            double cx = e1y * e2z - e1z * e2y;
                            double cy = e1z * e2x - e1x * e2z;
                            double cz = e1x * e2y - e1y * e2x;
                            if (cx * rnx + cy * rny + cz * rnz < 0.0)
                                flipWinding = true;
                        }

                        combined.Add(v0x); combined.Add(v0y); combined.Add(v0z);
                        if (flipWinding)
                        {
                            combined.Add(v2x); combined.Add(v2y); combined.Add(v2z);
                            combined.Add(v1x); combined.Add(v1y); combined.Add(v1z);
                        }
                        else
                        {
                            combined.Add(v1x); combined.Add(v1y); combined.Add(v1z);
                            combined.Add(v2x); combined.Add(v2y); combined.Add(v2z);
                        }
                    }
                }
                catch { }
            }
            return combined.ToArray();
        }

        public static void MeasureThinWebsFromTriangles(
            float[] tris,
            IList<double[]> samplePointsM,
            IList<double[]> sampleOutwardNormals,
            double maxSearchDistM,
            out double[] perRayMinMm,
            out double[][] perRayLocMm)
        {
            int n = samplePointsM != null ? samplePointsM.Count : 0;
            perRayMinMm = new double[n];
            perRayLocMm = new double[n][];
            for (int i = 0; i < n; i++)
                perRayMinMm[i] = double.MaxValue;

            if (n == 0 || tris == null || tris.Length < 9 || sampleOutwardNormals == null || sampleOutwardNormals.Count < n)
                return;

            int rawTriCount = tris.Length / 9;
            List<TessTriangle> mesh = new List<TessTriangle>(rawTriCount);
            double gMinX = double.MaxValue, gMinY = double.MaxValue, gMinZ = double.MaxValue;
            double gMaxX = double.MinValue, gMaxY = double.MinValue, gMaxZ = double.MinValue;

            for (int t = 0; t < rawTriCount; t++)
            {
                int b = t * 9;
                double v0x = tris[b], v0y = tris[b + 1], v0z = tris[b + 2];
                double v1x = tris[b + 3], v1y = tris[b + 4], v1z = tris[b + 5];
                double v2x = tris[b + 6], v2y = tris[b + 7], v2z = tris[b + 8];

                double e1x = v1x - v0x, e1y = v1y - v0y, e1z = v1z - v0z;
                double e2x = v2x - v0x, e2y = v2y - v0y, e2z = v2z - v0z;
                double cx = e1y * e2z - e1z * e2y;
                double cy = e1z * e2x - e1x * e2z;
                double cz = e1x * e2y - e1y * e2x;
                double len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (len < 1e-13) continue;

                double invLen = 1.0 / len;
                double minX = Math.Min(v0x, Math.Min(v1x, v2x));
                double minY = Math.Min(v0y, Math.Min(v1y, v2y));
                double minZ = Math.Min(v0z, Math.Min(v1z, v2z));
                double maxX = Math.Max(v0x, Math.Max(v1x, v2x));
                double maxY = Math.Max(v0y, Math.Max(v1y, v2y));
                double maxZ = Math.Max(v0z, Math.Max(v1z, v2z));

                mesh.Add(new TessTriangle
                {
                    V0x = v0x, V0y = v0y, V0z = v0z,
                    E1x = e1x, E1y = e1y, E1z = e1z,
                    E2x = e2x, E2y = e2y, E2z = e2z,
                    Nx = cx * invLen, Ny = cy * invLen, Nz = cz * invLen,
                    MinX = minX, MinY = minY, MinZ = minZ,
                    MaxX = maxX, MaxY = maxY, MaxZ = maxZ
                });

                if (minX < gMinX) gMinX = minX;
                if (minY < gMinY) gMinY = minY;
                if (minZ < gMinZ) gMinZ = minZ;
                if (maxX > gMaxX) gMaxX = maxX;
                if (maxY > gMaxY) gMaxY = maxY;
                if (maxZ > gMaxZ) gMaxZ = maxZ;
            }

            if (mesh.Count == 0)
                return;

            double diag = Math.Sqrt(
                (gMaxX - gMinX) * (gMaxX - gMinX) +
                (gMaxY - gMinY) * (gMaxY - gMinY) +
                (gMaxZ - gMinZ) * (gMaxZ - gMinZ));
            double searchDist = maxSearchDistM > 0.0 ? Math.Min(maxSearchDistM, Math.Max(diag + 0.001, 0.005)) : Math.Max(diag + 0.001, 0.005);

            const int GridRes = 12;
            bool useGrid = mesh.Count > 200;
            List<int>[] grid = null;
            int[] seenStamp = null;
            double cellX = 1.0, cellY = 1.0, cellZ = 1.0;

            if (useGrid)
            {
                cellX = Math.Max(1e-5, (gMaxX - gMinX) / GridRes);
                cellY = Math.Max(1e-5, (gMaxY - gMinY) / GridRes);
                cellZ = Math.Max(1e-5, (gMaxZ - gMinZ) / GridRes);
                grid = new List<int>[GridRes * GridRes * GridRes];
                seenStamp = new int[mesh.Count];

                for (int ti = 0; ti < mesh.Count; ti++)
                {
                    TessTriangle tri = mesh[ti];
                    int x0 = ClampCell((int)Math.Floor((tri.MinX - gMinX) / cellX), GridRes);
                    int x1 = ClampCell((int)Math.Floor((tri.MaxX - gMinX) / cellX), GridRes);
                    int y0 = ClampCell((int)Math.Floor((tri.MinY - gMinY) / cellY), GridRes);
                    int y1 = ClampCell((int)Math.Floor((tri.MaxY - gMinY) / cellY), GridRes);
                    int z0 = ClampCell((int)Math.Floor((tri.MinZ - gMinZ) / cellZ), GridRes);
                    int z1 = ClampCell((int)Math.Floor((tri.MaxZ - gMinZ) / cellZ), GridRes);

                    for (int ix = x0; ix <= x1; ix++)
                    for (int iy = y0; iy <= y1; iy++)
                    for (int iz = z0; iz <= z1; iz++)
                    {
                        int cellIdx = (ix * GridRes + iy) * GridRes + iz;
                        List<int> bucket = grid[cellIdx];
                        if (bucket == null)
                        {
                            bucket = new List<int>(4);
                            grid[cellIdx] = bucket;
                        }
                        bucket.Add(ti);
                    }
                }
            }

            for (int r = 0; r < n; r++)
            {
                double[] p = samplePointsM[r];
                double[] rawN = sampleOutwardNormals[r];
                if (p == null || p.Length < 3 || rawN == null || rawN.Length < 3)
                    continue;

                double nLen = Math.Sqrt(rawN[0] * rawN[0] + rawN[1] * rawN[1] + rawN[2] * rawN[2]);
                if (nLen < 1e-12) continue;
                double nx = rawN[0] / nLen, ny = rawN[1] / nLen, nz = rawN[2] / nLen;

                double ox = p[0] - 0.00003 * nx;
                double oy = p[1] - 0.00003 * ny;
                double oz = p[2] - 0.00003 * nz;
                double dx = -nx, dy = -ny, dz = -nz;

                double bestT = searchDist;
                double bestHx = 0.0, bestHy = 0.0, bestHz = 0.0;
                bool found = false;

                if (!useGrid)
                {
                    for (int ti = 0; ti < mesh.Count; ti++)
                    {
                        TessTriangle tri = mesh[ti];
                        TestTriangleHit(
                            ref tri, ox, oy, oz, dx, dy, dz, nx, ny, nz,
                            ref bestT, ref bestHx, ref bestHy, ref bestHz, ref found);
                    }
                }
                else
                {
                    int stamp = r + 1;
                    double endX = ox + bestT * dx;
                    double endY = oy + bestT * dy;
                    double endZ = oz + bestT * dz;

                    int cx0 = ClampCell((int)Math.Floor((Math.Min(ox, endX) - gMinX) / cellX), GridRes);
                    int cx1 = ClampCell((int)Math.Floor((Math.Max(ox, endX) - gMinX) / cellX), GridRes);
                    int cy0 = ClampCell((int)Math.Floor((Math.Min(oy, endY) - gMinY) / cellY), GridRes);
                    int cy1 = ClampCell((int)Math.Floor((Math.Max(oy, endY) - gMinY) / cellY), GridRes);
                    int cz0 = ClampCell((int)Math.Floor((Math.Min(oz, endZ) - gMinZ) / cellZ), GridRes);
                    int cz1 = ClampCell((int)Math.Floor((Math.Max(oz, endZ) - gMinZ) / cellZ), GridRes);

                    for (int ix = cx0; ix <= cx1; ix++)
                    for (int iy = cy0; iy <= cy1; iy++)
                    for (int iz = cz0; iz <= cz1; iz++)
                    {
                        List<int> bucket = grid[(ix * GridRes + iy) * GridRes + iz];
                        if (bucket == null) continue;
                        for (int bi = 0; bi < bucket.Count; bi++)
                        {
                            int ti = bucket[bi];
                            if (seenStamp[ti] == stamp) continue;
                            seenStamp[ti] = stamp;

                            TessTriangle tri = mesh[ti];
                            TestTriangleHit(
                                ref tri, ox, oy, oz, dx, dy, dz, nx, ny, nz,
                                ref bestT, ref bestHx, ref bestHy, ref bestHz, ref found);
                        }
                    }
                }

                if (found)
                {
                    double proj = bestT + 0.00003;
                    if (proj > 0.00004)
                    {
                        perRayMinMm[r] = proj * 1000.0;
                        perRayLocMm[r] = new[]
                        {
                            Math.Round(0.5 * (p[0] + bestHx) * 1000.0, 2),
                            Math.Round(0.5 * (p[1] + bestHy) * 1000.0, 2),
                            Math.Round(0.5 * (p[2] + bestHz) * 1000.0, 2)
                        };
                    }
                }
            }
        }

        private static int ClampCell(int idx, int res)
        {
            if (idx < 0) return 0;
            if (idx >= res) return res - 1;
            return idx;
        }

        private static void TestTriangleHit(
            ref TessTriangle tri,
            double ox, double oy, double oz,
            double dx, double dy, double dz,
            double nx, double ny, double nz,
            ref double bestT,
            ref double bestHx, ref double bestHy, ref double bestHz,
            ref bool found)
        {
            double dotOpp = tri.Nx * nx + tri.Ny * ny + tri.Nz * nz;
            if (dotOpp > -0.45) return;

            if (!RayIntersectsAabb(ox, oy, oz, dx, dy, dz, bestT, tri.MinX, tri.MinY, tri.MinZ, tri.MaxX, tri.MaxY, tri.MaxZ))
                return;

            double px = dy * tri.E2z - dz * tri.E2y;
            double py = dz * tri.E2x - dx * tri.E2z;
            double pz = dx * tri.E2y - dy * tri.E2x;
            double det = tri.E1x * px + tri.E1y * py + tri.E1z * pz;
            if (Math.Abs(det) < 1e-14) return;

            double invDet = 1.0 / det;
            double tx = ox - tri.V0x, ty = oy - tri.V0y, tz = oz - tri.V0z;
            double u = (tx * px + ty * py + tz * pz) * invDet;
            if (u < -1e-5 || u > 1.0 + 1e-5) return;

            double qx = ty * tri.E1z - tz * tri.E1y;
            double qy = tz * tri.E1x - tx * tri.E1z;
            double qz = tx * tri.E1y - ty * tri.E1x;
            double v = (dx * qx + dy * qy + dz * qz) * invDet;
            if (v < -1e-5 || u + v > 1.0 + 1e-5) return;

            double t = (tri.E2x * qx + tri.E2y * qy + tri.E2z * qz) * invDet;
            if (t <= 0.00001 || t >= bestT) return;

            bestT = t;
            bestHx = ox + t * dx;
            bestHy = oy + t * dy;
            bestHz = oz + t * dz;
            found = true;
        }

        private static bool RayIntersectsAabb(
            double ox, double oy, double oz,
            double dx, double dy, double dz,
            double maxT,
            double bMinX, double bMinY, double bMinZ,
            double bMaxX, double bMaxY, double bMaxZ)
        {
            double tMin = 0.0;
            double tMax = maxT;
            if (!ClipAxis(ox, dx, bMinX, bMaxX, ref tMin, ref tMax)) return false;
            if (!ClipAxis(oy, dy, bMinY, bMaxY, ref tMin, ref tMax)) return false;
            if (!ClipAxis(oz, dz, bMinZ, bMaxZ, ref tMin, ref tMax)) return false;
            return true;
        }

        private static bool ClipAxis(double o, double d, double minB, double maxB, ref double tMin, ref double tMax)
        {
            if (Math.Abs(d) < 1e-12)
                return o >= minB - 1e-7 && o <= maxB + 1e-7;
            double invD = 1.0 / d;
            double t0 = (minB - o) * invD;
            double t1 = (maxB - o) * invD;
            if (t0 > t1) { double tmp = t0; t0 = t1; t1 = tmp; }
            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            return tMax >= tMin;
        }

        private static bool IsSmallCylinder(ISurface surf, double maxDiaM)
        {
            double[] cp = surf.CylinderParams as double[];
            return cp != null && cp.Length >= 7 && (2.0 * cp[6]) <= maxDiaM;
        }

        private static bool EvaluateSurfacePointAndNormal(
            IFace2 face,
            ISurface surf,
            double u,
            double v,
            out double[] pointM,
            out double[] outwardNormal)
        {
            pointM = null;
            outwardNormal = null;
            double[] ev = surf.Evaluate(u, v, 1, 1) as double[];
            if (ev == null || ev.Length < 12) return false;
            pointM = new[] { ev[0], ev[1], ev[2] };
            double[] n = { ev[ev.Length - 3], ev[ev.Length - 2], ev[ev.Length - 1] };
            if (face.FaceInSurfaceSense()) { n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]; }
            outwardNormal = AssemblyGeometryHelper.Normalize(n);
            return true;
        }

        private static void CollectSurfaceUvSamples(
            IFace2 face,
            ISurface surf,
            int nu,
            int nv,
            bool cardinalMidOnly,
            List<RayWallSample> samples,
            int maxPerFace = 6)
        {
            double[] uv = face.GetUVBounds() as double[];
            if (uv == null || uv.Length < 4) return;
            double[] fb = face.GetBox() as double[];
            int added = 0;

            for (int iu = 1; iu <= nu && added < maxPerFace; iu++)
            {
                double u = uv[0] + (iu / (double)(nu + 1)) * (uv[1] - uv[0]);
                for (int iv = 1; iv <= nv && added < maxPerFace; iv++)
                {
                    if (samples.Count >= 400) return;
                    double v = cardinalMidOnly
                        ? uv[2] + ((iv - 1) / (double)Math.Max(1, nv)) * (uv[3] - uv[2])
                        : uv[2] + (iv / (double)(nv + 1)) * (uv[3] - uv[2]);
                    if (!EvaluateSurfacePointAndNormal(face, surf, u, v, out double[] pt, out double[] n))
                        continue;
                    if (fb != null && fb.Length >= 6)
                    {
                        double eps = 0.0002;
                        if (pt[0] < fb[0] - eps || pt[0] > fb[3] + eps ||
                            pt[1] < fb[1] - eps || pt[1] > fb[4] + eps ||
                            pt[2] < fb[2] - eps || pt[2] > fb[5] + eps)
                            continue;
                    }
                    double[] cp = face.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[];
                    if (cp != null && cp.Length >= 3)
                    {
                        double d2 = (cp[0] - pt[0]) * (cp[0] - pt[0]) +
                                    (cp[1] - pt[1]) * (cp[1] - pt[1]) +
                                    (cp[2] - pt[2]) * (cp[2] - pt[2]);
                        if (d2 > 1e-9) continue;
                    }
                    samples.Add(new RayWallSample { PointM = pt, OutwardNormal = n });
                    added++;
                }
            }

            if (added == 0 && samples.Count < 400 && surf.IsPlane())
            {
                float[] tris = face.GetTessTriangles(true) as float[];
                if (tris != null && tris.Length >= 9)
                {
                    double cx = (tris[0] + tris[3] + tris[6]) / 3.0;
                    double cy = (tris[1] + tris[4] + tris[7]) / 3.0;
                    double cz = (tris[2] + tris[5] + tris[8]) / 3.0;
                    if (AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(face, surf, out double[] fn, out _))
                        samples.Add(new RayWallSample { PointM = new[] { cx, cy, cz }, OutwardNormal = fn });
                }
            }
        }

        private static void ProjectBoxAlongAxis(double[] b, double[] axis, out double sMin, out double sMax)
        {
            sMin = double.MaxValue;
            sMax = double.MinValue;
            for (int ix = 0; ix <= 1; ix++)
            for (int iy = 0; iy <= 1; iy++)
            for (int iz = 0; iz <= 1; iz++)
            {
                double s = (ix == 0 ? b[0] : b[3]) * axis[0] +
                           (iy == 0 ? b[1] : b[4]) * axis[1] +
                           (iz == 0 ? b[2] : b[5]) * axis[2];
                if (s < sMin) sMin = s;
                if (s > sMax) sMax = s;
            }
        }
    }
}
