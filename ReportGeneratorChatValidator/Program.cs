using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

string usage = "Usage: ReportGeneratorChatValidator <chat-report.json>";
if (args.Length == 0)
{
    Console.WriteLine(usage);
    return;
}
var path = args[0];
if (!File.Exists(path))
{
    Console.WriteLine($"File not found: {path}");
    Environment.Exit(2);
}

string ReadString(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.ToString();

using var fs = File.OpenRead(path);
using var doc = JsonDocument.Parse(fs);
var root = doc.RootElement;
var errors = new List<string>();

void Require(string key, JsonValueKind kind)
{
    if (!root.TryGetProperty(key, out var p)) { errors.Add($"Missing top-level property '{key}'"); return; }
    if (p.ValueKind != kind) errors.Add($"Property '{key}' should be {kind}, found {p.ValueKind}");
}

Require("metadata", JsonValueKind.Object);
Require("summary", JsonValueKind.Object);
Require("policyCompliance", JsonValueKind.Object);
Require("pivotHighlights", JsonValueKind.Array);
Require("recommendations", JsonValueKind.Array);
Require("humanSummary", JsonValueKind.String);

// Basic checks inside summary
if (root.TryGetProperty("summary", out var summary))
{
    if (!summary.TryGetProperty("finalScore", out var fsVal) || fsVal.ValueKind != JsonValueKind.Number) errors.Add("summary.finalScore missing or not a number");
    if (!summary.TryGetProperty("pivotCoverage", out var pcVal) || pcVal.ValueKind != JsonValueKind.Number) errors.Add("summary.pivotCoverage missing or not a number");
}

// pivotHighlights sanity
if (root.TryGetProperty("pivotHighlights", out var pivots) && pivots.ValueKind == JsonValueKind.Array)
{
    int idx = 0;
    foreach (var p in pivots.EnumerateArray())
    {
        if (!p.TryGetProperty("pivotTitle", out var tt) || tt.ValueKind != JsonValueKind.String) errors.Add($"pivotHighlights[{idx}].pivotTitle missing or not string");
        if (!p.TryGetProperty("severity", out var sev) || sev.ValueKind != JsonValueKind.String) errors.Add($"pivotHighlights[{idx}].severity missing or not string");
        idx++;
    }
}

// Print results
if (errors.Count == 0)
{
    Console.WriteLine($"Validation PASSED for: {Path.GetFullPath(path)}");
    Environment.Exit(0);
}
else
{
    Console.WriteLine($"Validation FAILED with {errors.Count} error(s):");
    foreach (var e in errors) Console.WriteLine(" - " + e);
    Environment.Exit(1);
}
