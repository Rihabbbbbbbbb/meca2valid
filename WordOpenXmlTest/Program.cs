using System;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using WordOpenXml.Core;
using WordOpenXml.Core.Models;

var tracePath = Path.Combine(AppContext.BaseDirectory, "run-trace.log");
void Log(string message)
{
    Console.WriteLine(message);
    File.AppendAllText(tracePath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
}

File.WriteAllText(tracePath, string.Empty);
Log("=== WordOpenXmlTest START ===");

string? GetArg(string key)
{
    var index = Array.FindIndex(args, arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= args.Length)
    {
        return null;
    }

    return args[index + 1];
}

var userArg = GetArg("--user");
var templateArg = GetArg("--template");
var guideArg = GetArg("--guide");

if (!string.IsNullOrWhiteSpace(userArg) && !string.IsNullOrWhiteSpace(templateArg) && !string.IsNullOrWhiteSpace(guideArg))
{
    RunComparisonMode(userArg!, templateArg!, guideArg!);
}
else
{
    var path = args.Length > 0
        ? args[0]
        : @"C:\Users\TA29225\Desktop\M2_outer_handle_technical_specification__RSP-update-21072025_ (11) (1).docx";

    RunSingleMode(path);
}

void RunSingleMode(string path)
{
    Log($"Reading: {path}");
    if (!File.Exists(path))
    {
        Log("❌ File not found. Check the path.");
        return;
    }

    var ioWatch = Stopwatch.StartNew();
    var bytes = File.ReadAllBytes(path);
    ioWatch.Stop();
    Log($"Loaded bytes: {bytes.Length} in {ioWatch.ElapsedMilliseconds} ms");

    var parser = new WordParser();
    Log("Parsing started...");
    var parseWatch = Stopwatch.StartNew();
    var parseTask = Task.Run(() => parser.Parse(bytes));
    if (!parseTask.Wait(TimeSpan.FromSeconds(90)))
    {
        Log("❌ Parsing timeout after 90s. The document may be very large/complex.");
        return;
    }

    var sections = parseTask.Result;
    parseWatch.Stop();
    Log($"Parsing done in {parseWatch.ElapsedMilliseconds} ms");

    Log($"Sections found: {sections.Count}");
    var previewCount = Math.Min(sections.Count, 10);
    for (var index = 0; index < previewCount; index++)
    {
        var s = sections[index];
        Log($"[L{s.Level}] {s.Title}");
    }

    if (sections.Count > previewCount)
    {
        Log($"... {sections.Count - previewCount} more sections not displayed");
    }

    var jsonPath = Path.Combine(AppContext.BaseDirectory, "sections.json");
    var jsonPayload = new
    {
        sourceFile = path,
        sectionCount = sections.Count,
        sections = sections.Select(section => new
        {
            level = section.Level,
            title = section.Title,
            content = section.Content
        })
    };

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    File.WriteAllText(jsonPath, JsonSerializer.Serialize(jsonPayload, jsonOptions));
    Log($"JSON exported: {jsonPath}");
    Log("=== END ===");
}

void RunComparisonMode(string userPath, string templatePath, string guidePath)
{
    var files = new Dictionary<string, string>
    {
        ["user"] = userPath,
        ["template"] = templatePath,
        ["guide"] = guidePath
    };

    foreach (var item in files)
    {
        if (!File.Exists(item.Value))
        {
            Log($"❌ Missing {item.Key} file: {item.Value}");
            return;
        }
    }

    Log("Comparison mode started");
    var parser = new WordParser();
    var analyzer = new CdcAnalysisService();

    IReadOnlyList<Section> Parse(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return parser.Parse(bytes);
    }

    var userAnalysis = analyzer.Analyze(userPath, Parse(userPath));
    var templateAnalysis = analyzer.Analyze(templatePath, Parse(templatePath));
    var guideAnalysis = analyzer.Analyze(guidePath, Parse(guidePath));

    var report = new CdcComparisonReport
    {
        User = userAnalysis,
        Template = templateAnalysis,
        Guide = guideAnalysis,
        VsTemplate = analyzer.Compare(userAnalysis, templateAnalysis),
        VsGuide = analyzer.Compare(userAnalysis, guideAnalysis),
        VsTemplateAndGuide = analyzer.Compare(userAnalysis, templateAnalysis, guideAnalysis)
    };

    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    var reportPath = Path.Combine(AppContext.BaseDirectory, "comparison-report.json");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));

    Log($"Comparison JSON exported: {reportPath}");
    Log($"Coverage vs Template: {report.VsTemplate.CoveragePercent}%");
    Log($"Coverage vs Guide: {report.VsGuide.CoveragePercent}%");
    Log($"Coverage vs Template+Guide: {report.VsTemplateAndGuide.CoveragePercent}%");
    Log("=== END ===");
}
