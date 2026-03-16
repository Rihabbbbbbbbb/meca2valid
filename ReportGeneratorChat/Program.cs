using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

string usage = "Usage: ReportGeneratorChat <validation-json> [output-json] [--brief]";
if (args.Length == 0)
{
    Console.WriteLine(usage);
    return;
}

var inputPath = args[0];
var outputPath = args.Length > 1 && !args[1].Equals("--brief", StringComparison.OrdinalIgnoreCase) ? args[1] : Path.Combine("output", "chat-report.json");
var brief = args.Any(a => a.Equals("--brief", StringComparison.OrdinalIgnoreCase));
Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "output");
if (!File.Exists(inputPath))
{
    Console.WriteLine($"Input file not found: {inputPath}");
    return;
}

using var srcStream = File.OpenRead(inputPath);
using var doc = JsonDocument.Parse(srcStream);
var root = doc.RootElement;

var chat = new Dictionary<string, object?>();

// Metadata
if (root.TryGetProperty("Metadata", out var meta))
{
    var md = new Dictionary<string, object?>();
    foreach (var prop in new[] { "Tool", "Version", "StartedAtUtc", "ValidatedAtUtc", "DurationMs", "TemplatePath", "UserDocumentPath" })
    {
        if (meta.TryGetProperty(prop, out var v)) md[prop] = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }
    chat["metadata"] = md;
}

// Scores & summary
var summary = new Dictionary<string, object?>();
if (root.TryGetProperty("Scores", out var scores))
{
    void AddScore(string k, string outKey)
    {
        if (scores.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null)
        {
            if (v.ValueKind == JsonValueKind.Number) summary[outKey] = v.GetDouble();
            else summary[outKey] = v.GetString();
        }
    }
    AddScore("FinalScore", "finalScore");
    AddScore("PivotCoveragePercent", "pivotCoverage");
    AddScore("SubtitleRecallPercent", "subtitleRecall");
    AddScore("SubtitlePrecisionPercent", "subtitlePrecision");
    AddScore("OrderScorePercent", "orderScore");
    AddScore("ConfidenceBand", "confidenceBand");
}
chat["summary"] = summary;

// Policy compliance (prefer unified fields; fall back to older fields)
if (root.TryGetProperty("PolicyCompliance", out var policy))
{
    var pc = new Dictionary<string, object?>();
    // missing sections: prefer 'MissingSections' (new unified), otherwise merge old fields
    if (policy.TryGetProperty("MissingSections", out var missNew))
    {
        pc["missingSections"] = missNew.EnumerateArray().Select(e => e.GetString()).Where(s => s != null).ToArray();
    }
    else
    {
        var missing = new List<string>();
        if (policy.TryGetProperty("MissingMustSections", out var mm)) missing.AddRange(mm.EnumerateArray().Select(e => e.GetString()).Where(s => s != null)!);
        if (policy.TryGetProperty("MissingShouldSections", out var ms)) missing.AddRange(ms.EnumerateArray().Select(e => e.GetString()).Where(s => s != null)!);
        if (policy.TryGetProperty("MissingOptionalSections", out var mo)) missing.AddRange(mo.EnumerateArray().Select(e => e.GetString()).Where(s => s != null)!);
        pc["missingSections"] = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // total sections: prefer 'TotalSections' otherwise sum older totals
    if (policy.TryGetProperty("TotalSections", out var totalNew))
    {
        pc["totalSections"] = totalNew.GetInt32();
    }
    else
    {
        int total = 0;
        if (policy.TryGetProperty("TotalMustSections", out var tm)) total += tm.GetInt32();
        if (policy.TryGetProperty("TotalShouldSections", out var ts)) total += ts.GetInt32();
        if (policy.TryGetProperty("TotalOptionalSections", out var to)) total += to.GetInt32();
        pc["totalSections"] = total;
    }

    chat["policyCompliance"] = pc;
}

// Quality gates and empty sections
if (root.TryGetProperty("QualityGates", out var qg))
{
    var q = new Dictionary<string, object?>();
    q["requiresNotApplicableForEmptySections"] = qg.TryGetProperty("RequiresNotApplicableForEmptySections", out var r) ? r.GetBoolean() : (bool?)null;
    q["redTextDetected"] = qg.TryGetProperty("RedTextDetected", out var rt) ? rt.GetBoolean() : (bool?)null;
    if (qg.TryGetProperty("EmptySectionViolations", out var ev))
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var item in ev.EnumerateArray())
        {
            var ent = new Dictionary<string, object?>();
            if (item.TryGetProperty("Order", out var o)) ent["order"] = o.GetInt32();
            if (item.TryGetProperty("Title", out var t)) ent["title"] = t.GetString();
            if (item.TryGetProperty("CanonicalTitle", out var ct)) ent["canonicalTitle"] = ct.GetString();
            if (item.TryGetProperty("Requirement", out var req)) ent["requirement"] = req.GetString();
            list.Add(ent);
        }
        q["emptySectionViolations"] = list;
    }
    chat["qualityGates"] = q;
}

// Findings: pivot comparisons - convert to compact list and compute severities
if (root.TryGetProperty("Findings", out var findings))
{
    var highlights = new List<Dictionary<string, object?>>();
    if (findings.TryGetProperty("PivotComparisons", out var pivots))
    {
        foreach (var p in pivots.EnumerateArray())
        {
            var ent = new Dictionary<string, object?>();
            var title = p.TryGetProperty("PivotTitle", out var pt) ? pt.GetString() : null;
            ent["pivotTitle"] = title;
            ent["templateSubtitleCount"] = p.TryGetProperty("TemplateSubtitleCount", out var tsc) ? tsc.GetInt32() : (int?)null;
            ent["userSubtitleCount"] = p.TryGetProperty("UserSubtitleCount", out var usc) ? usc.GetInt32() : (int?)null;
            ent["matchedSubtitleCount"] = p.TryGetProperty("MatchedSubtitleCount", out var msc) ? msc.GetInt32() : (int?)null;
            ent["orderTemplate"] = p.TryGetProperty("PivotOrderTemplate", out var opt) ? opt.GetInt32() : (int?)null;
            ent["orderUser"] = p.TryGetProperty("PivotOrderUser", out var ouu) ? ouu.GetInt32() : (int?)null;
            var missing = p.TryGetProperty("MissingSubtitles", out var miss) ? miss.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray() : Array.Empty<string>();
            // keep extra subtitles internal for severity heuristics but do not expose them in the chat report
            var extra = p.TryGetProperty("ExtraSubtitles", out var ext) ? ext.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray() : Array.Empty<string>();
            ent["missingSubtitles"] = missing;

            // severity heuristics
            var sev = "Low";
            if ((missing?.Length ?? 0) > 0) sev = "High";
            else if ((extra?.Length ?? 0) > 3) sev = "Medium";
            ent["severity"] = sev;

            if ((missing?.Length ?? 0) > 0 || (extra?.Length ?? 0) > 0 || (p.TryGetProperty("PivotOrderTemplate", out var _) && p.TryGetProperty("PivotOrderUser", out var _2) && p.GetProperty("PivotOrderTemplate").GetInt32() != p.GetProperty("PivotOrderUser").GetInt32()))
            {
                highlights.Add(ent);
            }
        }
    }
    // sort highlights by severity and position
    highlights = highlights.OrderBy(h => h["severity"]?.ToString() == "High" ? 0 : h["severity"]?.ToString() == "Medium" ? 1 : 2)
                           .ThenBy(h => (int?)(h["orderUser"] ?? h["orderTemplate"] ?? int.MaxValue)).ToList();
    chat["pivotHighlights"] = highlights;

    // topIssues aggregated
    var topIssues = new List<Dictionary<string, object?>>();
    if (root.TryGetProperty("PolicyCompliance", out var pc2))
    {
        // prefer unified MissingSections
        if (pc2.TryGetProperty("MissingSections", out var msNew) && msNew.GetArrayLength() > 0)
        {
            topIssues.Add(new Dictionary<string, object?> { ["type"] = "MissingSections", ["count"] = msNew.GetArrayLength(), ["items"] = msNew.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray(), ["severity"] = "High" });
        }
        else
        {
            // aggregate older MissingMust/MissingShould/MissingOptional into a single MissingSections entry
            var legacyMissing = new List<string>();
            if (pc2.TryGetProperty("MissingMustSections", out var mm2)) legacyMissing.AddRange(mm2.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null)!);
            if (pc2.TryGetProperty("MissingShouldSections", out var ms2)) legacyMissing.AddRange(ms2.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null)!);
            if (pc2.TryGetProperty("MissingOptionalSections", out var mo2)) legacyMissing.AddRange(mo2.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null)!);
            if (legacyMissing.Count > 0)
            {
                var items = legacyMissing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                topIssues.Add(new Dictionary<string, object?> { ["type"] = "MissingSections", ["count"] = items.Length, ["items"] = items, ["severity"] = "High" });
            }
        }
    }
    if (root.TryGetProperty("QualityGates", out var qg2) && qg2.TryGetProperty("EmptySectionViolations", out var ev2) && ev2.GetArrayLength() > 0)
    {
        var list = ev2.EnumerateArray().Select(x => x.TryGetProperty("Title", out var t)? t.GetString() : null).Where(s=>s!=null).ToArray();
        topIssues.Add(new Dictionary<string, object?> { ["type"] = "EmptySections", ["count"] = list.Length, ["items"] = list, ["severity"] = "Medium" });
    }
    chat["topIssues"] = topIssues;
}

// Table summary (if present)
if (root.TryGetProperty("TableValidation", out var table))
{
    var t = new Dictionary<string, object?>();
    foreach (var p in new[] { "Enabled", "TemplateTableCount", "UserTableCount", "SectionCoveragePercent", "HeaderRecallPercent", "HeaderPrecisionPercent", "QualityBand" })
    {
        if (table.TryGetProperty(p, out var v)) t[p] = v.ValueKind == JsonValueKind.Number ? (object?)v.GetDouble() : v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False ? (object?)v.GetBoolean() : v.GetString();
    }
    chat["tableSummary"] = t;

    // include per-section table comparisons (concise)
    if (table.TryGetProperty("SectionComparisons", out var comps))
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var item in comps.EnumerateArray())
        {
            var ent = new Dictionary<string, object?>();
            if (item.TryGetProperty("SectionCanonicalTitle", out var sct)) ent["sectionCanonicalTitle"] = sct.GetString();
            if (item.TryGetProperty("SectionTitleTemplate", out var stt)) ent["sectionTitleTemplate"] = stt.GetString();
            if (item.TryGetProperty("ExpectedTableCount", out var etc)) ent["expectedTableCount"] = etc.GetInt32();
            if (item.TryGetProperty("DetectedTableCount", out var dtc)) ent["detectedTableCount"] = dtc.GetInt32();
            if (item.TryGetProperty("MissingTableCount", out var mtc)) ent["missingTableCount"] = mtc.GetInt32();
            if (item.TryGetProperty("MissingTablesWaivedByNotApplicable", out var waived)) ent["missingTablesWaivedByNotApplicable"] = waived.GetBoolean();
            if (item.TryGetProperty("MatchedHeaderSignatures", out var mhs)) ent["matchedHeaderSignatures"] = mhs.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray();
            if (item.TryGetProperty("MissingHeaderSignatures", out var mis)) ent["missingHeaderSignatures"] = mis.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray();
            if (item.TryGetProperty("ExtraHeaderSignatures", out var exs)) ent["extraHeaderSignatures"] = exs.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray();
            if (item.TryGetProperty("NotApplicableEvidence", out var nae)) ent["notApplicableEvidence"] = nae.EnumerateArray().Select(e=>e.GetString()).Where(s=>s!=null).ToArray();
            list.Add(ent);
        }
        chat["tableComparisons"] = list;
    }
}

// Recommendations (heuristic)
var rec = new List<string>();
if (chat.TryGetValue("policyCompliance", out var pcv) && pcv is Dictionary<string, object?> pd)
{
    if (pd.TryGetValue("missingSections", out var mssVal) && (mssVal as object[] ?? Array.Empty<object>()).Length > 0)
        rec.Add("Add the missing sections or explicitly mark them 'Not applicable'.");
}
if (chat.TryGetValue("summary", out var sv) && sv is Dictionary<string, object?> sd)
{
    if (sd.TryGetValue("subtitlePrecision", out var sp) && sp is double spd && spd < 70) rec.Add("Subtitle precision is low — review extra subtitles and consolidate duplicates.");
    if (sd.TryGetValue("confidenceBand", out var cb) && cb is string cbs && cbs != "HIGH") rec.Add("Low confidence detected — manually review highlighted pivots and ambiguous sections.");
}
if (rec.Count == 0) rec.Add("No immediate automated actions detected — consider manual review for quality assurance.");
chat["recommendations"] = rec;

// include an abbreviated human-friendly message
chat["humanSummary"] = GenerateHumanSummary(summary, chat);

// If brief mode requested, produce a compact, validator-compatible output
if (brief)
{
    var briefOut = new Dictionary<string, object?>();
    // keep minimal metadata
    if (chat.TryGetValue("metadata", out var md)) briefOut["metadata"] = md;
    // keep crucial summary fields
    if (chat.TryGetValue("summary", out var sm))
    {
        var s = sm as Dictionary<string, object?>;
        var s2 = new Dictionary<string, object?>();
        if (s != null)
        {
            if (s.TryGetValue("finalScore", out var fs)) s2["finalScore"] = fs;
            if (s.TryGetValue("pivotCoverage", out var pc)) s2["pivotCoverage"] = pc;
            if (s.TryGetValue("confidenceBand", out var cb)) s2["confidenceBand"] = cb;
        }
        briefOut["summary"] = s2;
    }

    // policyCompliance: supply missingSections + totalSections if available
    if (chat.TryGetValue("policyCompliance", out var pcv2)) briefOut["policyCompliance"] = pcv2;

    // pivotHighlights: only keep high-severity highlights (or top 5)
    if (chat.TryGetValue("pivotHighlights", out var ph))
    {
        var arr = ph as List<Dictionary<string, object?>> ?? new List<Dictionary<string, object?>>();
        var high = arr.Where(x => (x.TryGetValue("severity", out var sv) && sv?.ToString() == "High")).ToList();
        if (high.Count == 0) high = arr.OrderByDescending(x => (int?)(x.GetValueOrDefault("matchedSubtitleCount") as int? ?? 0)).Take(5).ToList();
        // keep only pivotTitle, missingSubtitles, extraSubtitles, severity
        var briefHighlights = high.Select(x => new Dictionary<string, object?> {
            ["pivotTitle"] = x.GetValueOrDefault("pivotTitle"),
            ["missingSubtitles"] = x.GetValueOrDefault("missingSubtitles"),
            ["severity"] = x.GetValueOrDefault("severity")
        }).ToArray();
        briefOut["pivotHighlights"] = briefHighlights;
    }

    // include brief table comparisons: only problematic sections (missing tables or header mismatches)
    if (chat.TryGetValue("tableComparisons", out var tcAll) && tcAll is List<Dictionary<string, object?>> allTc)
    {
        var problematic = allTc.Where(x => (x.TryGetValue("missingTableCount", out var mt) && (mt as int? ?? 0) > 0)
                                      || (x.TryGetValue("missingHeaderSignatures", out var mhs) && (mhs as object[] ?? Array.Empty<object>()).Length > 0)
                                      || (x.TryGetValue("extraHeaderSignatures", out var ehs) && (ehs as object[] ?? Array.Empty<object>()).Length > 0)).Take(10).ToList();
        if (problematic.Count > 0)
        {
            var briefTc = problematic.Select(x => new Dictionary<string, object?> {
                ["sectionCanonicalTitle"] = x.GetValueOrDefault("sectionCanonicalTitle"),
                ["expectedTableCount"] = x.GetValueOrDefault("expectedTableCount"),
                ["detectedTableCount"] = x.GetValueOrDefault("detectedTableCount"),
                ["missingTableCount"] = x.GetValueOrDefault("missingTableCount"),
                ["missingHeaderSignatures"] = x.GetValueOrDefault("missingHeaderSignatures"),
                ["extraHeaderSignatures"] = x.GetValueOrDefault("extraHeaderSignatures")
            }).ToArray();
            briefOut["tableComparisons"] = briefTc;
        }
    }

    // recommendations and humanSummary
    if (chat.TryGetValue("recommendations", out var rc)) briefOut["recommendations"] = rc;
    if (chat.TryGetValue("humanSummary", out var hs)) briefOut["humanSummary"] = hs;

    // replace chat with briefOut
    chat = briefOut;
}

// write output
var opts = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
var outText = JsonSerializer.Serialize(chat, opts);
File.WriteAllText(outputPath, outText);
Console.WriteLine($"Chat-ready report written to: {Path.GetFullPath(outputPath)}");

string GenerateHumanSummary(Dictionary<string, object?> summaryObj, Dictionary<string, object?> chatObj)
{
    var parts = new List<string>();
    if (summaryObj.TryGetValue("finalScore", out var fs) && fs is double fd) parts.Add($"Overall score: {fd:0.##}%.");
    if (summaryObj.TryGetValue("pivotCoverage", out var pc) && pc is double pcd) parts.Add($"Pivot coverage: {pcd:0.##}%.");
    if (chatObj.TryGetValue("topIssues", out var tis) && tis is List<Dictionary<string, object?>> list && list.Count > 0)
    {
        parts.Add($"Top issues: {string.Join(", ", list.Select(x => (string?)x.GetValueOrDefault("type") ?? "issue"))}.");
    }
    parts.Add("See 'pivotHighlights' for per-section details and 'recommendations' for next steps.");
    return string.Join(" ", parts);
}
