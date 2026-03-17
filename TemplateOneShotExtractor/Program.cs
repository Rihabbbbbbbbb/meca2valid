using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// Request model for template validation
    /// </summary>
    public class ValidationRequest
    {
        /// <summary>
        /// The blueprint template identifier or path
        /// </summary>
        public string TemplateBlueprint { get; set; } = string.Empty;

        /// <summary>
        /// The user identifier requesting validation
        /// </summary>
        public string User { get; set; } = string.Empty;

        /// <summary>
        /// Azure Blob Storage URL (SAS) of the uploaded DOCX document to validate.
        /// Provided by Power Automate after uploading the user's file to Blob Storage.
        /// </summary>
        public string? DocumentUrl { get; set; }
    }

    /// <summary>
    /// Validation issue (error or warning)
    /// </summary>
    public class ValidationIssue
    {
        /// <summary>
        /// Issue type: error or warning
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Section or field where issue was found
        /// </summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>
        /// Detailed message
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Severity: critical, high, medium, low
        /// </summary>
        public string Severity { get; set; } = string.Empty;
    }

    /// <summary>
    /// Validation summary statistics
    /// </summary>
    public class ValidationSummary
    {
        /// <summary>
        /// Total number of checks performed
        /// </summary>
        public int TotalChecks { get; set; }

        /// <summary>
        /// Number of checks that passed
        /// </summary>
        public int Passed { get; set; }

        /// <summary>
        /// Number of errors found
        /// </summary>
        public int Failed { get; set; }

        /// <summary>
        /// Number of warnings
        /// </summary>
        public int Warnings { get; set; }
    }

    /// <summary>
    /// Section validation details
    /// </summary>
    public class SectionDetail
    {
        /// <summary>
        /// Section name
        /// </summary>
        public string Section { get; set; } = string.Empty;

        /// <summary>
        /// Validation status: valid, invalid, warning
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Checks performed on this section
        /// </summary>
        public List<string> Checks { get; set; } = new List<string>();
    }

    /// <summary>
    /// Detailed validation report
    /// </summary>
    public class ValidationReport
    {
        /// <summary>
        /// Overall validation status
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Validation score (0-100)
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// Summary statistics
        /// </summary>
        public ValidationSummary Summary { get; set; } = new ValidationSummary();

        /// <summary>
        /// List of errors found
        /// </summary>
        public List<ValidationIssue> Errors { get; set; } = new List<ValidationIssue>();

        /// <summary>
        /// List of warnings
        /// </summary>
        public List<ValidationIssue> Warnings { get; set; } = new List<ValidationIssue>();

        /// <summary>
        /// Section-by-section details
        /// </summary>
        public List<SectionDetail> Details { get; set; } = new List<SectionDetail>();
    }

    /// <summary>
    /// Response model for successful validation - Optimized for Copilot Studio
    /// </summary>
    public class ValidationResponse
    {
        /// <summary>
        /// Indicates if the operation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable status message
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Timestamp of validation (ISO 8601)
        /// </summary>
        public string ValidatedAt { get; set; } = string.Empty;

        /// <summary>
        /// User who requested validation
        /// </summary>
        public string User { get; set; } = string.Empty;

        /// <summary>
        /// Template blueprint used
        /// </summary>
        public string Template { get; set; } = string.Empty;

        /// <summary>
        /// Detailed validation report
        /// </summary>
        public ValidationReport Report { get; set; } = new ValidationReport();

        /// <summary>
        /// Unique correlation ID for this validation request (for tracing)
        /// </summary>
        public string CorrelationId { get; set; } = string.Empty;

        /// <summary>
        /// Pre-formatted Copilot Studio display report.
        /// Use ChatReport.HumanSummary, ChatReport.TopIssues, ChatReport.Recommendations
        /// directly in Copilot Studio topic messages — no parsing needed.
        /// </summary>
        public ChatReport ChatReport { get; set; } = new ChatReport();
    }

    /// <summary>
    /// Error response model
    /// </summary>
    public class ErrorResponse
    {
        public string Error { get; set; } = string.Empty;
        public string? Details { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // CHAT REPORT — matches chat-report-template.json format exactly
    // Copilot Studio display-ready formatted output
    // ═══════════════════════════════════════════════════════════════

    public class ChatMetadata
    {
        public string Tool { get; set; } = "TemplateOneShotExtractor-AzureFunction";
        public string Version { get; set; } = "2.0.0";
        public string StartedAtUtc { get; set; } = string.Empty;
        public string ValidatedAtUtc { get; set; } = string.Empty;
        public long DurationMs { get; set; }
        public string TemplatePath { get; set; } = string.Empty;
        public string UserDocumentPath { get; set; } = string.Empty;
    }

    public class ChatSummary
    {
        public double FinalScore { get; set; }
        public double PivotCoverage { get; set; }
        public double SubtitleRecall { get; set; }
        public double SubtitlePrecision { get; set; }
        public double OrderScore { get; set; }
        public string ConfidenceBand { get; set; } = "LOW";
    }

    public class ChatPolicyCompliance
    {
        public List<string> MissingSections { get; set; } = new();
        public int TotalSections { get; set; }
    }

    public class ChatEmptySectionViolation
    {
        public int Order { get; set; }
        public string Title { get; set; } = string.Empty;
        public string CanonicalTitle { get; set; } = string.Empty;
        public string Requirement { get; set; } = string.Empty;
    }

    public class ChatQualityGates
    {
        public bool RequiresNotApplicableForEmptySections { get; set; }
        public bool RedTextDetected { get; set; }
        public List<ChatEmptySectionViolation> EmptySectionViolations { get; set; } = new();
    }

    public class ChatPivotHighlight
    {
        public string PivotTitle { get; set; } = string.Empty;
        public int TemplateSubtitleCount { get; set; }
        public int UserSubtitleCount { get; set; }
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

    /// <summary>
    /// Pre-formatted Copilot Studio ready report matching chat-report-template.json.
    /// </summary>
    public class ChatReport
    {
        public ChatMetadata Metadata { get; set; } = new();
        public ChatSummary Summary { get; set; } = new();
        public ChatPolicyCompliance PolicyCompliance { get; set; } = new();
        public ChatQualityGates QualityGates { get; set; } = new();
        public List<ChatPivotHighlight> PivotHighlights { get; set; } = new();
        public List<ChatTopIssue> TopIssues { get; set; } = new();
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
        Summary = "Validate a template against blueprint specifications",
        Description = "This endpoint validates a user-provided template against predefined blueprint specifications and generates a validation report.",
        Visibility = OpenApiVisibilityType.Important
    )]
    [OpenApiRequestBody(
        contentType: "application/json",
        bodyType: typeof(ValidationRequest),
        Required = true,
        Description = "Validation request containing template blueprint and user information"
    )]
    [OpenApiResponseWithBody(
        statusCode: HttpStatusCode.OK,
        contentType: "application/json",
        bodyType: typeof(ValidationResponse),
        Summary = "Validation successful",
        Description = "Template validation completed successfully and report generated"
    )]
    [OpenApiResponseWithBody(
        statusCode: HttpStatusCode.BadRequest,
        contentType: "application/json",
        bodyType: typeof(ErrorResponse),
        Summary = "Bad request",
        Description = "Invalid request parameters or malformed JSON"
    )]
    [OpenApiResponseWithBody(
        statusCode: HttpStatusCode.InternalServerError,
        contentType: "application/json",
        bodyType: typeof(ErrorResponse),
        Summary = "Internal server error",
        Description = "An unexpected error occurred during validation"
    )]
    [OpenApiSecurity("function_key", SecuritySchemeType.ApiKey, Name = "code", In = OpenApiSecurityLocationType.Query)]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        _logger.LogInformation("ValidateTemplate function triggered. CorrelationId={CorrelationId}", correlationId);

        try
        {
            // Parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogDebug("[{CorrelationId}] Request body received: {RequestBody}", correlationId, requestBody);

            Dictionary<string, string>? argsMap;
            try
            {
                argsMap = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize request body");
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new ErrorResponse 
                { 
                    Error = "Invalid JSON format in request body",
                    Details = ex.Message
                });
                return badResponse;
            }

            // Validate required arguments
            if (argsMap == null || !argsMap.ContainsKey("templateBlueprint") || !argsMap.ContainsKey("user"))
            {
                _logger.LogWarning("Missing required fields in request");
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new ErrorResponse 
                { 
                    Error = "Invalid request body. Required fields: templateBlueprint, user",
                    Details = "Both 'templateBlueprint' and 'user' fields are mandatory"
                });
                return badResponse;
            }

            // Resolve blueprint from environment or request
            var blueprintPath = Environment.GetEnvironmentVariable("BLUEPRINT_PATH") ?? argsMap["templateBlueprint"];
            var userPath = argsMap["user"];
            var documentUrl = argsMap.ContainsKey("documentUrl") ? argsMap["documentUrl"] : null;

            // documentUrl is REQUIRED for real validation
            if (string.IsNullOrWhiteSpace(documentUrl))
            {
                _logger.LogWarning("[{CorrelationId}] Missing required field: documentUrl", correlationId);
                var noDocResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await noDocResponse.WriteAsJsonAsync(new ErrorResponse
                {
                    Error = "Missing required field: documentUrl",
                    Details = "A valid HTTPS URL to a .docx document is required. Upload your document to Azure Blob Storage and provide the URL (with SAS token if needed)."
                });
                return noDocResponse;
            }

            _logger.LogInformation("[{CorrelationId}] Processing validation for user: {User}, blueprint: {Blueprint}, documentUrl: {DocUrl}",
                correlationId, userPath, blueprintPath, documentUrl);

            // Perform REAL template validation using WordOpenXml.Core pipeline
            var (validationReport, chatReport) = await PerformValidationAsync(blueprintPath, documentUrl, correlationId);

            _logger.LogInformation("[{CorrelationId}] Validation completed for user: {User} - IsValid: {IsValid}, Score: {Score}",
                correlationId, userPath, validationReport.IsValid, validationReport.Score);
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new ValidationResponse 
            { 
                Success = true,
                Message = validationReport.IsValid 
                    ? "Template validation completed successfully" 
                    : "Template validation completed with errors",
                ValidatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                User = userPath,
                Template = blueprintPath,
                Report = validationReport,
                CorrelationId = correlationId,
                ChatReport = chatReport
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred during validation");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new ErrorResponse 
            { 
                Error = "An error occurred during validation",
                Details = ex.Message
            });
            return errorResponse;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // REAL VALIDATION ENGINE — Uses WordOpenXml.Core (proven library)
    // WordParser.Parse → CdcAnalysisService.Analyze → Compare
    // Produces chat-report-template.json format output
    // ═══════════════════════════════════════════════════════════════

    private async Task<(ValidationReport Report, ChatReport Chat)> PerformValidationAsync(
        string blueprintPathOrUrl, string documentUrl, string correlationId)
    {
        var startedAt = DateTime.UtcNow;

        var report = new ValidationReport
        {
            Summary = new ValidationSummary(),
            Errors = new List<ValidationIssue>(),
            Warnings = new List<ValidationIssue>(),
            Details = new List<SectionDetail>()
        };

        var chat = new ChatReport
        {
            Metadata = new ChatMetadata
            {
                StartedAtUtc = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                TemplatePath = blueprintPathOrUrl,
                UserDocumentPath = documentUrl
            }
        };

        // ── STEP 1: Download user DOCX from documentUrl ──────────────
        byte[] docxBytes;
        try
        {
            _logger.LogInformation("[{CId}] Downloading document from: {Url}", correlationId, documentUrl);
            docxBytes = await _sharedHttpClient.GetByteArrayAsync(documentUrl);
            _logger.LogInformation("[{CId}] Document downloaded: {Size} bytes", correlationId, docxBytes.Length);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{CId}] Failed to download document from {Url}", correlationId, documentUrl);
            report.IsValid = false; report.Score = 0;
            report.Errors.Add(new ValidationIssue { Type = "error", Field = "documentUrl",
                Message = $"Cannot download document: {ex.StatusCode} — {ex.Message}", Severity = "critical" });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            chat.HumanSummary = $"Validation failed: cannot download document ({ex.StatusCode}).";
            return (report, chat);
        }
        catch (TaskCanceledException)
        {
            report.IsValid = false; report.Score = 0;
            report.Errors.Add(new ValidationIssue { Type = "error", Field = "documentUrl",
                Message = "Document download timed out after 60 seconds", Severity = "critical" });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            chat.HumanSummary = "Validation failed: document download timed out.";
            return (report, chat);
        }

        // ── STEP 2: Parse user DOCX with WordParser (proven library) ─
        IReadOnlyList<Section> rawSections;
        try
        {
            var parser = new WordParser();
            rawSections = parser.Parse(docxBytes);
            _logger.LogInformation("[{CId}] WordParser extracted {Count} raw sections", correlationId, rawSections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CId}] Failed to parse DOCX with WordParser", correlationId);
            report.IsValid = false; report.Score = 0;
            report.Errors.Add(new ValidationIssue { Type = "error", Field = "document",
                Message = $"Cannot parse DOCX file: {ex.Message}", Severity = "critical" });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            chat.HumanSummary = $"Validation failed: cannot parse DOCX ({ex.Message}).";
            return (report, chat);
        }

        if (rawSections.Count == 0)
        {
            report.IsValid = false; report.Score = 0;
            report.Errors.Add(new ValidationIssue { Type = "error", Field = "document",
                Message = "Document contains no identifiable headings or sections", Severity = "critical" });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            chat.HumanSummary = "Validation failed: document contains no headings.";
            return (report, chat);
        }

        // ── STEP 3: Analyze user document with CdcAnalysisService ────
        var analysisService = new CdcAnalysisService();
        var userAnalysis = analysisService.Analyze("user-document", rawSections);
        _logger.LogInformation("[{CId}] CdcAnalysisService: {Raw} raw → {Filtered} filtered out → {Norm} normalized sections",
            correlationId, userAnalysis.RawSectionCount, userAnalysis.FilteredOutCount, userAnalysis.Sections.Count);

        // ── STEP 4: Load blueprint JSON and build reference DocumentAnalysis ─
        DocumentAnalysis referenceAnalysis;
        List<BlueprintHeading> blueprintHeadings;
        try
        {
            var blueprintJson = await LoadResourceAsync(blueprintPathOrUrl, correlationId);
            blueprintHeadings = ExtractBlueprintHeadings(blueprintJson);
            _logger.LogInformation("[{CId}] Blueprint loaded: {Count} headings", correlationId, blueprintHeadings.Count);

            // Convert blueprint headings to Section objects, then let CdcAnalysisService
            // canonicalize them with the SAME logic used for the user document
            var blueprintSections = blueprintHeadings.Select(h => new Section
            {
                Level = h.Level,
                Title = h.Title,
                Content = string.Empty
            }).ToList();
            referenceAnalysis = analysisService.Analyze("blueprint", blueprintSections);
            _logger.LogInformation("[{CId}] Reference analysis: {Count} normalized sections", correlationId, referenceAnalysis.Sections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CId}] Failed to load blueprint from {Path}", correlationId, blueprintPathOrUrl);
            report.IsValid = false; report.Score = 0;
            report.Errors.Add(new ValidationIssue { Type = "error", Field = "blueprint",
                Message = $"Cannot load blueprint: {ex.Message}", Severity = "critical" });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            chat.HumanSummary = $"Validation failed: cannot load blueprint ({ex.Message}).";
            return (report, chat);
        }

        // ── STEP 5: Compare user vs reference using CdcAnalysisService ─
        var comparison = analysisService.Compare(userAnalysis, referenceAnalysis);
        _logger.LogInformation("[{CId}] Comparison: {Ref} reference, {User} user, {Common} common, {Missing} missing, Coverage={Cov:F1}%",
            correlationId, comparison.ReferenceCount, comparison.UserCount,
            comparison.Common.Count, comparison.MissingInUser.Count, comparison.CoveragePercent);

        // ── STEP 6: Load policy JSON ────────────────────────────────
        PolicyConfig policy;
        try
        {
            var policyPathOrUrl = Environment.GetEnvironmentVariable("POLICY_PATH") ?? "template-policy.json";
            var policyJson = await LoadResourceAsync(policyPathOrUrl, correlationId);
            policy = JsonSerializer.Deserialize<PolicyConfig>(policyJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new PolicyConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{CId}] Policy load failed, continuing with blueprint-only comparison", correlationId);
            policy = new PolicyConfig();
        }

        // Uppercase policy canonical titles to match CdcAnalysisService convention
        var mustTitles = policy.MustCanonicalTitles.Select(t => t.ToUpperInvariant()).ToList();
        var shouldTitles = policy.ShouldCanonicalTitles.Select(t => t.ToUpperInvariant()).ToList();
        var optionalTitles = policy.OptionalCanonicalTitles.Select(t => t.ToUpperInvariant()).ToList();
        int totalPolicySections = mustTitles.Count + shouldTitles.Count + optionalTitles.Count;

        // ── STEP 7: Build ValidationReport details ───────────────────
        var userCanonicalSet = userAnalysis.Sections
            .Select(s => s.CanonicalTitle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int totalChecks = 0;
        int passedChecks = 0;

        foreach (var refSection in referenceAnalysis.Sections)
        {
            totalChecks++;
            bool found = userCanonicalSet.Contains(refSection.CanonicalTitle);

            if (found)
            {
                passedChecks++;
                var userSection = userAnalysis.Sections.FirstOrDefault(s =>
                    s.CanonicalTitle.Equals(refSection.CanonicalTitle, StringComparison.OrdinalIgnoreCase));
                int wordCount = CountWords(userSection?.Content ?? "");
                string status = wordCount >= 5 ? "valid" : "warning";

                report.Details.Add(new SectionDetail
                {
                    Section = refSection.Title,
                    Status = status,
                    Checks = new List<string>
                    {
                        "Section found in user document",
                        wordCount > 0 ? $"Content: {wordCount} words" : "Section is empty (no content)"
                    }
                });

                if (wordCount < 5)
                {
                    report.Warnings.Add(new ValidationIssue
                    {
                        Type = "warning", Field = refSection.Title,
                        Message = $"Section '{refSection.Title}' is present but has insufficient content ({wordCount} words)",
                        Severity = "low"
                    });
                }
            }
            else
            {
                report.Details.Add(new SectionDetail
                {
                    Section = refSection.Title,
                    Status = "missing",
                    Checks = new List<string> { "Section NOT found in user document" }
                });
            }
        }

        // ── STEP 8: Policy compliance ────────────────────────────────
        var missingMust = new List<string>();
        var missingShould = new List<string>();
        foreach (var must in mustTitles)
        {
            if (!userCanonicalSet.Contains(must))
            {
                missingMust.Add(must);
                report.Errors.Add(new ValidationIssue
                {
                    Type = "error", Field = must,
                    Message = $"REQUIRED section '{must}' is missing (policy: MUST have)",
                    Severity = "critical"
                });
            }
        }
        foreach (var should in shouldTitles)
        {
            if (!userCanonicalSet.Contains(should))
            {
                missingShould.Add(should);
                report.Warnings.Add(new ValidationIssue
                {
                    Type = "warning", Field = should,
                    Message = $"Recommended section '{should}' is missing (policy: SHOULD have)",
                    Severity = "medium"
                });
            }
        }

        // Extra sections in user doc not in blueprint
        if (comparison.ExtraInUser.Count > 0)
        {
            report.Details.Add(new SectionDetail
            {
                Section = "Extra Sections (not in blueprint)",
                Status = "info",
                Checks = comparison.ExtraInUser.Select(s => $"Extra: {s}").ToList()
            });
        }

        // ── STEP 9: Quality gates — empty section violations ─────────
        var emptySectionViolations = new List<ChatEmptySectionViolation>();
        int orderIdx = 0;
        foreach (var refSection in referenceAnalysis.Sections)
        {
            orderIdx++;
            if (userCanonicalSet.Contains(refSection.CanonicalTitle))
            {
                var userSection = userAnalysis.Sections.FirstOrDefault(s =>
                    s.CanonicalTitle.Equals(refSection.CanonicalTitle, StringComparison.OrdinalIgnoreCase));
                int wc = CountWords(userSection?.Content ?? "");
                if (wc == 0)
                {
                    string req = mustTitles.Contains(refSection.CanonicalTitle, StringComparer.OrdinalIgnoreCase) ? "MUST"
                               : shouldTitles.Contains(refSection.CanonicalTitle, StringComparer.OrdinalIgnoreCase) ? "SHOULD"
                               : "OPTIONAL";
                    emptySectionViolations.Add(new ChatEmptySectionViolation
                    {
                        Order = orderIdx,
                        Title = refSection.Title,
                        CanonicalTitle = refSection.CanonicalTitle,
                        Requirement = req
                    });
                }
            }
        }

        // ── STEP 10: Deterministic score ─────────────────────────────
        int criticalPenalty = report.Errors.Count(e => e.Severity == "critical") * 15;
        int mediumPenalty = report.Warnings.Count(w => w.Severity == "medium") * 3;
        int lowPenalty = report.Warnings.Count(w => w.Severity == "low") * 1;
        report.Score = Math.Max(0, Math.Min(100, (int)Math.Round(comparison.CoveragePercent) - criticalPenalty - mediumPenalty - lowPenalty));
        report.Summary = new ValidationSummary
        {
            TotalChecks = totalChecks,
            Passed = passedChecks,
            Failed = report.Errors.Count,
            Warnings = report.Warnings.Count
        };
        report.IsValid = report.Errors.Count == 0 && report.Score >= 70;

        // ── STEP 11: Build ChatReport (chat-report-template.json format) ─
        var validatedAt = DateTime.UtcNow;
        chat.Metadata.ValidatedAtUtc = validatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        chat.Metadata.DurationMs = (long)(validatedAt - startedAt).TotalMilliseconds;

        // Summary — subtitle-level metrics computed from pivot analysis
        var pivotHighlights = BuildPivotHighlights(referenceAnalysis, userAnalysis);
        int totalTemplateSubtitles = pivotHighlights.Sum(p => p.TemplateSubtitleCount);
        int totalMatchedSubtitles = pivotHighlights.Sum(p => p.MatchedSubtitleCount);
        int totalUserSubtitles = pivotHighlights.Sum(p => p.UserSubtitleCount);
        double subtitleRecall = totalTemplateSubtitles > 0 ? Math.Round((double)totalMatchedSubtitles / totalTemplateSubtitles * 100, 2) : 100;
        double subtitlePrecision = totalUserSubtitles > 0 ? Math.Round((double)totalMatchedSubtitles / totalUserSubtitles * 100, 2) : 100;
        double orderScore = 100; // Simplified: full order score needs subtitle-order analysis

        string confidenceBand = comparison.CoveragePercent >= 80 && subtitleRecall >= 70 ? "HIGH"
                              : comparison.CoveragePercent >= 50 ? "MEDIUM" : "LOW";

        chat.Summary = new ChatSummary
        {
            FinalScore = report.Score,
            PivotCoverage = comparison.CoveragePercent,
            SubtitleRecall = subtitleRecall,
            SubtitlePrecision = subtitlePrecision,
            OrderScore = orderScore,
            ConfidenceBand = confidenceBand
        };

        // Policy compliance
        var allMissing = missingMust.Concat(missingShould).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        chat.PolicyCompliance = new ChatPolicyCompliance
        {
            MissingSections = allMissing,
            TotalSections = totalPolicySections
        };

        // Quality gates
        chat.QualityGates = new ChatQualityGates
        {
            RequiresNotApplicableForEmptySections = policy.AllowNotApplicableForEmpty,
            RedTextDetected = false,
            EmptySectionViolations = emptySectionViolations
        };

        // Pivot highlights — sorted by severity (High first)
        chat.PivotHighlights = pivotHighlights
            .Where(p => p.MissingSubtitles.Count > 0 || p.TemplateSubtitleCount != p.MatchedSubtitleCount)
            .OrderBy(p => p.Severity == "High" ? 0 : p.Severity == "Medium" ? 1 : 2)
            .ToList();

        // Top issues
        chat.TopIssues = new List<ChatTopIssue>();
        if (comparison.MissingInUser.Count > 0)
        {
            chat.TopIssues.Add(new ChatTopIssue
            {
                Type = "MissingSections",
                Count = comparison.MissingInUser.Count,
                Items = comparison.MissingInUser.ToList(),
                Severity = "High"
            });
        }
        if (emptySectionViolations.Count > 0)
        {
            chat.TopIssues.Add(new ChatTopIssue
            {
                Type = "EmptySections",
                Count = emptySectionViolations.Count,
                Items = emptySectionViolations.Select(v => v.Title).ToList(),
                Severity = "Medium"
            });
        }

        // Recommendations (heuristic, matching ReportGeneratorChat logic)
        chat.Recommendations = new List<string>();
        if (allMissing.Count > 0)
            chat.Recommendations.Add("Add the missing sections or explicitly mark them 'Not applicable'.");
        if (subtitlePrecision < 70)
            chat.Recommendations.Add("Subtitle precision is low — review extra subtitles and consolidate duplicates.");
        if (confidenceBand != "HIGH")
            chat.Recommendations.Add("Low confidence detected — manually review highlighted pivots and ambiguous sections.");
        if (emptySectionViolations.Count > 0)
            chat.Recommendations.Add("Fill in empty sections with relevant content or mark as 'N/A' if not applicable.");
        if (chat.Recommendations.Count == 0)
            chat.Recommendations.Add("No immediate automated actions detected — consider manual review for quality assurance.");

        // Human summary
        var parts = new List<string>();
        parts.Add($"Overall score: {report.Score}%.");
        parts.Add($"Pivot coverage: {comparison.CoveragePercent:0.##}%.");
        if (chat.TopIssues.Count > 0)
            parts.Add($"Top issues: {string.Join(", ", chat.TopIssues.Select(i => i.Type))}.");
        parts.Add("See 'pivotHighlights' for per-section details and 'recommendations' for next steps.");
        chat.HumanSummary = string.Join(" ", parts);

        _logger.LogInformation(
            "[{CId}] Validation complete: Coverage={Cov:F1}%, Score={Score}, Band={Band}, Missing={Miss}, Pivots={Pivots}",
            correlationId, comparison.CoveragePercent, report.Score, confidenceBand,
            comparison.MissingInUser.Count, chat.PivotHighlights.Count);

        return (report, chat);
    }

    // ═══════════════════════════════════════════════════════════════
    // Pivot Highlights Builder — groups Level 1 sections as pivots,
    // Level 2+ as subtitles, then compares user vs reference
    // ═══════════════════════════════════════════════════════════════

    private static List<ChatPivotHighlight> BuildPivotHighlights(
        DocumentAnalysis reference, DocumentAnalysis user)
    {
        // Group reference sections into pivots (Level 1) with subtitles (Level 2+)
        var pivots = new List<(NormalizedSection Pivot, List<NormalizedSection> Subtitles)>();
        (NormalizedSection Pivot, List<NormalizedSection> Subtitles)? current = null;

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
                    // Subtitle before any pivot — create a synthetic pivot
                    current = (new NormalizedSection
                    {
                        Level = 1, Title = "(Preamble)",
                        CanonicalTitle = "PREAMBLE", Content = ""
                    }, new List<NormalizedSection>());
                }
                current.Value.Subtitles.Add(section);
            }
        }
        if (current != null) pivots.Add(current.Value);

        // Build user subtitle lookup: for each user section, store its canonical title
        var userCanonicals = user.Sections
            .Select(s => s.CanonicalTitle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var highlights = new List<ChatPivotHighlight>();
        foreach (var (pivot, subtitles) in pivots)
        {
            int templateSubCount = subtitles.Count;
            var matchedSubs = subtitles.Where(s => userCanonicals.Contains(s.CanonicalTitle)).ToList();
            var missingSubs = subtitles.Where(s => !userCanonicals.Contains(s.CanonicalTitle)).ToList();

            // Count user subtitles under this pivot (approximate: user sections matching any sub)
            int userSubCount = matchedSubs.Count;

            // Severity heuristic (matching ReportGeneratorChat logic)
            string severity = missingSubs.Count > 0 ? "High"
                            : templateSubCount > 0 && matchedSubs.Count < templateSubCount ? "Medium"
                            : "Low";

            highlights.Add(new ChatPivotHighlight
            {
                PivotTitle = pivot.Title,
                TemplateSubtitleCount = templateSubCount,
                UserSubtitleCount = userSubCount,
                MatchedSubtitleCount = matchedSubs.Count,
                MissingSubtitles = missingSubs.Select(s => s.Title).ToList(),
                Severity = severity
            });
        }

        return highlights;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ═══════════════════════════════════════════════════════════════
    // Resource Loading (URL or local file)
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> LoadResourceAsync(string pathOrUrl, string correlationId)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[{CId}] Loading resource from URL: {Url}", correlationId, pathOrUrl);
            return await _sharedHttpClient.GetStringAsync(pathOrUrl);
        }

        var fullPath = Path.IsPathFullyQualified(pathOrUrl)
            ? pathOrUrl
            : Path.Combine(AppContext.BaseDirectory, pathOrUrl);
        _logger.LogDebug("[{CId}] Loading resource from file: {Path}", correlationId, fullPath);
        return await File.ReadAllTextAsync(fullPath);
    }

    // ═══════════════════════════════════════════════════════════════
    // Blueprint & Policy JSON Parsing
    // ═══════════════════════════════════════════════════════════════

    private static List<BlueprintHeading> ExtractBlueprintHeadings(string blueprintJson)
    {
        using var doc = JsonDocument.Parse(blueprintJson);
        var headings = new List<BlueprintHeading>();

        if (!doc.RootElement.TryGetProperty("Headings", out var headingsArray))
            throw new InvalidOperationException("Blueprint JSON does not contain a 'Headings' array");

        foreach (var h in headingsArray.EnumerateArray())
        {
            var title = h.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "";
            var level = h.TryGetProperty("Level", out var l) ? l.GetInt32() : 0;
            var wordCount = h.TryGetProperty("ContentWordCount", out var w) ? w.GetInt32() : 0;

            if (!string.IsNullOrWhiteSpace(title))
            {
                headings.Add(new BlueprintHeading
                {
                    Title = title,
                    Level = level,
                    ContentWordCount = wordCount
                });
            }
        }

        return headings;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal Models (private — not exposed in OpenAPI)
    // ═══════════════════════════════════════════════════════════════

    private class BlueprintHeading
    {
        public string Title { get; set; } = "";
        public int Level { get; set; }
        public int ContentWordCount { get; set; }
    }

    private class PolicyConfig
    {
        public string ProfileName { get; set; } = "";
        public string Version { get; set; } = "";
        public bool AllowNotApplicableForEmpty { get; set; }
        public List<string> MustCanonicalTitles { get; set; } = new();
        public List<string> ShouldCanonicalTitles { get; set; } = new();
        public List<string> OptionalCanonicalTitles { get; set; } = new();
    }
}
}