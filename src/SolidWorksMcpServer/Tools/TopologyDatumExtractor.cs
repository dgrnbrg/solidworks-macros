using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcpServer.Core;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Tools
{
    public static class TopologyDatumExtractor
    {
        public static void Register(McpStdioHost host, SwConnectionManager swMgr)
        {
            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_inspect_model_summary",
                Description = "Return tight 3D bounding box, bodies/components, mass (enabled by default for parts and <=30-component assemblies when requested), volume, surface area, CoG, and document unit system.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "units", JsonHelper.Prop("string", "Output units: mm, in, or m (defaults to active doc units).") },
                    { "include_mass_properties", JsonHelper.Prop("boolean", "Compute IMassProperty2 metrics (defaults to true for parts, false for assemblies to avoid slow rebuilds on >30-component assemblies).") }
                }),
                Handler = args => HandleModelSummary(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_inspect_feature_tree",
                Description = "Inspect FeatureManager Design Tree features, parent/child dependencies, sketch constraint states (-)/(+), and rebuild errors.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "include_subfeatures", JsonHelper.Prop("boolean", "Include absorbed sub-features/sketches (skipped on assembly component Reference items for speed).", true) }
                }),
                Handler = args => HandleInspectTree(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_inspect_topology_datums",
                Description = "Extract planar mounting faces, cylindrical holes/bosses, coplanar bolt-hole patterns, and KiCad PCB connector/board outlines from any SLDPRT or STEP import.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "units", JsonHelper.Prop("string", "Output units: mm or in.") },
                    { "extract_planar_datums", JsonHelper.Prop("boolean", "Rank largest planar datum faces.", true) },
                    { "extract_hole_patterns", JsonHelper.Prop("boolean", "Group cylindrical holes and bolt patterns.", true) },
                    { "pcb_mode", JsonHelper.Prop("boolean", "Extract KiCad PCB outline, FR4 thickness, mounting holes, and top/bottom component heights.", false) },
                    { "max_faces", JsonHelper.Prop("integer", "Maximum planar faces to return.", 25) }
                }),
                Handler = args => HandleInspectTopology(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_inspect_assembly_joints",
                Description = "Scan an assembly (.SLDASM) in global coordinates to pair coaxial holes across parts into joint stacks (including subassembly boundaries, countersinks, and counterbores), detect existing fasteners, measure radial boss & bidirectional axial clearances, and recommend McMaster recipes (18-8 Stainless Steel default).",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional assembly title/path.") },
                    { "units", JsonHelper.Prop("string", "Output units: mm or in.") },
                    { "nut_style", JsonHelper.Prop("string", "Through-bolted nut configuration: nylock, hex_nut (single nut), or jam_nut_stack (hex nut + jam nut).", "nylock", new[] { "nylock", "hex_nut", "jam_nut_stack" }) },
                    { "material", JsonHelper.Prop("string", "Preferred fastener material (defaults to 18-8 Stainless Steel; e.g. '316 Stainless Steel', 'Black-Oxide Alloy Steel', 'Brass', 'Zinc-Plated Steel').", "18-8 Stainless Steel") },
                    { "max_hole_diameter_mm", JsonHelper.Prop("number", "Maximum cylindrical hole diameter in mm to treat as a candidate fastener hole.", 16.0) },
                    { "include_single_component_holes", JsonHelper.Prop("boolean", "Also return isolated holes that only pass through a single component.", false) },
                    { "top_level_only", JsonHelper.Prop("boolean", "Keep true for fast top-level + boundary scans (setting false requires include_components subset filter).", true) },
                    { "include_subassembly_boundary_joints", JsonHelper.Prop("boolean", "When top_level_only is true, also scan subassembly leaf components touching top-level parts so boundary joints are detected without deep internal recursion.", true) },
                    { "include_components", JsonHelper.ArrayProp("string", "Optional subset of component names/paths/patterns (including nested subassembly components) to scope joint scanning.") }
                }),
                Handler = args => AssemblyJointAnalyzer.InspectAssemblyJoints(swMgr, args)
            });

            host.RegisterTool(new McpToolDefinition
            {
                Name = "sw_measure_clearance",
                Description = "Run fast subset assembly interference detection (requires include_components when measuring interference) with component-pair grouping and thread vs collision classification; or measure minimum distance between entity_a and entity_b.",
                InputSchema = JsonHelper.Schema(new Dictionary<string, object>
                {
                    { "target_doc", JsonHelper.Prop("string", "Optional document title/path.") },
                    { "entity_a", JsonHelper.Prop("string", "Optional first entity token/name/comp selector for ClosestDistance.") },
                    { "entity_b", JsonHelper.Prop("string", "Optional second entity token/name/comp selector for ClosestDistance.") },
                    { "include_components", JsonHelper.ArrayProp("string", "Subset of 2 to 40 component names/paths/patterns (supports nested paths and trailing * prefix wildcard) to pre-select into InterferenceDetectionManager. Unfiltered all-to-all interference checks are disabled.") },
                    { "ignore_pairs", JsonHelper.ArrayProp("object", "Optional [{component_a, component_b}] component pairs to ignore (e.g. actuated switch feelers or known contacts).") },
                    { "units", JsonHelper.Prop("string", "Output units: mm or in.") },
                    { "exclude_expected_thread_engagements", JsonHelper.Prop("boolean", "Filter out expected bolt-to-nut / tapped-hole thread overlaps and return only unexpected collisions.", false) },
                    { "exclude_same_subassembly_pairs", JsonHelper.Prop("boolean", "Skip internal component pairs within the same subassembly when checking cross-component clearance (default true).", true) },
                    { "group_by_pair", JsonHelper.Prop("boolean", "Group interferences by component pair type and reason for a compact summary (default true).", true) }
                }),
                Handler = args => HandleMeasureClearance(swMgr, args)
            });
        }

        private static McpToolResult HandleModelSummary(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            bool isAssy = doc is IAssemblyDoc;
            bool includeMass = args.ContainsKey("include_mass_properties")
                ? JsonHelper.GetBool(args, "include_mass_properties", !isAssy)
                : !isAssy;

            double[] boxM = null;
            List<object> bodiesOut = new List<object>();

            if (doc is IPartDoc part)
            {
                boxM = part.GetPartBox(true) as double[];
                object[] bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
                if (bodies != null)
                {
                    for (int i = 0; i < bodies.Length; i++)
                    {
                        if (bodies[i] is IBody2 b)
                        {
                            double[] bb = b.GetBodyBox() as double[];
                            bodiesOut.Add(new
                            {
                                index = i,
                                name = b.Name,
                                is_sheet_metal = b.IsSheetMetal(),
                                size = bb != null && bb.Length >= 6 ? new
                                {
                                    x = Math.Round(ShopCatalogService.FromMeters(bb[3] - bb[0], units), 4),
                                    y = Math.Round(ShopCatalogService.FromMeters(bb[4] - bb[1], units), 4),
                                    z = Math.Round(ShopCatalogService.FromMeters(bb[5] - bb[2], units), 4)
                                } : null
                            });
                        }
                    }
                }
            }
            else if (doc is IAssemblyDoc assy)
            {
                boxM = assy.GetBox(0) as double[];
                object[] comps = assy.GetComponents(true) as object[];
                if (comps != null)
                {
                    foreach (object cObj in comps)
                    {
                        if (cObj is IComponent2 c)
                        {
                            bodiesOut.Add(new
                            {
                                name = c.Name2,
                                path = c.GetPathName(),
                                suppressed = c.GetSuppression() == (int)swComponentSuppressionState_e.swComponentSuppressed
                            });
                        }
                    }
                }
            }

            string massNote = null;
            MassStats ms = null;
            if (includeMass)
            {
                if (isAssy && bodiesOut.Count > 30)
                {
                    massNote = string.Format("Skipped CreateMassProperty2 on assembly with {0} top-level components (>30 limit) to prevent long stalls.", bodiesOut.Count);
                }
                else
                {
                    ms = SwConnectionManager.ComputeMassStats(doc);
                }
            }
            else if (isAssy)
            {
                massNote = "Skipped on assembly by default for speed (pass include_mass_properties=true on assemblies with <=30 components if needed).";
            }

            return McpToolResult.JsonText(new
            {
                title = doc.GetTitle(),
                path = doc.GetPathName(),
                doc_type = doc.GetType(),
                document_units = SwConnectionManager.GetDocumentUnits(doc),
                output_units = units,
                bounding_box = boxM != null && boxM.Length >= 6 ? new
                {
                    min = new[] { Math.Round(ShopCatalogService.FromMeters(boxM[0], units), 4), Math.Round(ShopCatalogService.FromMeters(boxM[1], units), 4), Math.Round(ShopCatalogService.FromMeters(boxM[2], units), 4) },
                    max = new[] { Math.Round(ShopCatalogService.FromMeters(boxM[3], units), 4), Math.Round(ShopCatalogService.FromMeters(boxM[4], units), 4), Math.Round(ShopCatalogService.FromMeters(boxM[5], units), 4) },
                    span = new
                    {
                        x = Math.Round(ShopCatalogService.FromMeters(boxM[3] - boxM[0], units), 4),
                        y = Math.Round(ShopCatalogService.FromMeters(boxM[4] - boxM[1], units), 4),
                        z = Math.Round(ShopCatalogService.FromMeters(boxM[5] - boxM[2], units), 4)
                    }
                } : null,
                mass_properties = ms != null ? new
                {
                    mass_kg = Math.Round(ms.MassKg, 6),
                    volume_m3 = ms.VolumeM3,
                    surface_area_m2 = ms.SurfaceAreaM2,
                    center_of_mass = new[]
                    {
                        Math.Round(ShopCatalogService.FromMeters(ms.CenterOfMassM[0], units), 4),
                        Math.Round(ShopCatalogService.FromMeters(ms.CenterOfMassM[1], units), 4),
                        Math.Round(ShopCatalogService.FromMeters(ms.CenterOfMassM[2], units), 4)
                    }
                } : null,
                mass_properties_note = massNote,
                items = bodiesOut
            });
        }

        private static McpToolResult HandleInspectTree(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            bool includeSubs = JsonHelper.GetBool(args, "include_subfeatures", true);
            bool isAssy = doc is IAssemblyDoc;

            List<object> featuresOut = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IFeature feat = doc.FirstFeature() as IFeature;
            int idx = 0;
            while (feat != null)
            {
                string fType = feat.GetTypeName2() ?? string.Empty;
                bool skipHeavyAssyComponentNode = isAssy &&
                    (string.Equals(fType, "Reference", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(fType, "ReferencePattern", StringComparison.OrdinalIgnoreCase));

                AddFeatureEntry(feat, idx++, false, skipParents: skipHeavyAssyComponentNode, seen: seen, list: featuresOut);
                if (includeSubs && !skipHeavyAssyComponentNode)
                {
                    IFeature sub = feat.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        AddFeatureEntry(sub, idx++, true, skipParents: false, seen: seen, list: featuresOut);
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                feat = feat.GetNextFeature() as IFeature;
            }

            return McpToolResult.JsonText(new
            {
                title = doc.GetTitle(),
                feature_count = featuresOut.Count,
                features = featuresOut
            });
        }

        private static void AddFeatureEntry(IFeature feat, int order, bool isSub, bool skipParents, HashSet<string> seen, List<object> list)
        {
            if (feat == null || !seen.Add(feat.Name + "|" + isSub))
                return;

            string typeName = feat.GetTypeName2();
            bool isSketch = string.Equals(typeName, "ProfileFeature", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(typeName, "3DProfileFeature", StringComparison.OrdinalIgnoreCase);

            int constraintStatus = -1;
            if (isSketch && feat.GetSpecificFeature2() is ISketch sk)
                constraintStatus = sk.GetConstrainedStatus();

            List<string> parents = new List<string>();
            if (!skipParents)
            {
                object[] pArr = feat.GetParents() as object[];
                if (pArr != null)
                {
                    foreach (object pObj in pArr)
                        if (pObj is IFeature pf) parents.Add(pf.Name);
                }
            }

            int errCode = feat.GetErrorCode2(out bool isWarn);

            list.Add(new
            {
                order = order,
                name = feat.Name,
                type = typeName,
                is_subfeature = isSub,
                suppressed = feat.IsSuppressed(),
                sketch_constrained_status = constraintStatus,
                parents = parents,
                error_code = errCode,
                is_warning = isWarn
            });
        }

        private class CylHoleInfo
        {
            public string Token;
            public double DiameterM;
            public double DepthM;
            public double[] OriginM;
            public double[] Axis;
            public bool IsThroughOrOpen;
        }

        private static McpToolResult HandleInspectTopology(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            bool wantPlanes = JsonHelper.GetBool(args, "extract_planar_datums", true);
            bool wantHoles = JsonHelper.GetBool(args, "extract_hole_patterns", true);
            bool pcbMode = JsonHelper.GetBool(args, "pcb_mode", false);
            int maxFaces = JsonHelper.GetInt(args, "max_faces", 25);

            if (!(doc is IPartDoc part))
                return McpToolResult.Error("sw_inspect_topology_datums currently inspects active part documents (open the component part or STEP body).");

            object[] bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if (bodies == null || bodies.Length == 0)
                return McpToolResult.Error("No solid/surface bodies found in document.");

            List<Dictionary<string, object>> planarFaces = new List<Dictionary<string, object>>();
            List<CylHoleInfo> cylHoles = new List<CylHoleInfo>();
            List<object> cylBosses = new List<object>();

            for (int bIdx = 0; bIdx < bodies.Length; bIdx++)
            {
                if (!(bodies[bIdx] is IBody2 body)) continue;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;

                foreach (object fObj in faces)
                {
                    if (!(fObj is IFace2 face)) continue;
                    ISurface surf = face.GetSurface() as ISurface;
                    if (surf == null) continue;

                    double area = face.GetArea();
                    string token = SwConnectionManager.ComputeFaceToken(face, bIdx);

                    if (wantPlanes && surf.IsPlane())
                    {
                        if (!AssemblyGeometryHelper.TryGetPlanarFaceNormalAndPoint(face, surf, out double[] nLocal, out double[] pLocal))
                            continue;
                        double[] bb = face.GetBox() as double[];
                        planarFaces.Add(new Dictionary<string, object>
                        {
                            { "token", token },
                            { "body_index", bIdx },
                            { "area_m2", area },
                            { "normal", new[] { Math.Round(nLocal[0], 4), Math.Round(nLocal[1], 4), Math.Round(nLocal[2], 4) } },
                            { "point", new[]
                                {
                                    Math.Round(ShopCatalogService.FromMeters(pLocal[0], units), 4),
                                    Math.Round(ShopCatalogService.FromMeters(pLocal[1], units), 4),
                                    Math.Round(ShopCatalogService.FromMeters(pLocal[2], units), 4)
                                }
                            },
                            { "bounds_span", bb != null && bb.Length >= 6 ? new[]
                                {
                                    Math.Round(ShopCatalogService.FromMeters(bb[3] - bb[0], units), 4),
                                    Math.Round(ShopCatalogService.FromMeters(bb[4] - bb[1], units), 4),
                                    Math.Round(ShopCatalogService.FromMeters(bb[5] - bb[2], units), 4)
                                } : null
                            }
                        });
                    }
                    else if (wantHoles && surf.IsCylinder())
                    {
                        double[] cp = surf.CylinderParams as double[];
                        double[] bb = face.GetBox() as double[];
                        if (cp != null && cp.Length >= 7)
                        {
                            // CylinderParams: [ox, oy, oz, ax, ay, az, radius]
                            double radius = cp[6];
                            bool isHole = face.FaceInSurfaceSense(); // concave inner face typically has FaceInSurfaceSense == true
                            double ax = cp[3], ay = cp[4], az = cp[5];
                            double span = 0.0;
                            if (bb != null && bb.Length >= 6)
                            {
                                double dx = bb[3] - bb[0], dy = bb[4] - bb[1], dz = bb[5] - bb[2];
                                span = Math.Abs(dx * ax) + Math.Abs(dy * ay) + Math.Abs(dz * az);
                            }

                            if (isHole)
                            {
                                cylHoles.Add(new CylHoleInfo
                                {
                                    Token = token,
                                    DiameterM = 2.0 * radius,
                                    DepthM = span,
                                    OriginM = new[] { cp[0], cp[1], cp[2] },
                                    Axis = new[] { ax, ay, az },
                                    IsThroughOrOpen = true
                                });
                            }
                            else
                            {
                                cylBosses.Add(new
                                {
                                    token = token,
                                    body_index = bIdx,
                                    diameter = Math.Round(ShopCatalogService.FromMeters(2.0 * radius, units), 4),
                                    height = Math.Round(ShopCatalogService.FromMeters(span, units), 4),
                                    axis = new[] { Math.Round(ax, 4), Math.Round(ay, 4), Math.Round(az, 4) },
                                    origin = new[]
                                    {
                                        Math.Round(ShopCatalogService.FromMeters(cp[0], units), 4),
                                        Math.Round(ShopCatalogService.FromMeters(cp[1], units), 4),
                                        Math.Round(ShopCatalogService.FromMeters(cp[2], units), 4)
                                    }
                                });
                            }
                        }
                    }
                }
            }

            planarFaces.Sort((a, b) => JsonHelper.GetDouble(b, "area_m2").CompareTo(JsonHelper.GetDouble(a, "area_m2")));
            if (planarFaces.Count > maxFaces)
                planarFaces = planarFaces.GetRange(0, maxFaces);

            List<object> holesOut = new List<object>();
            foreach (CylHoleInfo h in cylHoles)
            {
                holesOut.Add(new
                {
                    token = h.Token,
                    diameter = Math.Round(ShopCatalogService.FromMeters(h.DiameterM, units), 4),
                    depth = Math.Round(ShopCatalogService.FromMeters(h.DepthM, units), 4),
                    axis = new[] { Math.Round(h.Axis[0], 4), Math.Round(h.Axis[1], 4), Math.Round(h.Axis[2], 4) },
                    origin = new[]
                    {
                        Math.Round(ShopCatalogService.FromMeters(h.OriginM[0], units), 4),
                        Math.Round(ShopCatalogService.FromMeters(h.OriginM[1], units), 4),
                        Math.Round(ShopCatalogService.FromMeters(h.OriginM[2], units), 4)
                    }
                });
            }

            // Group holes with similar diameter & parallel axis
            List<object> holePatterns = new List<object>();
            bool[] used = new bool[cylHoles.Count];
            for (int i = 0; i < cylHoles.Count; i++)
            {
                if (used[i]) continue;
                List<CylHoleInfo> grp = new List<CylHoleInfo> { cylHoles[i] };
                used[i] = true;
                for (int j = i + 1; j < cylHoles.Count; j++)
                {
                    if (used[j]) continue;
                    double dDiff = Math.Abs(cylHoles[i].DiameterM - cylHoles[j].DiameterM);
                    double dot = Math.Abs(cylHoles[i].Axis[0] * cylHoles[j].Axis[0] + cylHoles[i].Axis[1] * cylHoles[j].Axis[1] + cylHoles[i].Axis[2] * cylHoles[j].Axis[2]);
                    if (dDiff < 0.0002 && dot > 0.99)
                    {
                        grp.Add(cylHoles[j]);
                        used[j] = true;
                    }
                }
                if (grp.Count >= 2)
                {
                    holePatterns.Add(new
                    {
                        count = grp.Count,
                        diameter = Math.Round(ShopCatalogService.FromMeters(grp[0].DiameterM, units), 4),
                        axis = new[] { Math.Round(grp[0].Axis[0], 4), Math.Round(grp[0].Axis[1], 4), Math.Round(grp[0].Axis[2], 4) },
                        tokens = grp.ConvertAll(x => x.Token)
                    });
                }
            }

            object pcbSummary = null;
            if (pcbMode)
            {
                double[] pBox = part.GetPartBox(true) as double[];
                // Heuristic: largest planar face pair ~1.6mm apart in KiCad STEP indicates FR4 substrate body
                pcbSummary = new
                {
                    body_count = bodies.Length,
                    total_envelope = pBox != null && pBox.Length >= 6 ? new
                    {
                        x = Math.Round(ShopCatalogService.FromMeters(pBox[3] - pBox[0], units), 3),
                        y = Math.Round(ShopCatalogService.FromMeters(pBox[4] - pBox[1], units), 3),
                        z = Math.Round(ShopCatalogService.FromMeters(pBox[5] - pBox[2], units), 3)
                    } : null,
                    detected_mounting_hole_patterns = holePatterns,
                    largest_planar_faces = planarFaces.Count > 4 ? planarFaces.GetRange(0, 4) : planarFaces
                };
            }

            return McpToolResult.JsonText(new
            {
                units = units,
                body_count = bodies.Length,
                planar_datums = planarFaces,
                cylindrical_holes = holesOut,
                cylindrical_bosses = cylBosses,
                hole_patterns = holePatterns,
                pcb_summary = pcbSummary
            });
        }

        private static McpToolResult HandleMeasureClearance(SwConnectionManager swMgr, Dictionary<string, object> args)
        {
            IModelDoc2 doc = swMgr.RequireModel(JsonHelper.GetString(args, "target_doc"));
            string units = SwConnectionManager.ResolveUnits(doc, JsonHelper.GetString(args, "units"));
            string entA = JsonHelper.GetString(args, "entity_a");
            string entB = JsonHelper.GetString(args, "entity_b");

            if (!string.IsNullOrWhiteSpace(entA) && !string.IsNullOrWhiteSpace(entB))
            {
                doc.ClearSelection2(true);
                object objA = ResolveDistanceEntity(doc, entA);
                if (objA == null)
                    return McpToolResult.Error("Could not select entity_a: " + entA);
                object objB = ResolveDistanceEntity(doc, entB, append: true);
                if (objB == null)
                    return McpToolResult.Error("Could not select entity_b: " + entB);

                object pt1 = null, pt2 = null;
                double distM = doc.ClosestDistance(objA, objB, out pt1, out pt2);
                doc.ClearSelection2(true);
                return McpToolResult.JsonText(new
                {
                    units = units,
                    minimum_distance = Math.Round(ShopCatalogService.FromMeters(distM, units), 4),
                    point_a = ScalePointArray(pt1 as double[], units),
                    point_b = ScalePointArray(pt2 as double[], units)
                });
            }

            if (doc is IAssemblyDoc assy)
            {
                List<string> includeComps = JsonHelper.GetStringList(args, "include_components");
                if (includeComps.Count == 0)
                {
                    return McpToolResult.Error(
                        "Unfiltered all-to-all assembly interference detection is disabled to avoid >3m0s timeouts. Pass 'include_components' (2 to 40 component names/paths/wildcards) for fast subset interference detection, or pass 'entity_a' and 'entity_b' for ClosestDistance.");
                }

                List<IComponent2> resolvedComps = AssemblyGeometryHelper.ResolveComponentsMatching(assy, includeComps);
                if (resolvedComps.Count < 2)
                {
                    return McpToolResult.Error(string.Format(
                        "Subset interference detection requires at least 2 resolved components (matched {0} from include_components).",
                        resolvedComps.Count));
                }
                if (resolvedComps.Count > 40)
                {
                    return McpToolResult.Error(string.Format(
                        "Subset interference detection is capped at 40 components per call to keep checks fast (matched {0}). Split into targeted component subsets.",
                        resolvedComps.Count));
                }

                ISldWorks app = swMgr.GetApp();
                IMathUtility mu = app.GetMathUtility() as IMathUtility;
                bool excludeThread = JsonHelper.GetBool(args, "exclude_expected_thread_engagements", false);
                bool excludeSameSubassembly = JsonHelper.GetBool(args, "exclude_same_subassembly_pairs", true);
                bool groupByPair = JsonHelper.GetBool(args, "group_by_pair", true);
                List<Tuple<string, string>> ignorePairs = ParseIgnorePairs(JsonHelper.GetList(args, "ignore_pairs"));

                Dictionary<string, object> payload = RunSubsetInterferenceDetection(
                    doc, assy, mu, resolvedComps, ignorePairs, excludeThread, groupByPair, excludeSameSubassembly);
                return McpToolResult.JsonText(payload);
            }

            return McpToolResult.Error("Pass entity_a and entity_b for distance measurement, or run on an active Assembly with include_components for subset interference detection.");
        }

        private static object ResolveDistanceEntity(IModelDoc2 doc, string selector, bool append = false)
        {
            if (doc is IAssemblyDoc assy)
            {
                if (AssemblyGeometryHelper.TrySelectAssemblyTarget(doc, assy, selector, append, 0))
                {
                    ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                    return selMgr != null ? selMgr.GetSelectedObject6(selMgr.GetSelectedObjectCount2(-1), -1) : null;
                }
                IComponent2 comp = AssemblyGeometryHelper.ResolveComponentByPath(assy, selector);
                if (comp != null && comp.Select4(append, null, false))
                    return comp;
                return null;
            }
            if (SwConnectionManager.TrySelectByToken(doc, selector, append, 0))
            {
                ISelectionMgr selMgr = doc.SelectionManager as ISelectionMgr;
                return selMgr != null ? selMgr.GetSelectedObject6(selMgr.GetSelectedObjectCount2(-1), -1) : null;
            }
            return null;
        }

        private static double[] ScalePointArray(double[] ptM, string units)
        {
            if (ptM == null || ptM.Length < 3) return ptM;
            return new[]
            {
                Math.Round(ShopCatalogService.FromMeters(ptM[0], units), 4),
                Math.Round(ShopCatalogService.FromMeters(ptM[1], units), 4),
                Math.Round(ShopCatalogService.FromMeters(ptM[2], units), 4)
            };
        }

        internal static List<Tuple<string, string>> ParseIgnorePairs(List<object> rawPairs)
        {
            List<Tuple<string, string>> list = new List<Tuple<string, string>>();
            if (rawPairs == null) return list;
            foreach (object pObj in rawPairs)
            {
                Dictionary<string, object> pd = pObj as Dictionary<string, object> ?? JsonHelper.DeserializeDict(JsonHelper.Serialize(pObj));
                if (pd == null) continue;
                string a = JsonHelper.GetString(pd, "component_a");
                string b = JsonHelper.GetString(pd, "component_b");
                if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                    list.Add(Tuple.Create(a.Trim(), b.Trim()));
            }
            return list;
        }

        private static bool MatchesIgnorePairs(string compA, string compB, string baseA, string baseB, IList<Tuple<string, string>> ignorePairs)
        {
            if (ignorePairs == null || ignorePairs.Count == 0) return false;
            string leafA = compA.Contains("/") ? compA.Substring(compA.LastIndexOf('/') + 1) : compA;
            string leafB = compB.Contains("/") ? compB.Substring(compB.LastIndexOf('/') + 1) : compB;
            string leafBaseA = StripInstanceSuffix(leafA);
            string leafBaseB = StripInstanceSuffix(leafB);

            foreach (Tuple<string, string> pair in ignorePairs)
            {
                bool aHits1 = MatchesPairSide(pair.Item1, compA, baseA, leafA, leafBaseA);
                bool bHits2 = MatchesPairSide(pair.Item2, compB, baseB, leafB, leafBaseB);
                if (aHits1 && bHits2) return true;

                bool aHits2 = MatchesPairSide(pair.Item2, compA, baseA, leafA, leafBaseA);
                bool bHits1 = MatchesPairSide(pair.Item1, compB, baseB, leafB, leafBaseB);
                if (aHits2 && bHits1) return true;
            }
            return false;
        }

        private static bool MatchesPairSide(string pat, string fullName, string baseName, string leafName, string leafBase)
        {
            return string.Equals(pat, fullName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pat, baseName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pat, leafName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pat, leafBase, StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> RunSubsetInterferenceDetection(
            IModelDoc2 doc,
            IAssemblyDoc assy,
            IMathUtility mu,
            IList<IComponent2> resolvedComps,
            IList<Tuple<string, string>> ignorePairs,
            bool excludeThread,
            bool groupByPair,
            bool excludeSameSubassembly = true)
        {
            doc.ClearSelection2(true);
            int selCount = 0;
            List<string> checkedNames = new List<string>();
            HashSet<string> distinctTopRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IComponent2 c in resolvedComps)
            {
                if (c != null && !c.IsSuppressed() && c.Select4(selCount > 0, null, false))
                {
                    selCount++;
                    string cn = c.Name2 ?? "";
                    checkedNames.Add(cn);
                    int slash = cn.IndexOf('/');
                    distinctTopRoots.Add(slash > 0 ? cn.Substring(0, slash) : cn);
                }
            }

            IInterferenceDetectionMgr mgr = assy.InterferenceDetectionManager;
            mgr.TreatCoincidenceAsInterference = false;
            mgr.TreatSubAssembliesAsComponents = false;
            mgr.IncludeMultibodyPartInterferences = true;

            AssemblyJointAnalyzer.InterferenceHoleCache holeCache = new AssemblyJointAnalyzer.InterferenceHoleCache();
            List<object> items = new List<object>();
            List<object> unexpectedDetails = new List<object>();
            Dictionary<string, ClearanceGroupBucket> groupBuckets = new Dictionary<string, ClearanceGroupBucket>(StringComparer.OrdinalIgnoreCase);

            int rawCount = 0;
            int ignoredPairCount = 0;
            int skippedInternalSubassemblyCount = 0;
            int expectedThreadCount = 0;
            int unexpectedCollisionCount = 0;

            try
            {
                rawCount = mgr.GetInterferenceCount();
                object[] interferences = mgr.GetInterferences() as object[];

                if (interferences != null)
                {
                    foreach (object iObj in interferences)
                    {
                        if (!(iObj is IInterference inf)) continue;
                        if (excludeSameSubassembly && distinctTopRoots.Count >= 2)
                        {
                            object[] infComps = inf.Components as object[];
                            if (infComps != null && infComps.Length >= 2 &&
                                infComps[0] is IComponent2 icA && infComps[1] is IComponent2 icB)
                            {
                                string na = icA.Name2 ?? "";
                                string nb = icB.Name2 ?? "";
                                int sa = na.IndexOf('/');
                                int sb = nb.IndexOf('/');
                                if (sa > 0 && sb > 0 &&
                                    string.Equals(na.Substring(0, sa), nb.Substring(0, sb), StringComparison.OrdinalIgnoreCase))
                                {
                                    skippedInternalSubassemblyCount++;
                                    continue;
                                }
                            }
                        }

                        bool isExpected = AssemblyJointAnalyzer.IsExpectedThreadEngagement(
                            assy, inf, mu, holeCache, out string compA, out string compB, out double volMm3, out string reason);

                        string baseA = StripInstanceSuffix(compA);
                        string baseB = StripInstanceSuffix(compB);
                        if (MatchesIgnorePairs(compA, compB, baseA, baseB, ignorePairs))
                        {
                            ignoredPairCount++;
                            continue;
                        }

                        if (isExpected)
                            expectedThreadCount++;
                        else
                            unexpectedCollisionCount++;

                        string classification = isExpected ? "expected_thread_engagement" : "unexpected_collision";
                        if (!excludeThread || !isExpected)
                        {
                            if (string.Compare(baseA, baseB, StringComparison.OrdinalIgnoreCase) > 0)
                            {
                                string tmp = baseA; baseA = baseB; baseB = tmp;
                            }
                            string key = baseA + "|" + baseB + "|" + classification + "|" + reason;
                            if (!groupBuckets.TryGetValue(key, out ClearanceGroupBucket bucket))
                            {
                                bucket = new ClearanceGroupBucket
                                {
                                    ComponentA = baseA,
                                    ComponentB = baseB,
                                    Classification = classification,
                                    Reason = reason
                                };
                                groupBuckets[key] = bucket;
                            }
                            bucket.Count++;
                            bucket.TotalVolumeMm3 += volMm3;
                            if (volMm3 > bucket.MaxVolumeMm3) bucket.MaxVolumeMm3 = volMm3;
                        }

                        if (excludeThread && isExpected)
                            continue;

                        object entry = new
                        {
                            component_a = compA,
                            component_b = compB,
                            volume_mm3 = volMm3,
                            volume_m3 = inf.Volume,
                            classification = classification,
                            reason = reason,
                            is_fastener = inf.IsFastener,
                            is_possible_interference = inf.IsPossibleInterference
                        };

                        if (!isExpected && unexpectedDetails.Count < 50)
                            unexpectedDetails.Add(entry);
                        if (!groupByPair)
                            items.Add(entry);
                    }
                }
            }
            finally
            {
                try { mgr.Done(); } catch { }
                try { doc.ClearSelection2(true); } catch { }
            }

            List<ClearanceGroupBucket> sortedBuckets = new List<ClearanceGroupBucket>(groupBuckets.Values);
            sortedBuckets.Sort((a, b) =>
            {
                int ua = a.Classification == "unexpected_collision" ? 0 : 1;
                int ub = b.Classification == "unexpected_collision" ? 0 : 1;
                int cmp = ua.CompareTo(ub);
                return cmp != 0 ? cmp : b.Count.CompareTo(a.Count);
            });

            List<object> groupedSummary = new List<object>();
            foreach (ClearanceGroupBucket b in sortedBuckets)
            {
                groupedSummary.Add(new
                {
                    component_a = b.ComponentA,
                    component_b = b.ComponentB,
                    classification = b.Classification,
                    reason = b.Reason,
                    count = b.Count,
                    max_volume_mm3 = Math.Round(b.MaxVolumeMm3, 4),
                    total_volume_mm3 = Math.Round(b.TotalVolumeMm3, 4)
                });
            }

            Dictionary<string, object> res = new Dictionary<string, object>
            {
                { "mode", "subset_interference_detection" },
                { "checked_components_count", checkedNames.Count },
                { "checked_components", checkedNames },
                { "total_interference_count", rawCount },
                { "ignored_pair_count", ignoredPairCount },
                { "skipped_internal_subassembly_count", skippedInternalSubassemblyCount },
                { "unexpected_collision_count", unexpectedCollisionCount },
                { "expected_thread_engagement_count", expectedThreadCount },
                { "grouped_pair_count", groupedSummary.Count },
                { "grouped_summary", groupedSummary }
            };
            if (groupByPair)
            {
                res["unexpected_collisions"] = unexpectedDetails;
            }
            else
            {
                res["returned_count"] = items.Count;
                res["interferences"] = items;
            }
            return res;
        }

        private class ClearanceGroupBucket
        {
            public string ComponentA;
            public string ComponentB;
            public string Classification;
            public string Reason;
            public int Count;
            public double MaxVolumeMm3;
            public double TotalVolumeMm3;
        }

        private static string StripInstanceSuffix(string compName)
        {
            if (string.IsNullOrWhiteSpace(compName)) return "Unknown";
            return Regex.Replace(compName.Trim(), @"-\d+$", "");
        }
    }
}
