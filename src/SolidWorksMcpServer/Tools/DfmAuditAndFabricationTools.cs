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
    public static class DfmAuditAndFabricationTools
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr, ShopCatalogService catalog)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_audit_dfm",
                Description = "Audit active SOLIDWORKS geometry against shop_profile.json & sendcutsend_catalog.json for 3D printing (Prusa MK3.5S+ MMGS), CNC carving (Onefinity 8-bit library), SendCutSend sheet metal (deprioritized gate), or woodworking (IPS machinery & clamps).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "process", JsonHelper.Prop("string", "Manufacturing process to audit.", "3d_printing", new[] { "3d_printing", "cnc_carving", "sheet_metal_sendcutsend", "woodworking" }) },
                    { "material", JsonHelper.Prop("string", "Optional material name/key for tighter checks.") },
                    { "thickness_in", JsonHelper.Prop("number", "Optional sheet thickness in inches for SendCutSend checks.") },
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
            if (swMgr.TryAttachOnly(out ISldWorks app))
                doc = app.ActiveDoc as IModelDoc2;

            if (doc == null && !string.IsNullOrWhiteSpace(JsonHelper.GetString(args, "target_doc")))
                doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));

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
                    findings.Add(new
                    {
                        severity = "INFO",
                        rule = "filament_and_nozzle",
                        message = "Default filament: MatterHackers Tough PLA (Basic) on fixed 0.4mm nozzle (walls in multiples of 0.4mm: 1.2, 1.6, 2.0mm). Check bottom chamfers (45deg) and horizontal hole teardrops."
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
    }
}
