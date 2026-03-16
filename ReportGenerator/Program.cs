using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

string usage = "Usage: ReportGenerator <validation-json> [output-html]";
if (args.Length == 0)
{
    Console.WriteLine(usage);
    return;
}

var inputPath = args[0];
var outputPath = args.Length > 1 ? args[1] : Path.Combine("output", "validation-report.html");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "output");

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Input file not found: {inputPath}");
    return;
}

var jsonText = File.ReadAllText(inputPath);
using var doc = JsonDocument.Parse(jsonText);
var root = doc.RootElement;

string safe(string? s) => string.IsNullOrEmpty(s) ? "—" : System.Net.WebUtility.HtmlEncode(s);

var sb = new StringBuilder();
sb.AppendLine("<!doctype html>");
sb.AppendLine("<html lang=\"en\">\n<head>");
sb.AppendLine("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n<title>Validation Report</title>");
sb.AppendLine("<link rel=\"stylesheet\" href=\"style.css\">\n</head>\n<body>");

// Header / metadata
sb.AppendLine("<header class=\"report-header\">\n  <h1>Template Validation Report</h1>");
if (root.TryGetProperty("Metadata", out var meta))
{
    sb.AppendLine("  <dl class=\"meta\">");
    if (meta.TryGetProperty("Tool", out var tool)) sb.AppendLine($"    <dt>Tool</dt><dd>{safe(tool.GetString())}</dd>");
    if (meta.TryGetProperty("Version", out var ver)) sb.AppendLine($"    <dt>Tool version</dt><dd>{safe(ver.GetString())}</dd>");
    if (meta.TryGetProperty("StartedAtUtc", out var started)) sb.AppendLine($"    <dt>Started</dt><dd>{safe(started.GetString())}</dd>");
    if (meta.TryGetProperty("ValidatedAtUtc", out var validated)) sb.AppendLine($"    <dt>Validated</dt><dd>{safe(validated.GetString())}</dd>");
    if (meta.TryGetProperty("TemplatePath", out var tpath)) sb.AppendLine($"    <dt>Template</dt><dd>{safe(tpath.GetString())}</dd>");
    if (meta.TryGetProperty("UserDocumentPath", out var upath)) sb.AppendLine($"    <dt>User document</dt><dd>{safe(upath.GetString())}</dd>");
    sb.AppendLine("  </dl>");
}
sb.AppendLine("</header>");

// Scores summary
if (root.TryGetProperty("Scores", out var scores))
{
    sb.AppendLine("<section class=\"summary\">\n  <h2>Summary</h2>\n  <ul>");
    if (scores.TryGetProperty("FinalScore", out var final)) sb.AppendLine($"    <li><strong>Final score:</strong> {final.GetDouble():0.##}%</li>");
    if (scores.TryGetProperty("PivotCoveragePercent", out var pcover)) sb.AppendLine($"    <li><strong>Pivot coverage:</strong> {pcover.GetDouble():0.##}%</li>");
    if (scores.TryGetProperty("SubtitleRecallPercent", out var recall)) sb.AppendLine($"    <li><strong>Subtitle recall:</strong> {recall.GetDouble():0.##}%</li>");
    if (scores.TryGetProperty("SubtitlePrecisionPercent", out var prec)) sb.AppendLine($"    <li><strong>Subtitle precision:</strong> {prec.GetDouble():0.##}%</li>");
    if (scores.TryGetProperty("ConfidenceBand", out var band)) sb.AppendLine($"    <li><strong>Confidence:</strong> {safe(band.GetString())}</li>");
    sb.AppendLine("  </ul>\n</section>");
}

// Policy compliance quick view (unified: missingSections)
if (root.TryGetProperty("PolicyCompliance", out var policy))
{
    sb.AppendLine("<section class=\"policy\">\n  <h2>Policy Compliance</h2>\n  <ul>");
    if (policy.TryGetProperty("MissingSections", out var miss))
    {
        var arr = miss.EnumerateArray().Select(e => safe(e.GetString())).ToArray();
        sb.AppendLine($"    <li><strong>Missing sections:</strong> { (arr.Length==0?"None":string.Join(", ", arr)) }</li>");
    }
    else
    {
        // fallback to older fields for compatibility: aggregate all legacy missing lists
        var legacy = new List<string>();
        if (policy.TryGetProperty("MissingMustSections", out var mm)) legacy.AddRange(mm.EnumerateArray().Select(e => safe(e.GetString())));
        if (policy.TryGetProperty("MissingShouldSections", out var ms)) legacy.AddRange(ms.EnumerateArray().Select(e => safe(e.GetString())));
        if (policy.TryGetProperty("MissingOptionalSections", out var mo)) legacy.AddRange(mo.EnumerateArray().Select(e => safe(e.GetString())));
        var items = legacy.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        sb.AppendLine($"    <li><strong>Missing sections:</strong> { (items.Length==0?"None":string.Join(", ", items)) }</li>");
    }
    sb.AppendLine("  </ul>\n</section>");
}

// Top findings - missing required pivots and some example pivot comparisons
if (root.TryGetProperty("Findings", out var findings))
{
    sb.AppendLine("<section class=\"findings\">\n  <h2>Detailed Findings</h2>");
    if (findings.TryGetProperty("PivotComparisons", out var pivots))
    {
        sb.AppendLine("  <div class=\"pivots\">\n    <h3>Pivot comparisons (select highlights)</h3>");
        sb.AppendLine("    <table class=\"pivottable\">\n      <thead><tr><th>Pivot</th><th>Template</th><th>User</th><th>Matched</th><th>Missing</th><th>Extra</th></tr></thead>\n      <tbody>");
        int shown = 0;
        foreach (var p in pivots.EnumerateArray())
        {
            if (shown++ > 50) break;
            var title = safe(p.GetProperty("PivotTitle").GetString());
            var tcount = p.GetProperty("TemplateSubtitleCount").GetInt32();
            var ucount = p.GetProperty("UserSubtitleCount").GetInt32();
            var mcount = p.GetProperty("MatchedSubtitleCount").GetInt32();
            var missing = p.GetProperty("MissingSubtitles").EnumerateArray().Select(e => safe(e.GetString())).ToArray();
            var extra = p.GetProperty("ExtraSubtitles").EnumerateArray().Select(e => safe(e.GetString())).ToArray();
            sb.AppendLine($"      <tr><td>{title}</td><td>{tcount}</td><td>{ucount}</td><td>{mcount}</td><td>{(missing.Length==0?"-":string.Join("; ", missing))}</td><td>{(extra.Length==0?"-":string.Join("; ", extra))}</td></tr>");
        }
        sb.AppendLine("      </tbody>\n    </table>\n  </div>");
    }
    sb.AppendLine("</section>");
}

sb.AppendLine("<footer class=\"report-footer\">Generated by ReportGenerator</footer>");
sb.AppendLine("</body>\n</html>");

File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
// copy style in same folder if available
var styleSrc = Path.Combine(AppContext.BaseDirectory, "style.css");
var styleDest = Path.Combine(Path.GetDirectoryName(outputPath) ?? "output", "style.css");
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "style.css")))
{
    File.Copy(Path.Combine(AppContext.BaseDirectory, "style.css"), styleDest, overwrite: true);
}
else
{
    // create a minimal CSS
    File.WriteAllText(styleDest, @"body{font-family:Segoe UI,Arial,Helvetica,sans-serif;margin:20px;color:#222}header.report-header{border-bottom:1px solid #ddd;padding-bottom:8px;margin-bottom:12px}h1{font-size:22px;margin:0}h2{color:#1b4f72}dl.meta{display:grid;grid-template-columns:140px 1fr;gap:4px 12px;margin-top:8px}dl.meta dt{font-weight:600}table.pivottable{width:100%;border-collapse:collapse;margin-top:8px}table.pivottable th,table.pivottable td{border:1px solid #e5e5e5;padding:6px 8px;text-align:left;font-size:13px}table.pivottable thead th{background:#f5f8fa}", Encoding.UTF8);
}

Console.WriteLine($"Report written to: {Path.GetFullPath(outputPath)}");
