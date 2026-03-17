using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.OpenApi.Models;
using Microsoft.Azure.Functions.Worker.Extensions.OpenApi.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WordOpenXml.Core;
using WordOpenXml.Core.Models;

// ═══════════════════════════════════════════════════════════════════
// CDC Validation Engine v4 — Specialist Analysis
// ═══════════════════════════════════════════════════════════════════
// Fixes over v3:
// 1. Smart table matching — groups by distinct signature, compares
//    counts per unique pattern, not 1-to-N inflation
// 2. Section-aware table mapping — ties user tables to nearest heading
// 3. Table-content awareness in emptiness check
// 4. Placeholder content detection (<<...>>, XXX, <component name>)
// 5. Content depth check — flags suspiciously thin sections
// 6. Blueprint-empty section awareness — structural parents excluded
// ═══════════════════════════════════════════════════════════════════

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureOpenApi()
    .Build();

host.Run();

namespace TemplateOneShotExtractor.Models
{
    // ─── REQUEST / RESPONSE DTOs ─────────────────────────────────

    public class ValidationRequest
    {
        public string TemplateBlueprint { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string? DocumentUrl { get; set; }
    }

    public class ValidationIssue
    {
        public string Type { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class ValidationSummary
    {
        public int TotalBlueprintSections { get; set; }
        public int MatchedSections { get; set; }
        public int MissingSections { get; set; }
        public int TrulyEmptySections { get; set; }
        public int PlaceholderSections { get; set; }

        public int TotalBlueprintTablePatterns { get; set; }
        public int MatchedTablePatterns { get; set; }
        public int MissingTablePatterns { get; set; }
        public int EmptyTableBodies { get; set; }
        public int TotalBlueprintTables { get; set; }
        public int UserTableCount { get; set; }
    }

    public class ValidationReport
    {
        public bool IsValid { get; set; }
        public int Score { get; set; }
        public ValidationSummary Summary { get; set; } = new();
        public List<ValidationIssue> Issues { get; set; } = new();
    }

    public class ValidationResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ValidatedAt { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        public string Template { get; set; } = string.Empty;
        public ValidationReport Report { get; set; } = new();
        public string CorrelationId { get; set; } = string.Empty;
        public ChatReport ChatReport { get; set; } = new();
    }

    public class ErrorResponse
    {
        public string Error { get; set; } = string.Empty;
        public string? Details { get; set; }
    }

    // ─── CHAT REPORT — evaluator-focused ─────────────────────────

    public class ChatMetadata
    {
        public string Tool { get; set; } = "TemplateOneShotExtractor-AzureFunction";
        public string Version { get; set; } = "5.0.0";
        public string StartedAtUtc { get; set; } = string.Empty;
        public string ValidatedAtUtc { get; set; } = string.Empty;
        public long DurationMs { get; set; }
        public string TemplatePath { get; set; } = string.Empty;
        public string UserDocumentPath { get; set; } = string.Empty;
    }

    public class ChatSummary
    {
        public double FinalScore { get; set; }
        public double SectionCoverage { get; set; }
        public double SubtitleCoverage { get; set; }
        public double TablePatternCoverage { get; set; }
        public string ConfidenceBand { get; set; } = "LOW";
    }

    public class ChatPivotHighlight
    {
        public string PivotTitle { get; set; } = string.Empty;
        public int ExpectedSubtitleCount { get; set; }
        public int FoundSubtitleCount { get; set; }
        public int MatchedSubtitleCount { get; set; }
        public List<string> MissingSubtitles { get; set; } = new();
    }

    public class ChatTopIssue
    {
        public string Type { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Items { get; set; } = new();
    }

    public class ChatTableIssue
    {
        public string PatternSignature { get; set; } = string.Empty;
        public int ExpectedCount { get; set; }
        public int FoundCount { get; set; }
        public List<string> ExpectedInSections { get; set; } = new();
        public string Status { get; set; } = string.Empty;
    }

    public class ChatReport
    {
        public ChatMetadata Metadata { get; set; } = new();
        public ChatSummary Summary { get; set; } = new();
        public List<ChatTopIssue> TopIssues { get; set; } = new();
        public List<ChatPivotHighlight> PivotHighlights { get; set; } = new();
        public List<ChatTableIssue> TableAnalysis { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
        public string HumanSummary { get; set; } = string.Empty;
    }
}

namespace TemplateOneShotExtractor
{
    using TemplateOneShotExtractor.Models;

    public class ValidateTemplateFunction
    {
        private readonly ILogger<ValidateTemplateFunction> _logger;
        private static readonly HttpClient _sharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

        // Patterns that indicate placeholder/template content
        private static readonly Regex PlaceholderRegex = new(
            @"<<[^>]+>>|<[A-Za-z][^>]{2,}>|\bXXX+\b|\bYYY+\b|\bZZZ+\b|\b000X+\b|\bNAME\b|\bYYYY/MM/DD\b|\binsert\s+(here|text|diagram|the)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public ValidateTemplateFunction(ILogger<ValidateTemplateFunction> logger)
        {
            _logger = logger;
        }

        [Function("ValidateTemplate")]
        [OpenApiOperation(
            operationId: "ValidateTemplate",
            tags: new[] { "Template Validation" },
            Summary = "Validate a CDC document against blueprint specifications",
            Description = "Downloads a user DOCX, parses sections and tables, compares against the blueprint template with specialist CDC analysis: section coverage, table pattern matching, empty/placeholder/thin section detection.",
            Visibility = OpenApiVisibilityType.Important
        )]
        [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(ValidationRequest), Required = true,
            Description = "Validation request with documentUrl (REQUIRED — SAS URL to the DOCX)")]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json",
            bodyType: typeof(ValidationResponse), Summary = "Validation report generated")]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json",
            bodyType: typeof(ErrorResponse), Summary = "Bad request")]
        [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json",
            bodyType: typeof(ErrorResponse), Summary = "Internal server error")]
        [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "code", In = OpenApiSecurityLocationType.Query)]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            var correlationId = Guid.NewGuid().ToString("N")[..12];
            _logger.LogInformation("ValidateTemplate v4 triggered. CId={CId}", correlationId);

            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

                Dictionary<string, string>? argsMap;
                try { argsMap = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody); }
                catch (JsonException ex)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new ErrorResponse { Error = "Invalid JSON", Details = ex.Message });
                    return bad;
                }

                if (argsMap == null || !argsMap.ContainsKey("templateBlueprint") || !argsMap.ContainsKey("user"))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new ErrorResponse { Error = "Missing required fields: templateBlueprint, user" });
                    return bad;
                }

                var blueprintPath = Environment.GetEnvironmentVariable("BLUEPRINT_PATH") ?? argsMap["templateBlueprint"];
                var userPath = argsMap["user"];
                var documentUrl = argsMap.ContainsKey("documentUrl") ? argsMap["documentUrl"] : null;

                if (string.IsNullOrWhiteSpace(documentUrl))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new ErrorResponse
                    {
                        Error = "Missing required field: documentUrl",
                        Details = "Provide a valid HTTPS URL to a .docx document (with SAS token if needed)."
                    });
                    return bad;
                }

                var (report, chatReport) = await PerformValidationAsync(blueprintPath, documentUrl, correlationId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new ValidationResponse
                {
                    Success = true,
                    Message = report.IsValid
                        ? "Template validation passed — no critical issues found"
                        : "Template validation completed — issues found that need attention",
                    ValidatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    User = userPath,
                    Template = blueprintPath,
                    Report = report,
                    CorrelationId = correlationId,
                    ChatReport = chatReport
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during validation");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new ErrorResponse { Error = "Validation error", Details = ex.Message });
                return err;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // VALIDATION ENGINE v4
        // ═══════════════════════════════════════════════════════════════

        private async Task<(ValidationReport Report, ChatReport Chat)> PerformValidationAsync(
            string blueprintPathOrUrl, string documentUrl, string correlationId)
        {
            var startedAt = DateTime.UtcNow;
            var report = new ValidationReport { Issues = new List<ValidationIssue>() };
            var chat = new ChatReport
            {
                Metadata = new ChatMetadata
                {
                    StartedAtUtc = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    TemplatePath = blueprintPathOrUrl,
                    UserDocumentPath = documentUrl
                }
            };

            // ── STEP 1: Download user DOCX ───────────────────────────────
            byte[] docxBytes;
            try
            {
                _logger.LogInformation("[{CId}] Downloading DOCX…", correlationId);
                docxBytes = await _sharedHttpClient.GetByteArrayAsync(documentUrl);
                _logger.LogInformation("[{CId}] Downloaded {Size} bytes", correlationId, docxBytes.Length);
            }
            catch (HttpRequestException ex)
            {
                return FailFast(report, chat, "documentUrl",
                    $"Cannot download document: {ex.StatusCode} — {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                return FailFast(report, chat, "documentUrl", "Download timed out (60s)");
            }

            // ── STEP 2: Parse user DOCX — sections ──────────────────────
            IReadOnlyList<Section> rawSections;
            try
            {
                rawSections = new WordParser().Parse(docxBytes);
                _logger.LogInformation("[{CId}] WordParser: {N} raw sections", correlationId, rawSections.Count);
            }
            catch (Exception ex)
            {
                return FailFast(report, chat, "document", $"Cannot parse DOCX: {ex.Message}");
            }

            if (rawSections.Count == 0)
                return FailFast(report, chat, "document", "Document has no identifiable headings");

            // ── STEP 3: Extract tables from user DOCX ────────────────────
            // Returns tables with section-aware mapping (nearest heading)
            var userTables = ExtractTablesFromDocx(docxBytes);
            _logger.LogInformation("[{CId}] Extracted {N} tables from user DOCX", correlationId, userTables.Count);

            // Build a set of section indices that have tables
            var sectionsWithTableIndices = new HashSet<int>();
            foreach (var ut in userTables)
            {
                if (ut.NearestHeadingIndex >= 0)
                    sectionsWithTableIndices.Add(ut.NearestHeadingIndex);
            }

            // ── STEP 4: Analyze user sections ────────────────────────────
            var svc = new CdcAnalysisService();
            var userAnalysis = svc.Analyze("user-document", rawSections);
            _logger.LogInformation("[{CId}] User analysis: {N} normalized sections", correlationId, userAnalysis.Sections.Count);

            // ── STEP 5: Load blueprint ───────────────────────────────────
            string blueprintJson;
            try
            {
                blueprintJson = await LoadResourceAsync(blueprintPathOrUrl, correlationId);
            }
            catch (Exception ex)
            {
                return FailFast(report, chat, "blueprint", $"Cannot load blueprint: {ex.Message}");
            }

            var blueprintHeadings = ExtractBlueprintHeadings(blueprintJson);
            var blueprintTables = ExtractBlueprintTables(blueprintJson);
            var blueprintEmptyOrders = ExtractBlueprintEmptyOrders(blueprintJson);
            _logger.LogInformation("[{CId}] Blueprint: {H} headings, {T} tables, {E} empty orders",
                correlationId, blueprintHeadings.Count, blueprintTables.Count, blueprintEmptyOrders.Count);

            // Build reference analysis from blueprint headings
            var refSections = blueprintHeadings.Select(h => new Section
            {
                Level = h.Level, Title = h.Title, Content = string.Empty
            }).ToList();
            var refAnalysis = svc.Analyze("blueprint", refSections);

            // Build a map: blueprint canonical title → order, for empty-order lookup
            var canonicalToOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in blueprintHeadings)
            {
                var canon = h.Title.ToUpperInvariant().Trim();
                // Use NumberingPrefixRegex equivalent
                canon = Regex.Replace(canon, @"^\d+(\.\d+)*\s*[-:.)]?\s*", "");
                canon = Regex.Replace(canon, @"\s+", " ").Trim();
                if (!canonicalToOrder.ContainsKey(canon))
                    canonicalToOrder[canon] = h.Order;
            }

            // ── STEP 6: Section comparison ───────────────────────────────
            var comparison = svc.Compare(userAnalysis, refAnalysis);
            _logger.LogInformation("[{CId}] Sections: {Common} common, {Missing} missing, {Extra} extra, Coverage={Cov:F1}%",
                correlationId, comparison.Common.Count, comparison.MissingInUser.Count,
                comparison.ExtraInUser.Count, comparison.CoveragePercent);

            // ── STEP 7: Truly empty section detection ────────────────────
            // v4 improvement: a section is NOT empty if it has child
            // sub-sections with content OR if it has tables.
            var allTrulyEmpty = FindTrulyEmptySections(userAnalysis.Sections, sectionsWithTableIndices);
            var commonSet = new HashSet<string>(comparison.Common, StringComparer.OrdinalIgnoreCase);

            // Filter: only flag sections in the blueprint AND not blueprint-empty parents
            var trulyEmptySections = new List<NormalizedSection>();
            foreach (var s in allTrulyEmpty)
            {
                if (!commonSet.Contains(s.CanonicalTitle)) continue;

                // If the blueprint itself has this section as empty AND it's a
                // structural parent (Level 1-2), skip — it's expected to be empty
                if (canonicalToOrder.TryGetValue(s.CanonicalTitle, out var order)
                    && blueprintEmptyOrders.Contains(order)
                    && s.Level <= 2)
                {
                    // Check if this section has children in the blueprint
                    bool hasChildrenInBlueprint = blueprintHeadings
                        .Any(h => h.Order > order && h.Level > s.Level
                             && !blueprintHeadings.Any(h2 => h2.Order > order && h2.Order < h.Order && h2.Level <= s.Level));

                    if (hasChildrenInBlueprint) continue; // Structural parent — skip
                }

                trulyEmptySections.Add(s);
            }
            _logger.LogInformation("[{CId}] Truly empty sections: {N}", correlationId, trulyEmptySections.Count);

            // ── STEP 8: Placeholder content detection ────────────────────
            // Find sections that exist and have text, but the text is mostly
            // template placeholder content (<<...>>, XXX, <component name>)
            var placeholderSections = new List<(NormalizedSection Section, double PlaceholderRatio)>();
            foreach (var s in userAnalysis.Sections)
            {
                if (!commonSet.Contains(s.CanonicalTitle)) continue;
                if (string.IsNullOrWhiteSpace(s.Content)) continue;

                int totalWords = CountWords(s.Content);
                if (totalWords < 3) continue; // Too short to judge

                var matches = PlaceholderRegex.Matches(s.Content);
                int placeholderWords = matches.Sum(m => CountWords(m.Value));
                double ratio = (double)placeholderWords / totalWords;

                if (ratio > 0.40) // More than 40% placeholder content
                {
                    placeholderSections.Add((s, Math.Round(ratio * 100, 1)));
                }
            }
            _logger.LogInformation("[{CId}] Placeholder sections: {N}", correlationId, placeholderSections.Count);

            // ── STEP 9: Smart table pattern matching ─────────────────────
            // Group blueprint tables by DISTINCT signature (not 1-to-N).
            // Count how many instances of each pattern exist. Compare
            // against user table signature counts.
            // Filter out explanatory/naming-convention tables that aren't real data tables.
            var sectionTables = blueprintTables
                .Where(t => t.SectionOrder.HasValue && !IsExplanatoryTable(t.HeaderSignature))
                .ToList();

            // Group by signature → expected count + sections + min blueprint row count
            var blueprintSigGroups = sectionTables
                .GroupBy(t => t.HeaderSignature, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Signature = g.Key,
                    ExpectedCount = g.Count(),
                    Sections = g.Select(t => t.SectionTitle).Distinct().ToList(),
                    // Average blueprint RowCount: used to decide if an empty user table body is suspicious
                    AvgBlueprintRowCount = g.Average(t => (double)t.RowCount)
                })
                .ToList();

            // User table signature → count (exact)
            var userSigCounts = userTables
                .Where(t => !string.IsNullOrWhiteSpace(t.HeaderSignature))
                .GroupBy(t => t.HeaderSignature, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // Also build a fuzzy lookup: first two normalized words of each user sig
            // so "req no v|..." still matches "requirement number v|..." (synonyms)
            var userSigFuzzySet = userTables
                .Where(t => !string.IsNullOrWhiteSpace(t.HeaderSignature))
                .Select(t => SigFuzzyKey(t.HeaderSignature))
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Build list of all user signature strings for substring fallback matching
            var allUserSigs = userTables
                .Where(t => !string.IsNullOrWhiteSpace(t.HeaderSignature))
                .Select(t => t.HeaderSignature.ToLowerInvariant())
                .ToList();

            var tableAnalysis = new List<ChatTableIssue>();
            int matchedPatterns = 0;
            int totalPatterns = blueprintSigGroups.Count;

            foreach (var group in blueprintSigGroups)
            {
                int foundCount = userSigCounts.TryGetValue(group.Signature, out var c) ? c : 0;

                // Fuzzy fallback #1: synonym-normalized first-column key
                bool fuzzyMatch = foundCount == 0
                    && !string.IsNullOrWhiteSpace(group.Signature)
                    && userSigFuzzySet.Contains(SigFuzzyKey(group.Signature));

                // Fuzzy fallback #2: substring containment for single-column signatures
                // (e.g. blueprint "on indelible inscription" matches user table containing that text)
                if (!fuzzyMatch && foundCount == 0 && !group.Signature.Contains('|'))
                {
                    var bpLower = group.Signature.ToLowerInvariant().Trim();
                    fuzzyMatch = allUserSigs.Any(us =>
                        us.Contains(bpLower) || bpLower.Contains(us));
                }

                if (foundCount > 0 || fuzzyMatch)
                {
                    matchedPatterns++;
                    // Only report partial if significantly fewer AND exact match (fuzzy = ok)
                    if (!fuzzyMatch && foundCount < group.ExpectedCount * 0.5 && group.ExpectedCount > 2)
                    {
                        tableAnalysis.Add(new ChatTableIssue
                        {
                            PatternSignature = group.Signature,
                            ExpectedCount = group.ExpectedCount,
                            FoundCount = foundCount,
                            ExpectedInSections = group.Sections,
                            Status = fuzzyMatch ? "fuzzy-match" : "partial"
                        });
                    }
                }
                else
                {
                    tableAnalysis.Add(new ChatTableIssue
                    {
                        PatternSignature = group.Signature,
                        ExpectedCount = group.ExpectedCount,
                        FoundCount = 0,
                        ExpectedInSections = group.Sections,
                        Status = "missing"
                    });
                }
            }

            int missingPatterns = tableAnalysis.Count(t => t.Status == "missing");
            double tablePatternCoverage = totalPatterns > 0
                ? Math.Round((double)matchedPatterns / totalPatterns * 100, 2) : 100;

            _logger.LogInformation("[{CId}] Table patterns: {Matched}/{Total} matched, {Missing} missing, Coverage={Cov:F1}%",
                correlationId, matchedPatterns, totalPatterns, missingPatterns, tablePatternCoverage);

            // ── STEP 10: Empty table body detection ──────────────────────
            // A table that exists (correct headers) but has only 1 row (the
            // header itself — no data rows) is structurally hollow. Engineers
            // often put the shell in and forget to fill it.
            // Only flag where the blueprint itself has avg RowCount >= 3,
            // meaning real data rows are expected (excludes single-row info boxes).
            var sigToAvgBlueprintRows = blueprintSigGroups
                .ToDictionary(g => g.Signature, g => g.AvgBlueprintRowCount,
                    StringComparer.OrdinalIgnoreCase);

            // Build fuzzy-to-sig reverse map so we can look up blueprint avg rows for fuzzy matches
            var fuzzyKeyToSignature = blueprintSigGroups
                .GroupBy(g => SigFuzzyKey(g.Signature), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Signature,
                    StringComparer.OrdinalIgnoreCase);

            var emptyBodyTables = new List<(ExtractedTable Table, string MatchedSection)>();
            foreach (var ut in userTables)
            {
                if (ut.RowCount != 1) continue; // Only interested in header-only tables
                if (string.IsNullOrWhiteSpace(ut.HeaderSignature)) continue;

                // Find the blueprint avg row count for this signature (exact or fuzzy)
                double blueprintAvgRows = 0;
                if (sigToAvgBlueprintRows.TryGetValue(ut.HeaderSignature, out var exactAvg))
                    blueprintAvgRows = exactAvg;
                else
                {
                    var fk = SigFuzzyKey(ut.HeaderSignature);
                    if (!string.IsNullOrWhiteSpace(fk) && fuzzyKeyToSignature.TryGetValue(fk, out var matchedSig)
                        && sigToAvgBlueprintRows.TryGetValue(matchedSig, out var fuzzyAvg))
                        blueprintAvgRows = fuzzyAvg;
                }

                // Only flag if blueprint expects >= 3 rows (real data tables)
                if (blueprintAvgRows < 3) continue;

                // Resolve which section this table belongs to
                string sectionName = ut.NearestHeadingIndex >= 0
                    && ut.NearestHeadingIndex < userAnalysis.Sections.Count
                    ? userAnalysis.Sections[ut.NearestHeadingIndex].Title
                    : "(unknown section)";

                emptyBodyTables.Add((ut, sectionName));
            }
            _logger.LogInformation("[{CId}] Empty table bodies: {N}", correlationId, emptyBodyTables.Count);

            // ── STEP 11: Build issues list ───────────────────────────────
            // ONLY actionable items — no noise about what exists

            // Missing sections
            foreach (var missing in comparison.MissingInUser)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "MissingSection",
                    Field = missing,
                    Message = $"Required section '{missing}' is missing from the document"
                });
            }

            // Truly empty sections
            foreach (var empty in trulyEmptySections)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "EmptySection",
                    Field = empty.Title,
                    Message = $"Section '{empty.Title}' exists but has no content (no text, no sub-sections with content, and no tables)"
                });
            }

            // Placeholder sections
            foreach (var (sec, ratio) in placeholderSections)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "PlaceholderContent",
                    Field = sec.Title,
                    Message = $"Section '{sec.Title}' appears to contain ~{ratio}% placeholder/template text (<<...>>, XXX, <name>) — needs real content"
                });
            }

            // Missing table patterns — one issue PER SECTION for clarity
            foreach (var mt in tableAnalysis.Where(t => t.Status == "missing"))
            {
                if (mt.ExpectedInSections.Count <= 3)
                {
                    // Small group: one issue per section
                    foreach (var section in mt.ExpectedInSections)
                    {
                        report.Issues.Add(new ValidationIssue
                        {
                            Type = "MissingTablePattern",
                            Field = section,
                            Message = $"Section '{section}': required table with headers [{TruncateSig(mt.PatternSignature)}] not found — check that this table exists and has the correct column headers"
                        });
                    }
                }
                else
                {
                    // Large group (dominant pattern like requirement matrix, 50 sections):
                    // Single issue — the table header format itself likely differs
                    string preview = string.Join(", ", mt.ExpectedInSections.Take(3));
                    report.Issues.Add(new ValidationIssue
                    {
                        Type = "MissingTablePattern",
                        Field = $"{mt.ExpectedInSections.Count} sections",
                        Message = $"Requirement table pattern [{TruncateSig(mt.PatternSignature)}] not found in {mt.ExpectedInSections.Count} sections (e.g. {preview}…). " +
                                  $"Likely cause: your table headers use different wording than the blueprint — check that column headers match exactly."
                    });
                }
            }

            // Partial table patterns
            foreach (var mt in tableAnalysis.Where(t => t.Status == "partial"))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "PartialTablePattern",
                    Field = mt.PatternSignature,
                    Message = $"Table pattern [{TruncateSig(mt.PatternSignature)}] found {mt.FoundCount}× but expected {mt.ExpectedCount}× — some sections may be missing their tables"
                });
            }

            // Empty table bodies — table exists with correct headers but zero data rows
            foreach (var (tbl, section) in emptyBodyTables)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "EmptyTableBody",
                    Field = section,
                    Message = $"Section '{section}': table [{TruncateSig(tbl.HeaderSignature)}] has no data rows — the table structure is present but no requirements have been entered"
                });
            }

            // ── STEP 11: Score calculation ───────────────────────────────
            // Weighted: 50% section coverage, 25% table pattern coverage,
            // 15% content quality (empty + placeholder), 10% table body quality
            double sectionScore = comparison.CoveragePercent;
            double tableScore = tablePatternCoverage;

            int contentIssues = trulyEmptySections.Count + placeholderSections.Count;
            double contentPenalty = refAnalysis.Sections.Count > 0
                ? (double)contentIssues / refAnalysis.Sections.Count * 100 : 0;

            // Empty table body penalty: relative to total matched tables
            int totalMatchedUserTables = userTables.Count(ut =>
                !string.IsNullOrWhiteSpace(ut.HeaderSignature)
                && (userSigCounts.ContainsKey(ut.HeaderSignature)
                    || userSigFuzzySet.Contains(SigFuzzyKey(ut.HeaderSignature))));
            double emptyBodyPenalty = totalMatchedUserTables > 0
                ? (double)emptyBodyTables.Count / totalMatchedUserTables * 100 : 0;

            int finalScore = Math.Max(0, Math.Min(100,
                (int)Math.Round(
                    sectionScore * 0.50
                    + tableScore * 0.25
                    + (100 - contentPenalty) * 0.15
                    + (100 - emptyBodyPenalty) * 0.10)));

            report.Score = finalScore;
            report.Summary = new ValidationSummary
            {
                TotalBlueprintSections = refAnalysis.Sections.Count,
                MatchedSections = comparison.Common.Count,
                MissingSections = comparison.MissingInUser.Count,
                TrulyEmptySections = trulyEmptySections.Count,
                PlaceholderSections = placeholderSections.Count,
                TotalBlueprintTablePatterns = totalPatterns,
                MatchedTablePatterns = matchedPatterns,
                MissingTablePatterns = missingPatterns,
                EmptyTableBodies = emptyBodyTables.Count,
                TotalBlueprintTables = sectionTables.Count,
                UserTableCount = userTables.Count
            };
            report.IsValid = comparison.MissingInUser.Count == 0
                          && trulyEmptySections.Count == 0
                          && placeholderSections.Count == 0
                          && missingPatterns == 0
                          && emptyBodyTables.Count == 0;

            // ── STEP 13: Build ChatReport ────────────────────────────────
            var validatedAt = DateTime.UtcNow;
            chat.Metadata.ValidatedAtUtc = validatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            chat.Metadata.DurationMs = (long)(validatedAt - startedAt).TotalMilliseconds;

            // Pivot highlights
            var pivotHighlights = BuildPivotHighlights(refAnalysis, userAnalysis);
            int totalExpectedSubs = pivotHighlights.Sum(p => p.ExpectedSubtitleCount);
            int totalMatchedSubs = pivotHighlights.Sum(p => p.MatchedSubtitleCount);
            double subtitleCoverage = totalExpectedSubs > 0
                ? Math.Round((double)totalMatchedSubs / totalExpectedSubs * 100, 2) : 100;

            string band = sectionScore >= 80 && subtitleCoverage >= 70 && tablePatternCoverage >= 70 ? "HIGH"
                        : sectionScore >= 50 && tablePatternCoverage >= 40 ? "MEDIUM" : "LOW";

            chat.Summary = new ChatSummary
            {
                FinalScore = finalScore,
                SectionCoverage = comparison.CoveragePercent,
                SubtitleCoverage = subtitleCoverage,
                TablePatternCoverage = tablePatternCoverage,
                ConfidenceBand = band
            };

            // Top Issues — aggregated by type
            chat.TopIssues = new List<ChatTopIssue>();
            if (comparison.MissingInUser.Count > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "MissingSections",
                    Count = comparison.MissingInUser.Count,
                    Items = comparison.MissingInUser.ToList()
                });
            }
            if (trulyEmptySections.Count > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "EmptySections",
                    Count = trulyEmptySections.Count,
                    Items = trulyEmptySections.Select(s => s.Title).ToList()
                });
            }
            if (placeholderSections.Count > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "PlaceholderContent",
                    Count = placeholderSections.Count,
                    Items = placeholderSections.Select(p => $"{p.Section.Title} ({p.PlaceholderRatio}% placeholder)").ToList()
                });
            }
            if (missingPatterns > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "MissingTablePatterns",
                    Count = missingPatterns,
                    Items = tableAnalysis.Where(t => t.Status == "missing")
                        .Select(t => $"[{TruncateSig(t.PatternSignature)}] ×{t.ExpectedCount}").ToList()
                });
            }
            if (emptyBodyTables.Count > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "EmptyTableBodies",
                    Count = emptyBodyTables.Count,
                    Items = emptyBodyTables
                        .GroupBy(e => e.MatchedSection)
                        .Select(g => g.Count() == 1
                            ? g.First().MatchedSection
                            : $"{g.First().MatchedSection} (+{g.Count() - 1} more)")
                        .ToList()
                });
            }

            // Pivot highlights (only those with problems)
            chat.PivotHighlights = pivotHighlights
                .Where(p => p.MissingSubtitles.Count > 0)
                .OrderByDescending(p => p.MissingSubtitles.Count)
                .ToList();

            // Table analysis (only issues)
            chat.TableAnalysis = tableAnalysis;

            // Recommendations
            chat.Recommendations = BuildRecommendations(
                comparison.MissingInUser.Count, trulyEmptySections.Count,
                placeholderSections.Count, emptyBodyTables.Count,
                missingPatterns, band, comparison.ExtraInUser.Count,
                userTables.Count, sectionTables.Count);

            // Human summary
            chat.HumanSummary = BuildHumanSummary(report, chat, comparison);

            _logger.LogInformation(
                "[{CId}] DONE: Score={Score}%, Band={Band}, Missing={MS}, Empty={ES}, Placeholder={PS}, MissingTablePatterns={MT}, EmptyTableBodies={ETB}",
                correlationId, finalScore, band, comparison.MissingInUser.Count,
                trulyEmptySections.Count, placeholderSections.Count, missingPatterns, emptyBodyTables.Count);

            return (report, chat);
        }

        // ═══════════════════════════════════════════════════════════════
        // TRULY EMPTY SECTION DETECTION (v4)
        // ═══════════════════════════════════════════════════════════════
        // A section is "truly empty" ONLY if:
        //   - No direct text content (0 words)
        //   - No child sub-sections with content (walk forward)
        //   - No tables in the section (table-content aware)
        // ═══════════════════════════════════════════════════════════════

        private static List<NormalizedSection> FindTrulyEmptySections(
            IReadOnlyList<NormalizedSection> sections, HashSet<int> sectionsWithTableIndices)
        {
            var result = new List<NormalizedSection>();

            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                int ownWords = CountWords(section.Content);

                if (ownWords > 0) continue;

                // Check if this section has tables
                if (sectionsWithTableIndices.Contains(i)) continue;

                // Check children
                bool childrenHaveContent = false;
                for (int j = i + 1; j < sections.Count; j++)
                {
                    if (sections[j].Level <= section.Level)
                        break;

                    if (CountWords(sections[j].Content) > 0 || sectionsWithTableIndices.Contains(j))
                    {
                        childrenHaveContent = true;
                        break;
                    }
                }

                if (!childrenHaveContent)
                    result.Add(section);
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // TABLE EXTRACTION FROM USER DOCX (v4)
        // ═══════════════════════════════════════════════════════════════
        // Extracts tables AND maps each table to its nearest preceding
        // heading for section-aware analysis.
        // ═══════════════════════════════════════════════════════════════

        private static List<ExtractedTable> ExtractTablesFromDocx(byte[] docxBytes)
        {
            var tables = new List<ExtractedTable>();

            using var stream = new MemoryStream(docxBytes);
            using var document = WordprocessingDocument.Open(stream, false);
            var body = document.MainDocumentPart?.Document?.Body;
            if (body == null) return tables;

            // First pass: collect heading positions for section mapping
            var headingPositions = new List<(int ElementIndex, string Title)>();
            int elementIdx = 0;
            foreach (var element in body.ChildElements)
            {
                if (element is Paragraph para)
                {
                    var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var outlineLevel = para.ParagraphProperties?.OutlineLevel?.Val?.Value;
                    if ((!string.IsNullOrWhiteSpace(styleId)
                         && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                        || outlineLevel is not null)
                    {
                        headingPositions.Add((elementIdx, para.InnerText?.Trim() ?? ""));
                    }
                }
                elementIdx++;
            }

            // Second pass: extract tables with section mapping
            int tableIndex = 0;
            int bodyElementIndex = 0;
            foreach (var element in body.ChildElements)
            {
                if (element is Table table)
                {
                    tableIndex++;
                    var rows = table.Elements<TableRow>().ToList();
                    if (rows.Count == 0) { bodyElementIndex++; continue; }

                    // Extract header cells from first row
                    var headerCells = rows[0].Elements<TableCell>()
                        .Select(cell => NormalizeTableCell(cell.InnerText))
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .ToList();

                    if (headerCells.Count == 0) { bodyElementIndex++; continue; }

                    // Build normalized header signature (lowercase, pipe-separated)
                    var signature = string.Join("|", headerCells.Select(c =>
                        Regex.Replace(c.ToLowerInvariant().Trim(), @"\s+", " ")));

                    // Find nearest preceding heading
                    int nearestHeadingIdx = -1;
                    for (int h = headingPositions.Count - 1; h >= 0; h--)
                    {
                        if (headingPositions[h].ElementIndex < bodyElementIndex)
                        {
                            nearestHeadingIdx = h;
                            break;
                        }
                    }

                    tables.Add(new ExtractedTable
                    {
                        TableIndex = tableIndex,
                        RowCount = rows.Count,
                        ColumnCount = rows.Max(r => r.Elements<TableCell>().Count()),
                        HeaderCells = headerCells,
                        HeaderSignature = signature,
                        NearestHeadingIndex = nearestHeadingIdx
                    });
                }
                bodyElementIndex++;
            }

            return tables;
        }

        private static string NormalizeTableCell(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return Regex.Replace(text.Trim(), @"\s+", " ");
        }

        // ═══════════════════════════════════════════════════════════════
        // PIVOT HIGHLIGHTS
        // ═══════════════════════════════════════════════════════════════

        private static List<ChatPivotHighlight> BuildPivotHighlights(
            DocumentAnalysis reference, DocumentAnalysis user)
        {
            var pivots = new List<(NormalizedSection Pivot, List<NormalizedSection> Subs)>();
            (NormalizedSection Pivot, List<NormalizedSection> Subs)? current = null;

            foreach (var section in reference.Sections)
            {
                if (section.Level <= 1)
                {
                    if (current != null) pivots.Add(current.Value);
                    current = (section, new List<NormalizedSection>());
                }
                else
                {
                    if (current == null)
                    {
                        current = (new NormalizedSection
                        {
                            Level = 1, Title = "(Preamble)",
                            CanonicalTitle = "PREAMBLE", Content = ""
                        }, new List<NormalizedSection>());
                    }
                    current.Value.Subs.Add(section);
                }
            }
            if (current != null) pivots.Add(current.Value);

            var userCanonicals = user.Sections
                .Select(s => s.CanonicalTitle)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var highlights = new List<ChatPivotHighlight>();
            foreach (var (pivot, subs) in pivots)
            {
                var found = subs.Where(s => userCanonicals.Contains(s.CanonicalTitle)).ToList();
                var missing = subs.Where(s => !userCanonicals.Contains(s.CanonicalTitle)).ToList();

                highlights.Add(new ChatPivotHighlight
                {
                    PivotTitle = pivot.Title,
                    ExpectedSubtitleCount = subs.Count,
                    FoundSubtitleCount = found.Count,
                    MatchedSubtitleCount = found.Count,
                    MissingSubtitles = missing.Select(s => s.Title).ToList()
                });
            }

            return highlights;
        }

        // ═══════════════════════════════════════════════════════════════
        // RECOMMENDATIONS (v4)
        // ═══════════════════════════════════════════════════════════════

        private static List<string> BuildRecommendations(
            int missingSections, int emptySections, int placeholderSections,
            int emptyTableBodies, int missingTablePatterns,
            string band, int extraSections, int userTableCount, int blueprintTableCount)
        {
            var rec = new List<string>();

            if (missingSections > 0)
                rec.Add($"Add the {missingSections} missing section(s) — all blueprint sections are mandatory in this CDC type.");
            if (emptySections > 0)
                rec.Add($"Fill in the {emptySections} empty section(s) — these have no text content, no sub-sections with content, and no tables.");
            if (placeholderSections > 0)
                rec.Add($"Replace placeholder content in {placeholderSections} section(s) — text like '<<Insert here>>', 'XXX', '<component name>' must be replaced with real project data.");
            if (emptyTableBodies > 0)
                rec.Add($"Fill in {emptyTableBodies} table(s) that have no data rows — the table structure exists but no requirements have been entered yet.");
            if (missingTablePatterns > 0)
                rec.Add($"Add {missingTablePatterns} missing table pattern(s) — each section must contain its expected tables with the correct header structure.");
            if (userTableCount > 0 && userTableCount < blueprintTableCount * 0.5)
                rec.Add($"Document has {userTableCount} tables but blueprint expects ~{blueprintTableCount} — many sections may be missing their requirement/data tables.");
            if (band != "HIGH")
                rec.Add("Overall confidence is below HIGH — manually review the highlighted pivot sections for structural alignment.");
            if (extraSections > 10)
                rec.Add($"The document has {extraSections} extra sections not in the blueprint — verify they are intentional and correctly placed.");

            if (rec.Count == 0)
                rec.Add("All checks passed — document structure matches the blueprint. Consider manual content quality review.");

            return rec;
        }

        // ═══════════════════════════════════════════════════════════════
        // HUMAN SUMMARY
        // ═══════════════════════════════════════════════════════════════

        private static string BuildHumanSummary(ValidationReport report, ChatReport chat, ComparisonResult comparison)
        {
            var parts = new List<string>();
            parts.Add($"Score: {report.Score}% ({chat.Summary.ConfidenceBand}).");
            parts.Add($"Section coverage: {comparison.CoveragePercent:0.#}% ({comparison.Common.Count}/{comparison.ReferenceCount}).");

            if (comparison.MissingInUser.Count > 0)
                parts.Add($"{comparison.MissingInUser.Count} MISSING section(s).");
            if (report.Summary.TrulyEmptySections > 0)
                parts.Add($"{report.Summary.TrulyEmptySections} EMPTY section(s).");
            if (report.Summary.PlaceholderSections > 0)
                parts.Add($"{report.Summary.PlaceholderSections} section(s) still contain PLACEHOLDER text.");
            if (report.Summary.EmptyTableBodies > 0)
                parts.Add($"{report.Summary.EmptyTableBodies} table(s) exist but have NO DATA ROWS — shell only.");
            if (report.Summary.MissingTablePatterns > 0)
                parts.Add($"{report.Summary.MissingTablePatterns} table pattern(s) MISSING ({report.Summary.UserTableCount} user tables vs {report.Summary.TotalBlueprintTables} expected).");

            if (report.IsValid)
                parts.Add("All structural and content checks passed.");
            else
                parts.Add("Action required — see topIssues and recommendations.");

            return string.Join(" ", parts);
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private static (ValidationReport, ChatReport) FailFast(
            ValidationReport report, ChatReport chat, string field, string message)
        {
            report.IsValid = false;
            report.Score = 0;
            report.Issues.Add(new ValidationIssue
            {
                Type = "error", Field = field,
                Message = message
            });
            chat.HumanSummary = $"Validation failed: {message}";
            return (report, chat);
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string TruncateSig(string sig, int max = 60)
        {
            return sig.Length <= max ? sig : sig[..max] + "…";
        }

        /// <summary>
        /// Fuzzy key for table signature matching.
        /// Normalizes synonyms and abbreviations so that different wording
        /// for the same logical table still produces the same key.
        /// Examples:
        ///    "requirement number v|..."  → "requirement number"
        ///    "requirement no v|..."      → "requirement number"  (synonym: no→number)
        ///    "req no v|..."              → "requirement number"  (synonym: req→requirement)
        ///    "on indelible inscription"  → "indelible inscription" (strip leading stop words)
        ///    "type of attribute|..."     → "type attribute"
        /// </summary>
        private static string SigFuzzyKey(string sig)
        {
            if (string.IsNullOrWhiteSpace(sig)) return string.Empty;
            var firstCol = sig.Split('|')[0].Trim().ToLowerInvariant();

            // Normalize known synonyms/abbreviations
            firstCol = Regex.Replace(firstCol, @"\breq\b", "requirement");
            firstCol = Regex.Replace(firstCol, @"\bno\b", "number");
            firstCol = Regex.Replace(firstCol, @"\bnbr\b", "number");

            // Strip leading stop/filler words
            firstCol = Regex.Replace(firstCol, @"^(on|the|of|a|an|in|for|to|and|or)\s+", "");

            var words = firstCol.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 1) // skip single chars like "v"
                .Take(2)
                .ToArray();
            return string.Join(" ", words);
        }

        /// <summary>
        /// Checks if a blueprint table signature represents explanatory/naming-convention
        /// content rather than a real data table. Such tables should be excluded from
        /// the missing-table validation.
        /// </summary>
        private static bool IsExplanatoryTable(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature)) return false;
            var lower = signature.ToLowerInvariant();

            // Tables describing naming conventions (e.g. "APP|: Generic application...|: max 5 characters")
            if (Regex.IsMatch(lower, @"max\s+\d+\s+char")) return true;
            // Tables where columns are just abbreviation labels like "app|sys|doc"
            var cols = lower.Split('|');
            if (cols.Length >= 2 && cols.All(c => c.Trim().Length <= 5)) return true;
            // Tables with "(ex." or "(e.g." examples — typically explanatory
            if (lower.Contains("(ex.") || lower.Contains("(e.g.")) return true;
            // Tables where a column is just ":" (punctuation-only)
            if (cols.Any(c => c.Trim() == ":")) return true;

            return false;
        }

        private async Task<string> LoadResourceAsync(string pathOrUrl, string correlationId)
        {
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return await _sharedHttpClient.GetStringAsync(pathOrUrl);
            }

            var fullPath = Path.IsPathFullyQualified(pathOrUrl)
                ? pathOrUrl
                : Path.Combine(AppContext.BaseDirectory, pathOrUrl);
            return await File.ReadAllTextAsync(fullPath);
        }

        // ═══════════════════════════════════════════════════════════════
        // BLUEPRINT JSON PARSING
        // ═══════════════════════════════════════════════════════════════

        private static List<BlueprintHeading> ExtractBlueprintHeadings(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var headings = new List<BlueprintHeading>();

            if (!doc.RootElement.TryGetProperty("Headings", out var arr))
                throw new InvalidOperationException("Blueprint JSON missing 'Headings' array");

            foreach (var h in arr.EnumerateArray())
            {
                var title = h.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
                var level = h.TryGetProperty("Level", out var l) ? l.GetInt32() : 0;
                var order = h.TryGetProperty("Order", out var o) ? o.GetInt32() : 0;
                var wordCount = h.TryGetProperty("ContentWordCount", out var w) ? w.GetInt32() : 0;

                if (!string.IsNullOrWhiteSpace(title))
                    headings.Add(new BlueprintHeading { Title = title, Level = level, Order = order, ContentWordCount = wordCount });
            }

            return headings;
        }

        private static List<BlueprintTable> ExtractBlueprintTables(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var tables = new List<BlueprintTable>();

            if (!doc.RootElement.TryGetProperty("TableInventory", out var inv)) return tables;
            if (!inv.TryGetProperty("Tables", out var arr)) return tables;

            foreach (var t in arr.EnumerateArray())
            {
                var sectionTitle = t.TryGetProperty("SectionTitle", out var st) ? st.GetString() ?? "" : "";
                var sig = t.TryGetProperty("HeaderSignature", out var hs) ? hs.GetString() ?? "" : "";
                var rowCount = t.TryGetProperty("RowCount", out var rc) ? rc.GetInt32() : 0;
                var sectionOrder = t.TryGetProperty("SectionOrder", out var so)
                    ? (so.ValueKind == JsonValueKind.Number ? (int?)so.GetInt32() : null)
                    : null;

                tables.Add(new BlueprintTable
                {
                    SectionTitle = sectionTitle,
                    HeaderSignature = sig,
                    RowCount = rowCount,
                    SectionOrder = sectionOrder
                });
            }

            return tables;
        }

        private static HashSet<int> ExtractBlueprintEmptyOrders(string json)
        {
            var orders = new HashSet<int>();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("Quality", out var quality)) return orders;
            if (!quality.TryGetProperty("EmptySectionOrders", out var arr)) return orders;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    orders.Add(item.GetInt32());
            }

            return orders;
        }

        // ═══════════════════════════════════════════════════════════════
        // INTERNAL MODELS
        // ═══════════════════════════════════════════════════════════════

        private class BlueprintHeading
        {
            public string Title { get; set; } = "";
            public int Level { get; set; }
            public int Order { get; set; }
            public int ContentWordCount { get; set; }
        }

        private class BlueprintTable
        {
            public string SectionTitle { get; set; } = "";
            public string HeaderSignature { get; set; } = "";
            public int RowCount { get; set; }
            public int? SectionOrder { get; set; }
        }

        private class ExtractedTable
        {
            public int TableIndex { get; set; }
            public int RowCount { get; set; }
            public int ColumnCount { get; set; }
            public List<string> HeaderCells { get; set; } = new();
            public string HeaderSignature { get; set; } = "";
            public int NearestHeadingIndex { get; set; } = -1;
        }
    }
}
