using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksMcpServer.Core
{
    /// <summary>
    /// Fast, folder-safe SOLIDWORKS assembly mate health auditor.
    /// Detects all RED (error) and YELLOW (warning) mates across top-level assemblies and
    /// loaded resolved subassemblies, pinpoints dangling mate entities, and captures non-mate
    /// rebuild errors via IModelDocExtension.GetWhatsWrong.
    /// </summary>
    public static class AssemblyMateHealthAuditor
    {
        public class BadMateInfo
        {
            public string Name { get; set; }
            public string Assembly { get; set; }
            public string Severity { get; set; } // "RED" or "YELLOW"
            public string MateType { get; set; }
            public string Folder { get; set; }
            public bool Suppressed { get; set; }
            public int ErrorCode { get; set; }
            public string ErrorName { get; set; }
            public List<object> Entities { get; set; } = new List<object>();
            public List<string> Components { get; set; } = new List<string>();
            public string SuggestedFix { get; set; }
        }

        public class AuditReport
        {
            public string Document { get; set; }
            public bool IsAssembly { get; set; }
            public bool Clean { get; set; }
            public string Status { get; set; }
            public int RedMateCount { get; set; }
            public int YellowMateCount { get; set; }
            public int HealthyMateCount { get; set; }
            public int SuppressedMateCount { get; set; }
            public int OtherRedFeatureCount { get; set; }
            public int OtherYellowFeatureCount { get; set; }
            public List<string> AssembliesChecked { get; set; } = new List<string>();
            public List<BadMateInfo> BadMates { get; set; } = new List<BadMateInfo>();
            public List<string> RootBadMateNames { get; set; } = new List<string>();
            public Dictionary<string, List<string>> AffectedComponents { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public List<object> OtherFeatureIssues { get; set; } = new List<object>();
            public string Summary { get; set; }
        }

        public static AuditReport AuditDocument(
            IModelDoc2 rootDoc,
            bool includeSubassemblies = true,
            bool includeFeatureErrors = true,
            bool includeSuppressed = false,
            bool rebuildFirst = false)
        {
            AuditReport report = new AuditReport
            {
                Document = rootDoc != null ? rootDoc.GetTitle() : string.Empty,
                IsAssembly = rootDoc is IAssemblyDoc
            };

            if (rootDoc == null)
            {
                report.Clean = true;
                report.Status = "PASS";
                report.Summary = "No document provided.";
                return report;
            }

            if (rebuildFirst)
            {
                try { rootDoc.EditRebuild3(); } catch { }
            }

            HashSet<string> visitedDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<IModelDoc2> queue = new Queue<IModelDoc2>();
            queue.Enqueue(rootDoc);

            bool isRoot = true;
            while (queue.Count > 0)
            {
                IModelDoc2 curDoc = queue.Dequeue();
                if (curDoc == null) continue;

                string docKey = GetDocKey(curDoc);
                if (!visitedDocs.Add(docKey)) continue;

                string docLabel = curDoc.GetTitle() ?? Path.GetFileName(curDoc.GetPathName() ?? "");
                if (curDoc is IAssemblyDoc curAssy)
                {
                    report.AssembliesChecked.Add(docLabel);
                    AuditSingleAssembly(curDoc, docLabel, isRoot, includeFeatureErrors, includeSuppressed, report);

                    if (includeSubassemblies)
                    {
                        try
                        {
                            object[] comps = curAssy.GetComponents(true) as object[];
                            if (comps != null)
                            {
                                foreach (object cObj in comps)
                                {
                                    if (!(cObj is IComponent2 comp)) continue;
                                    try
                                    {
                                        if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                                            continue;
                                        IModelDoc2 subDoc = comp.GetModelDoc2() as IModelDoc2;
                                        if (subDoc is IAssemblyDoc)
                                        {
                                            string subKey = GetDocKey(subDoc);
                                            if (!visitedDocs.Contains(subKey))
                                                queue.Enqueue(subDoc);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
                else if (isRoot && includeFeatureErrors)
                {
                    // Non-assembly document: still inspect GetWhatsWrong feature errors
                    CollectWhatsWrongNonMates(curDoc, docLabel, new HashSet<string>(StringComparer.OrdinalIgnoreCase), report);
                }

                isRoot = false;
            }

            // Sort bad mates: RED before YELLOW, then root assembly first, then name
            report.BadMates.Sort((a, b) =>
            {
                int sevCmp = string.Equals(a.Severity, b.Severity, StringComparison.Ordinal)
                    ? 0
                    : (a.Severity == "RED" ? -1 : 1);
                if (sevCmp != 0) return sevCmp;
                int docCmp = string.Compare(a.Assembly, b.Assembly, StringComparison.OrdinalIgnoreCase);
                if (docCmp != 0) return docCmp;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            report.Clean = report.RedMateCount == 0 && report.YellowMateCount == 0;
            report.Status = report.Clean ? "PASS" : "FAIL";

            if (!report.IsAssembly)
            {
                report.Summary = string.Format(CultureInfo.InvariantCulture,
                    "Document '{0}' is not an assembly (other feature issues: {1} red, {2} yellow).",
                    report.Document, report.OtherRedFeatureCount, report.OtherYellowFeatureCount);
            }
            else if (report.Clean)
            {
                report.Summary = string.Format(CultureInfo.InvariantCulture,
                    "PASS: 0 red and 0 yellow mates across {0} assembly(ies) ({1} healthy, {2} suppressed{3}).",
                    report.AssembliesChecked.Count,
                    report.HealthyMateCount,
                    report.SuppressedMateCount,
                    (report.OtherRedFeatureCount + report.OtherYellowFeatureCount) > 0
                        ? string.Format(CultureInfo.InvariantCulture, "; note {0} red / {1} yellow non-mate feature issues", report.OtherRedFeatureCount, report.OtherYellowFeatureCount)
                        : "");
            }
            else
            {
                report.Summary = string.Format(CultureInfo.InvariantCulture,
                    "FAIL: Found {0} RED (error) and {1} YELLOW (warning) mate(s) across {2} assembly(ies) affecting {3} component(s). Fix or delete & re-mate all bad mates before saving/finishing.",
                    report.RedMateCount,
                    report.YellowMateCount,
                    report.AssembliesChecked.Count,
                    report.AffectedComponents.Count);
            }

            return report;
        }

        public static object ToJsonPayload(AuditReport report)
        {
            List<object> badOut = new List<object>(report.BadMates.Count);
            foreach (BadMateInfo bm in report.BadMates)
            {
                badOut.Add(new
                {
                    name = bm.Name,
                    assembly = bm.Assembly,
                    severity = bm.Severity,
                    mate_type = bm.MateType,
                    folder = bm.Folder,
                    suppressed = bm.Suppressed,
                    error_code = bm.ErrorCode,
                    error_name = bm.ErrorName,
                    components = bm.Components,
                    entities = bm.Entities,
                    suggested_fix = bm.SuggestedFix
                });
            }

            return new
            {
                document = report.Document,
                is_assembly = report.IsAssembly,
                clean = report.Clean,
                status = report.Status,
                red_mate_count = report.RedMateCount,
                yellow_mate_count = report.YellowMateCount,
                healthy_mate_count = report.HealthyMateCount,
                suppressed_mate_count = report.SuppressedMateCount,
                other_red_feature_count = report.OtherRedFeatureCount,
                other_yellow_feature_count = report.OtherYellowFeatureCount,
                assemblies_checked = report.AssembliesChecked,
                bad_mate_names = report.RootBadMateNames,
                affected_components = report.AffectedComponents,
                bad_mates = badOut,
                other_feature_issues = report.OtherFeatureIssues,
                summary = report.Summary
            };
        }

        /// <summary>
        /// Compact mate health summary automatically attached to assembly mutation/save tools.
        /// By default runs a &lt;5ms top-level GetWhatsWrong check so intermediate edits do not
        /// traverse hundreds of healthy subassembly mates; pass includeSubassemblies: true
        /// for a full feature-tree + subassembly audit.
        /// Returns null for non-assembly documents.
        /// </summary>
        public static object BuildCompactSummary(IModelDoc2 doc, int maxBadMates = 15, bool includeSubassemblies = false)
        {
            if (!(doc is IAssemblyDoc))
                return null;

            if (!includeSubassemblies)
                return BuildTopLevelFastSummary(doc, maxBadMates);

            AuditReport rep = AuditDocument(
                doc,
                includeSubassemblies: true,
                includeFeatureErrors: false,
                includeSuppressed: false,
                rebuildFirst: false);

            List<object> sample = new List<object>();
            for (int i = 0; i < rep.BadMates.Count && i < maxBadMates; i++)
            {
                BadMateInfo bm = rep.BadMates[i];
                sample.Add(new
                {
                    name = bm.Name,
                    assembly = bm.Assembly,
                    severity = bm.Severity,
                    mate_type = bm.MateType,
                    error_name = bm.ErrorName,
                    components = bm.Components
                });
            }

            return new
            {
                clean = rep.Clean,
                status = rep.Status,
                mode = "full_audit",
                red_mate_count = rep.RedMateCount,
                yellow_mate_count = rep.YellowMateCount,
                healthy_mate_count = rep.HealthyMateCount,
                suppressed_mate_count = rep.SuppressedMateCount,
                bad_mates_truncated = rep.BadMates.Count > maxBadMates,
                bad_mates = sample,
                warning = rep.Clean
                    ? null
                    : string.Format(CultureInfo.InvariantCulture,
                        "ACTION REQUIRED: Assembly has {0} RED and {1} YELLOW mate(s). Run sw_audit_assembly_mates and fix every yellow/red mate before finishing.",
                        rep.RedMateCount, rep.YellowMateCount)
            };
        }

        private static object BuildTopLevelFastSummary(IModelDoc2 doc, int maxBadMates)
        {
            string docLabel = doc.GetTitle() ?? Path.GetFileName(doc.GetPathName() ?? "");
            int redCount = 0;
            int yellowCount = 0;
            int totalBadMates = 0;
            List<object> sample = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                object featObj, errObj, warnObj;
                if (doc.Extension != null && doc.Extension.GetWhatsWrong(out featObj, out errObj, out warnObj))
                {
                    object[] feats = featObj as object[];
                    int[] errs = errObj as int[];
                    bool[] warns = warnObj as bool[];
                    if (feats != null)
                    {
                        for (int i = 0; i < feats.Length; i++)
                        {
                            IFeature f = feats[i] as IFeature;
                            if (f == null) continue;
                            string sName = f.Name ?? string.Empty;
                            string sType = f.GetTypeName2() ?? string.Empty;
                            if (string.IsNullOrEmpty(sName) || !seen.Add(sName))
                                continue;
                            if (string.Equals(sType, "MateGroup", StringComparison.OrdinalIgnoreCase))
                                continue;

                            IMate2 mate = null;
                            if (!LooksLikeMateFeatureType(sType))
                            {
                                try { mate = f.GetSpecificFeature2() as IMate2; } catch { }
                                if (mate == null) continue;
                            }

                            bool suppressed = false;
                            try { suppressed = f.IsSuppressed(); } catch { }
                            if (suppressed) continue;

                            int ec = (errs != null && i < errs.Length) ? errs[i] : 1;
                            bool isW = (warns != null && i < warns.Length) && warns[i];
                            if (ec == 0)
                            {
                                try { ec = f.GetErrorCode2(out isW); } catch { }
                                if (ec == 0) ec = 1;
                            }

                            totalBadMates++;
                            if (isW) yellowCount++;
                            else redCount++;

                            if (sample.Count < maxBadMates)
                            {
                                if (mate == null)
                                {
                                    try { mate = f.GetSpecificFeature2() as IMate2; } catch { }
                                }
                                List<string> comps = new List<string>();
                                if (mate != null)
                                {
                                    int entCount = 0;
                                    try { entCount = mate.GetMateEntityCount(); } catch { }
                                    for (int j = 0; j < entCount; j++)
                                    {
                                        try
                                        {
                                            IMateEntity2 me = mate.MateEntity(j);
                                            string cn = me != null && me.ReferenceComponent != null ? me.ReferenceComponent.Name2 : null;
                                            if (!string.IsNullOrEmpty(cn) && !comps.Contains(cn))
                                                comps.Add(cn);
                                        }
                                        catch { }
                                    }
                                }
                                sample.Add(new
                                {
                                    name = sName,
                                    assembly = docLabel,
                                    severity = isW ? "YELLOW" : "RED",
                                    mate_type = ResolveMateTypeName(mate, sType),
                                    error_name = FormatFeatureErrorName(ec),
                                    components = comps
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            bool clean = (redCount == 0 && yellowCount == 0);
            return new
            {
                clean = clean,
                status = clean ? "PASS" : "FAIL",
                mode = "fast_top_level_whats_wrong",
                red_mate_count = redCount,
                yellow_mate_count = yellowCount,
                healthy_mate_count = (int?)null,
                suppressed_mate_count = (int?)null,
                bad_mates_truncated = totalBadMates > maxBadMates,
                bad_mates = sample,
                warning = clean
                    ? null
                    : string.Format(CultureInfo.InvariantCulture,
                        "ACTION REQUIRED: Top-level assembly has {0} RED and {1} YELLOW mate(s). Run sw_audit_assembly_mates and fix every yellow/red mate before finishing.",
                        redCount, yellowCount)
            };
        }

        private static void AuditSingleAssembly(
            IModelDoc2 doc,
            string docLabel,
            bool isRoot,
            bool includeFeatureErrors,
            bool includeSuppressed,
            AuditReport report)
        {
            HashSet<string> seenMates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Tuple<int, bool>> whatsWrongByFeat = BuildWhatsWrongMap(doc);

            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                string fType = feat.GetTypeName2() ?? string.Empty;
                if (string.Equals(fType, "MateGroup", StringComparison.OrdinalIgnoreCase))
                {
                    string groupName = string.IsNullOrWhiteSpace(feat.Name) ? "Mates" : feat.Name;
                    IFeature sub = feat.GetFirstSubFeature() as IFeature;
                    while (sub != null)
                    {
                        InspectMateOrFolder(sub, docLabel, groupName, isRoot, includeSuppressed, seenMates, whatsWrongByFeat, report);
                        sub = sub.GetNextSubFeature() as IFeature;
                    }
                }
                feat = feat.GetNextFeature() as IFeature;
            }

            if (includeFeatureErrors)
                CollectWhatsWrongNonMates(doc, docLabel, seenMates, report);
        }

        private static void InspectMateOrFolder(
            IFeature sub,
            string docLabel,
            string folderPath,
            bool isRoot,
            bool includeSuppressed,
            HashSet<string> seenMates,
            Dictionary<string, Tuple<int, bool>> whatsWrongByFeat,
            AuditReport report)
        {
            if (sub == null) return;
            string sType = sub.GetTypeName2() ?? string.Empty;
            string sName = sub.Name ?? string.Empty;

            if (sName.EndsWith("___EndTag___", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(sType, "FtrFolder", StringComparison.OrdinalIgnoreCase))
            {
                string nextFolder = string.IsNullOrWhiteSpace(sName) ? folderPath : (folderPath + "/" + sName);
                try
                {
                    IFeatureFolder ff = sub.GetSpecificFeature2() as IFeatureFolder;
                    object[] folderFeats = ff != null ? ff.GetFeatures() as object[] : null;
                    if (folderFeats != null)
                    {
                        foreach (object fObj in folderFeats)
                        {
                            if (fObj is IFeature child)
                                InspectMateOrFolder(child, docLabel, nextFolder, isRoot, includeSuppressed, seenMates, whatsWrongByFeat, report);
                        }
                    }
                }
                catch { }

                IFeature childSub = null;
                try { childSub = sub.GetFirstSubFeature() as IFeature; } catch { }
                while (childSub != null)
                {
                    InspectMateOrFolder(childSub, docLabel, nextFolder, isRoot, includeSuppressed, seenMates, whatsWrongByFeat, report);
                    try { childSub = childSub.GetNextSubFeature() as IFeature; } catch { break; }
                }
                return;
            }

            if (string.IsNullOrEmpty(sName) || !seenMates.Add(sName))
                return;

            IMate2 mate = null;
            bool knownMateType = LooksLikeMateFeatureType(sType);
            if (!knownMateType)
            {
                try { mate = sub.GetSpecificFeature2() as IMate2; } catch { }
                if (mate == null)
                    return;
            }

            bool suppressed = false;
            try { suppressed = sub.IsSuppressed(); } catch { }

            int errorCode = 0;
            bool isWarning = false;
            try { errorCode = sub.GetErrorCode2(out isWarning); } catch { }

            if (errorCode == 0 && whatsWrongByFeat.TryGetValue(sName, out Tuple<int, bool> ww))
            {
                errorCode = ww.Item1;
                isWarning = ww.Item2;
            }

            if (suppressed)
            {
                report.SuppressedMateCount++;
                if (!includeSuppressed)
                    return;
            }

            if (errorCode == 0)
            {
                if (!suppressed)
                    report.HealthyMateCount++;
                return;
            }

            if (mate == null)
            {
                try { mate = sub.GetSpecificFeature2() as IMate2; } catch { }
            }

            string severity = isWarning ? "YELLOW" : "RED";
            if (!suppressed)
            {
                if (isWarning) report.YellowMateCount++;
                else report.RedMateCount++;
            }

            BadMateInfo info = new BadMateInfo
            {
                Name = sName,
                Assembly = docLabel,
                Severity = severity,
                MateType = ResolveMateTypeName(mate, sType),
                Folder = folderPath,
                Suppressed = suppressed,
                ErrorCode = errorCode,
                ErrorName = FormatFeatureErrorName(errorCode),
                SuggestedFix = BuildSuggestedFix(errorCode, isWarning, mate)
            };

            if (mate != null)
            {
                int entCount = 0;
                try { entCount = mate.GetMateEntityCount(); } catch { }
                for (int i = 0; i < entCount; i++)
                {
                    try
                    {
                        IMateEntity2 me = mate.MateEntity(i);
                        if (me == null) continue;
                        string compName = null;
                        try { compName = me.ReferenceComponent != null ? me.ReferenceComponent.Name2 : null; } catch { }
                        int refType = (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_UNKNOWN;
                        try { refType = me.ReferenceType2; } catch { }
                        object refObj = null;
                        try { refObj = me.Reference; } catch { }

                        bool isDangling = refObj == null || refType == (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_UNKNOWN;
                        info.Entities.Add(new
                        {
                            index = i,
                            component = compName ?? "(missing/root)",
                            reference_type = FormatMateEntityRefType(refType),
                            is_dangling = isDangling
                        });

                        if (!string.IsNullOrEmpty(compName) && !info.Components.Contains(compName))
                            info.Components.Add(compName);
                    }
                    catch { }
                }
            }

            report.BadMates.Add(info);
            if (isRoot && !suppressed)
                report.RootBadMateNames.Add(sName);

            if (!suppressed)
            {
                string tag = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", sName, severity);
                if (info.Components.Count == 0)
                {
                    AddAffected(report.AffectedComponents, "(missing/dangling component)", tag);
                }
                else
                {
                    foreach (string cn in info.Components)
                        AddAffected(report.AffectedComponents, cn, tag);
                }
            }
        }

        private static Dictionary<string, Tuple<int, bool>> BuildWhatsWrongMap(IModelDoc2 doc)
        {
            Dictionary<string, Tuple<int, bool>> map = new Dictionary<string, Tuple<int, bool>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object featObj, errObj, warnObj;
                if (doc.Extension != null && doc.Extension.GetWhatsWrong(out featObj, out errObj, out warnObj))
                {
                    object[] feats = featObj as object[];
                    int[] errs = errObj as int[];
                    bool[] warns = warnObj as bool[];
                    if (feats != null)
                    {
                        for (int i = 0; i < feats.Length; i++)
                        {
                            IFeature f = feats[i] as IFeature;
                            if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                            int ec = (errs != null && i < errs.Length) ? errs[i] : 1;
                            bool isW = (warns != null && i < warns.Length) && warns[i];
                            if (!map.ContainsKey(f.Name))
                                map[f.Name] = Tuple.Create(ec, isW);
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private static void CollectWhatsWrongNonMates(
            IModelDoc2 doc,
            string docLabel,
            HashSet<string> seenMates,
            AuditReport report)
        {
            try
            {
                object featObj, errObj, warnObj;
                if (doc.Extension == null || !doc.Extension.GetWhatsWrong(out featObj, out errObj, out warnObj))
                    return;

                object[] feats = featObj as object[];
                int[] errs = errObj as int[];
                bool[] warns = warnObj as bool[];
                if (feats == null) return;

                for (int i = 0; i < feats.Length; i++)
                {
                    IFeature f = feats[i] as IFeature;
                    if (f == null) continue;
                    string fname = f.Name ?? string.Empty;
                    string ftype = f.GetTypeName2() ?? string.Empty;
                    if (seenMates.Contains(fname)) continue;
                    if (string.Equals(ftype, "MateGroup", StringComparison.OrdinalIgnoreCase)) continue;

                    int ec = (errs != null && i < errs.Length) ? errs[i] : 0;
                    bool isW = (warns != null && i < warns.Length) && warns[i];
                    if (isW) report.OtherYellowFeatureCount++;
                    else report.OtherRedFeatureCount++;

                    if (report.OtherFeatureIssues.Count < 30)
                    {
                        report.OtherFeatureIssues.Add(new
                        {
                            document = docLabel,
                            feature = fname,
                            feature_type = ftype,
                            severity = isW ? "YELLOW" : "RED",
                            error_code = ec,
                            error_name = FormatFeatureErrorName(ec)
                        });
                    }
                }
            }
            catch { }
        }

        private static bool LooksLikeMateFeatureType(string fType)
        {
            if (string.IsNullOrEmpty(fType)) return false;
            return fType.IndexOf("Mate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Coincident", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Concentric", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Angle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Parallel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Perpendicular", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Tangent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Symmetric", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Gear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Rack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Cam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Slot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Hinge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Screw", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("LinearCoupler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("ProfileCenter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fType.IndexOf("InPlace", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveMateTypeName(IMate2 mate, string featureTypeName)
        {
            if (mate != null)
            {
                try
                {
                    int t = mate.Type;
                    if (Enum.IsDefined(typeof(swMateType_e), t))
                    {
                        string raw = ((swMateType_e)t).ToString();
                        if (raw.StartsWith("swMate", StringComparison.Ordinal))
                            raw = raw.Substring("swMate".Length);
                        return raw;
                    }
                }
                catch { }
            }
            return string.IsNullOrWhiteSpace(featureTypeName) ? "Unknown" : featureTypeName;
        }

        private static string FormatFeatureErrorName(int errorCode)
        {
            if (Enum.IsDefined(typeof(swFeatureError_e), errorCode))
                return ((swFeatureError_e)errorCode).ToString();
            return "ErrorCode_" + errorCode.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatMateEntityRefType(int refType)
        {
            if (Enum.IsDefined(typeof(swMateEntity2ReferenceType_e), refType))
            {
                string raw = ((swMateEntity2ReferenceType_e)refType).ToString();
                const string prefix = "swMateEntity2ReferenceType_";
                if (raw.StartsWith(prefix, StringComparison.Ordinal))
                    return raw.Substring(prefix.Length);
                return raw;
            }
            return "Unknown_" + refType.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildSuggestedFix(int errorCode, bool isWarning, IMate2 mate)
        {
            switch ((swFeatureError_e)errorCode)
            {
                case swFeatureError_e.swFeatureErrorMateDanglingGeometry:
                case swFeatureError_e.swFeatureErrorMateInvalidFace:
                case swFeatureError_e.swFeatureErrorMateInvalidEdge:
                case swFeatureError_e.swFeatureErrorMateInvalidEntity:
                case swFeatureError_e.swFeatureErrorMateEntityFailed:
                    return "Dangling/missing reference entity: delete this mate via sw_manage_assembly_components(action='delete_mates') and re-mate to surviving geometry.";
                case swFeatureError_e.swFeatureErrorMateOverdefined:
                    return "Over-defined assembly: remove redundant/conflicting mate, unfix an unintentionally fixed component, or unlock Concentric rotation.";
                case swFeatureError_e.swFeatureErrorMateBroken:
                    return "Unsatisfied mate: verify Distance/Angle flip sign, alignment (Aligned vs Anti-Aligned), or conflicting LimitDistance/Fixed constraints.";
                case swFeatureError_e.swFeatureErrorMateIlldefined:
                    return "Ill-defined mate: check that both mate entities exist and are geometrically compatible for this mate type.";
                default:
                    return isWarning
                        ? "Warning (YELLOW) mate: inspect entities/alignment and resolve or delete & re-mate until mate_health.clean is true."
                        : "Error (RED) mate: inspect entities/conflicts and repair or delete & re-mate until mate_health.clean is true.";
            }
        }

        private static void AddAffected(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out List<string> list))
            {
                list = new List<string>();
                map[key] = list;
            }
            if (!list.Contains(value))
                list.Add(value);
        }

        private static string GetDocKey(IModelDoc2 doc)
        {
            string p = doc.GetPathName() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(p)) return p;
            return doc.GetTitle() ?? string.Empty;
        }
    }
}
