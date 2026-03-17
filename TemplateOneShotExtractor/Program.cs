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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WordOpenXml.Core;
using WordOpenXml.Core.Models;

// Entry point for .NET Isolated Worker with OpenAPI support
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();

namespace TemplateOneShotExtractor.Models
{
    // ═══════════════════════════════════════════════════════════════
    // REQUEST / RESPONSE DTOs
    // ═══════════════════════════════════════════════════════════════

    public class ValidationRequest
    {
        public string TemplateBlueprint { get; set; } = string.Empty;
        public string User { get; set; } = string.Empty;
        /// <summary>
        /// Azure Blob Storage URL (SAS) of the uploaded DOCX document to validate.
        /// </summary>
        public string? DocumentUrl { get; set; }
    }

    public class ValidationIssue
    {
        public string Type { get; set; } = string.Empty;
        public string Field { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
    }

    public class ValidationSummary
    {
        public int TotalBlueprintSections { get; set; }
        public int MatchedSections { get; set; }
        public int MissingSections { get; set; }
        public int TrulyEmptySections { get; set; }
        public int TotalBlueprintTables { get; set; }
        public int MatchedTables { get; set; }
        public int MissingTables { get; set; }
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

    // ═══════════════════════════════════════════════════════════════
    // CHAT REPORT — evaluator-focused, actionable output
    // Only missing sections, truly empty sections, missing tables,
    // recommendations. No noise about what already exists.
    // ═══════════════════════════════════════════════════════════════

    public class ChatMetadata
    {
        public string Tool { get; set; } = "TemplateOneShotExtractor-AzureFunction";
        public string Version { get; set; } = "3.0.0";
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
        public double TableCoverage { get; set; }
        public string ConfidenceBand { get; set; } = "LOW";
    }

    public class ChatPivotHighlight
    {
        public string PivotTitle { get; set; } = string.Empty;
        public int ExpectedSubtitleCount { get; set; }
        public int FoundSubtitleCount { get; set; }
        public int MatchedSubtitleCount { get; set; }
        public List<string> MissingSubtitles { get; set; } = new();
        public string Severity { get; set; } = "Low";
    }

    public class ChatTopIssue
    {
        public string Type { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> Items { get; set; } = new();
        public string Severity { get; set; } = string.Empty;
    }

    public class ChatTableIssue
    {
        public string SectionTitle { get; set; } = string.Empty;
        public string ExpectedSignature { get; set; } = string.Empty;
        public int ExpectedRowCount { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class ChatReport
    {
        public ChatMetadata Metadata { get; set; } = new();
        public ChatSummary Summary { get; set; } = new();
        public List<ChatTopIssue> TopIssues { get; set; } = new();
        public List<ChatPivotHighlight> PivotHighlights { get; set; } = new();
        public List<ChatTableIssue> MissingTables { get; set; } = new();
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

        public ValidateTemplateFunction(ILogger<ValidateTemplateFunction> logger)
        {
            _logger = logger;
        }

        [Function("ValidateTemplate")]
        [OpenApiOperation(
            operationId: "ValidateTemplate",
            tags: new[] { "Template Validation" },
            Summary = "Validate a CDC document against blueprint specifications",
            Description = "Downloads a user DOCX, parses sections and tables, compares against the blueprint template, and returns an actionable report focusing on what is MISSING or needs to be changed.",
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
            _logger.LogInformation("ValidateTemplate triggered. CId={CId}", correlationId);

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
        // VALIDATION ENGINE v3 — Specialist CDC Analysis
        // ═══════════════════════════════════════════════════════════════
        //
        // Key design decisions:
        // 1. ALL blueprint sections are REQUIRED — no must/should/optional
        //    tiers (in CDC domain, everything in the template is mandatory)
        // 2. Empty section = section has no content AND no child sub-sections
        //    with content. A parent heading (e.g. "SCOPE" Level 1) that has
        //    Level 2+ children with content is NOT empty.
        // 3. Table comparison: extract tables from user DOCX, compare header
        //    signatures against blueprint TableInventory
        // 4. Report focuses ONLY on what's MISSING or needs to change — the
        //    evaluator doesn't need to see what already exists/passes.
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

            // ── STEP 2: Parse user DOCX — sections (WordParser) ──────────
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

            // ── STEP 3: Parse user DOCX — tables ─────────────────────────
            var userTables = ExtractTablesFromDocx(docxBytes);
            _logger.LogInformation("[{CId}] Extracted {N} tables from user DOCX", correlationId, userTables.Count);

            // ── STEP 4: Analyze sections with CdcAnalysisService ─────────
            var svc = new CdcAnalysisService();
            var userAnalysis = svc.Analyze("user-document", rawSections);
            _logger.LogInformation("[{CId}] User analysis: {N} normalized sections", correlationId, userAnalysis.Sections.Count);

            // ── STEP 5: Load blueprint JSON ──────────────────────────────
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
            _logger.LogInformation("[{CId}] Blueprint: {H} headings, {T} tables",
                correlationId, blueprintHeadings.Count, blueprintTables.Count);

            // Build reference DocumentAnalysis from blueprint headings
            var refSections = blueprintHeadings.Select(h => new Section
            {
                Level = h.Level, Title = h.Title, Content = string.Empty
            }).ToList();
            var refAnalysis = svc.Analyze("blueprint", refSections);

            // ── STEP 6: Section comparison ───────────────────────────────
            var comparison = svc.Compare(userAnalysis, refAnalysis);
            _logger.LogInformation("[{CId}] Sections: {Common} common, {Missing} missing, Coverage={Cov:F1}%",
                correlationId, comparison.Common.Count, comparison.MissingInUser.Count, comparison.CoveragePercent);

            // ── STEP 7: Truly empty section detection ────────────────────
            // A section is "truly empty" ONLY if it exists in the user doc,
            // is also expected in the blueprint, has no own content AND none
            // of its child sub-sections have content.
            var allTrulyEmpty = FindTrulyEmptySections(userAnalysis.Sections);
            // Only flag sections that exist in the blueprint (common sections)
            var commonSet = new HashSet<string>(comparison.Common, StringComparer.OrdinalIgnoreCase);
            var trulyEmptySections = allTrulyEmpty
                .Where(s => commonSet.Contains(s.CanonicalTitle))
                .ToList();
            _logger.LogInformation("[{CId}] Truly empty sections (in blueprint): {N}", correlationId, trulyEmptySections.Count);

            // ── STEP 8: Table comparison ─────────────────────────────────
            // Build set of user table signatures for matching
            var userSigSet = userTables.Select(t => t.HeaderSignature)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingTableIssues = new List<ChatTableIssue>();
            int matchedTableCount = 0;
            // Only compare tables assigned to a blueprint section
            var sectionTables = blueprintTables.Where(t => t.SectionOrder.HasValue).ToList();
            foreach (var bt in sectionTables)
            {
                if (userSigSet.Contains(bt.HeaderSignature))
                {
                    matchedTableCount++;
                }
                else
                {
                    missingTableIssues.Add(new ChatTableIssue
                    {
                        SectionTitle = bt.SectionTitle,
                        ExpectedSignature = bt.HeaderSignature,
                        ExpectedRowCount = bt.RowCount,
                        Status = "missing"
                    });
                }
            }
            int missingTableCount = missingTableIssues.Count;
            double tableCoverage = sectionTables.Count > 0
                ? Math.Round((double)matchedTableCount / sectionTables.Count * 100, 2) : 100;

            _logger.LogInformation("[{CId}] Tables: {Matched}/{Total} matched, {Missing} missing, Coverage={Cov:F1}%",
                correlationId, matchedTableCount, sectionTables.Count, missingTableCount, tableCoverage);

            // ── STEP 9: Build issues list (ONLY missing/actionable) ──────
            // Missing sections — ALL are required in CDC
            foreach (var missing in comparison.MissingInUser)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "MissingSection",
                    Field = missing,
                    Message = $"Required section '{missing}' is missing from the document",
                    Severity = "critical"
                });
            }

            // Truly empty sections
            foreach (var empty in trulyEmptySections)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "EmptySection",
                    Field = empty.Title,
                    Message = $"Section '{empty.Title}' exists but has no content and no sub-sections with content",
                    Severity = "high"
                });
            }

            // Missing tables
            foreach (var mt in missingTableIssues)
            {
                report.Issues.Add(new ValidationIssue
                {
                    Type = "MissingTable",
                    Field = mt.SectionTitle,
                    Message = $"Expected table [{mt.ExpectedSignature}] in section '{mt.SectionTitle}' — not found in document",
                    Severity = "high"
                });
            }

            // ── STEP 10: Score calculation ───────────────────────────────
            // Weighted: 60% section coverage, 20% empty penalty, 20% table coverage
            double sectionScore = comparison.CoveragePercent;
            double emptyPenalty = refAnalysis.Sections.Count > 0
                ? (double)trulyEmptySections.Count / refAnalysis.Sections.Count * 100 : 0;
            double tableScore = tableCoverage;

            int finalScore = Math.Max(0, Math.Min(100,
                (int)Math.Round(sectionScore * 0.6 + tableScore * 0.2 + (100 - emptyPenalty) * 0.2)));

            report.Score = finalScore;
            report.Summary = new ValidationSummary
            {
                TotalBlueprintSections = refAnalysis.Sections.Count,
                MatchedSections = comparison.Common.Count,
                MissingSections = comparison.MissingInUser.Count,
                TrulyEmptySections = trulyEmptySections.Count,
                TotalBlueprintTables = sectionTables.Count,
                MatchedTables = matchedTableCount,
                MissingTables = missingTableCount
            };
            report.IsValid = comparison.MissingInUser.Count == 0
                          && trulyEmptySections.Count == 0
                          && missingTableCount == 0;

            // ── STEP 11: Build ChatReport ────────────────────────────────
            var validatedAt = DateTime.UtcNow;
            chat.Metadata.ValidatedAtUtc = validatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            chat.Metadata.DurationMs = (long)(validatedAt - startedAt).TotalMilliseconds;

            // Pivot highlights (only pivots with missing subtitles)
            var pivotHighlights = BuildPivotHighlights(refAnalysis, userAnalysis);
            int totalExpectedSubs = pivotHighlights.Sum(p => p.ExpectedSubtitleCount);
            int totalMatchedSubs = pivotHighlights.Sum(p => p.MatchedSubtitleCount);
            double subtitleCoverage = totalExpectedSubs > 0
                ? Math.Round((double)totalMatchedSubs / totalExpectedSubs * 100, 2) : 100;

            string band = sectionScore >= 80 && subtitleCoverage >= 70 && tableCoverage >= 70 ? "HIGH"
                        : sectionScore >= 50 ? "MEDIUM" : "LOW";

            chat.Summary = new ChatSummary
            {
                FinalScore = finalScore,
                SectionCoverage = comparison.CoveragePercent,
                SubtitleCoverage = subtitleCoverage,
                TableCoverage = tableCoverage,
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
                    Items = comparison.MissingInUser.ToList(),
                    Severity = "Critical"
                });
            }
            if (trulyEmptySections.Count > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "EmptySections",
                    Count = trulyEmptySections.Count,
                    Items = trulyEmptySections.Select(s => s.Title).ToList(),
                    Severity = "High"
                });
            }
            if (missingTableCount > 0)
            {
                chat.TopIssues.Add(new ChatTopIssue
                {
                    Type = "MissingTables",
                    Count = missingTableCount,
                    Items = missingTableIssues.Select(t => $"[{t.ExpectedSignature}] in '{t.SectionTitle}'").ToList(),
                    Severity = "High"
                });
            }

            // Pivot highlights (only those with problems)
            chat.PivotHighlights = pivotHighlights
                .Where(p => p.MissingSubtitles.Count > 0)
                .OrderByDescending(p => p.MissingSubtitles.Count)
                .ToList();

            // Missing tables detail
            chat.MissingTables = missingTableIssues;

            // Recommendations
            chat.Recommendations = BuildRecommendations(
                comparison.MissingInUser.Count, trulyEmptySections.Count,
                missingTableCount, band, comparison.ExtraInUser.Count);

            // Human summary — evaluator-focused
            chat.HumanSummary = BuildHumanSummary(report, chat, comparison);

            _logger.LogInformation(
                "[{CId}] DONE: Score={Score}%, Band={Band}, MissingSections={MS}, EmptySections={ES}, MissingTables={MT}",
                correlationId, finalScore, band, comparison.MissingInUser.Count,
                trulyEmptySections.Count, missingTableCount);

            return (report, chat);
        }

        // ═══════════════════════════════════════════════════════════════
        // TRULY EMPTY SECTION DETECTION
        // ═══════════════════════════════════════════════════════════════
        // A section is "truly empty" ONLY if:
        //   - It has no direct content (0 words in its own Content field)
        //   - AND none of its child sub-sections (higher Level values that
        //     appear before the next section at the same or lower level)
        //     have any content either.
        //
        // Example: "SCOPE" (Level 1) has Content="" but has children:
        //   "SYSTEM DEVELOPMENT CONTEXT" (Level 2, 176 words)
        //   "GENERAL DESCRIPTION" (Level 2, ...)
        //   → SCOPE is NOT truly empty because children have content.
        // ═══════════════════════════════════════════════════════════════

        private static List<NormalizedSection> FindTrulyEmptySections(IReadOnlyList<NormalizedSection> sections)
        {
            var result = new List<NormalizedSection>();

            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                int ownWords = CountWords(section.Content);

                if (ownWords > 0) continue; // Has direct content — not empty

                // Check children: all sections after this one that have a higher Level
                // (deeper nesting) until we hit a section at the same or lower level.
                bool childrenHaveContent = false;
                for (int j = i + 1; j < sections.Count; j++)
                {
                    if (sections[j].Level <= section.Level)
                        break; // Hit a sibling or parent — stop

                    if (CountWords(sections[j].Content) > 0)
                    {
                        childrenHaveContent = true;
                        break;
                    }
                }

                if (!childrenHaveContent)
                {
                    // Truly empty: no own content AND no children with content
                    result.Add(section);
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // TABLE EXTRACTION FROM USER DOCX
        // ═══════════════════════════════════════════════════════════════
        // Parses all <w:tbl> elements from the DOCX body, extracts the
        // header row cells (first row of each table), and builds a
        // normalized header signature (lowercase, pipe-separated) for
        // comparison against the blueprint's TableInventory.
        // ═══════════════════════════════════════════════════════════════

        private static List<ExtractedTable> ExtractTablesFromDocx(byte[] docxBytes)
        {
            var tables = new List<ExtractedTable>();

            using var stream = new MemoryStream(docxBytes);
            using var document = WordprocessingDocument.Open(stream, false);
            var body = document.MainDocumentPart?.Document?.Body;
            if (body == null) return tables;

            int tableIndex = 0;
            foreach (var table in body.Descendants<Table>())
            {
                tableIndex++;
                var rows = table.Elements<TableRow>().ToList();
                if (rows.Count == 0) continue;

                // Extract header cells from first row
                var headerCells = rows[0].Elements<TableCell>()
                    .Select(cell => NormalizeTableCell(cell.InnerText))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (headerCells.Count == 0) continue;

                // Build normalized header signature (lowercase, pipe-separated)
                var signature = string.Join("|", headerCells.Select(c =>
                    Regex.Replace(c.ToLowerInvariant().Trim(), @"\s+", " ")));

                tables.Add(new ExtractedTable
                {
                    TableIndex = tableIndex,
                    RowCount = rows.Count,
                    ColumnCount = rows.Max(r => r.Elements<TableCell>().Count()),
                    HeaderCells = headerCells,
                    HeaderSignature = signature
                });
            }

            return tables;
        }

        private static string NormalizeTableCell(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return Regex.Replace(text.Trim(), @"\s+", " ");
        }

        // ═══════════════════════════════════════════════════════════════
        // PIVOT HIGHLIGHTS BUILDER
        // Groups Level 1 headings as pivots, Level 2+ as their subtitles.
        // Compares user subtitles vs reference subtitles per pivot.
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

                string severity = missing.Count > 0 ? "High"
                                : subs.Count > 0 && found.Count < subs.Count ? "Medium"
                                : "Low";

                highlights.Add(new ChatPivotHighlight
                {
                    PivotTitle = pivot.Title,
                    ExpectedSubtitleCount = subs.Count,
                    FoundSubtitleCount = found.Count,
                    MatchedSubtitleCount = found.Count,
                    MissingSubtitles = missing.Select(s => s.Title).ToList(),
                    Severity = severity
                });
            }

            return highlights;
        }

        // ═══════════════════════════════════════════════════════════════
        // RECOMMENDATIONS — heuristic, actionable
        // ═══════════════════════════════════════════════════════════════

        private static List<string> BuildRecommendations(
            int missingSections, int emptySections, int missingTables,
            string band, int extraSections)
        {
            var rec = new List<string>();

            if (missingSections > 0)
                rec.Add($"Add the {missingSections} missing section(s) — all blueprint sections are mandatory in this CDC type.");
            if (emptySections > 0)
                rec.Add($"Fill in the {emptySections} empty section(s) with content or add sub-sections — sections with no content at any level are flagged.");
            if (missingTables > 0)
                rec.Add($"Add the {missingTables} missing table(s) — each section must contain its expected tables with the correct header structure.");
            if (band != "HIGH")
                rec.Add("Confidence is below HIGH — manually review the highlighted pivot sections for structural alignment.");
            if (extraSections > 5)
                rec.Add($"The document has {extraSections} extra sections not in the blueprint — verify they are intentional and not duplicates of expected sections.");

            if (rec.Count == 0)
                rec.Add("All checks passed — document structure matches the blueprint. Consider manual content quality review.");

            return rec;
        }

        // ═══════════════════════════════════════════════════════════════
        // HUMAN SUMMARY — evaluator-focused, no noise
        // ═══════════════════════════════════════════════════════════════

        private static string BuildHumanSummary(ValidationReport report, ChatReport chat, ComparisonResult comparison)
        {
            var parts = new List<string>();
            parts.Add($"Score: {report.Score}% ({chat.Summary.ConfidenceBand}).");
            parts.Add($"Section coverage: {comparison.CoveragePercent:0.#}% ({comparison.Common.Count}/{comparison.ReferenceCount}).");

            if (comparison.MissingInUser.Count > 0)
                parts.Add($"{comparison.MissingInUser.Count} MISSING section(s).");
            if (report.Summary.TrulyEmptySections > 0)
                parts.Add($"{report.Summary.TrulyEmptySections} EMPTY section(s) needing content.");
            if (report.Summary.MissingTables > 0)
                parts.Add($"{report.Summary.MissingTables} MISSING table(s).");
            if (report.Summary.MissingTables == 0 && report.Summary.TotalBlueprintTables > 0)
                parts.Add($"Table coverage: {chat.Summary.TableCoverage:0.#}% ({report.Summary.MatchedTables}/{report.Summary.TotalBlueprintTables}).");

            if (report.IsValid)
                parts.Add("All structural checks passed.");
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
                Message = message, Severity = "critical"
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
        }
    }
}
