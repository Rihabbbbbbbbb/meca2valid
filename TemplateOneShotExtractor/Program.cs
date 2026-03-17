using System.Net;
using System.Text.Json;
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
    }

    /// <summary>
    /// Error response model
    /// </summary>
    public class ErrorResponse
    {
        /// <summary>
        /// Error message
        /// </summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>
        /// Error details
        /// </summary>
        public string? Details { get; set; }
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

            // Perform REAL template validation: download DOCX, parse, compare against blueprint + policy
            var validationReport = await PerformValidationAsync(blueprintPath, documentUrl, correlationId);

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
                CorrelationId = correlationId
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
    // REAL VALIDATION ENGINE — No simulation, no Random()
    // Downloads DOCX → parses OpenXML → compares vs blueprint → checks policy
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Performs REAL validation: downloads DOCX from documentUrl, parses it with OpenXML,
    /// compares its sections against the blueprint headings, and checks policy compliance.
    /// All results are deterministic — same document always produces the same report.
    /// </summary>
    private async Task<ValidationReport> PerformValidationAsync(string blueprintPathOrUrl, string documentUrl, string correlationId)
    {
        var report = new ValidationReport
        {
            Summary = new ValidationSummary(),
            Errors = new List<ValidationIssue>(),
            Warnings = new List<ValidationIssue>(),
            Details = new List<SectionDetail>()
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
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error", Field = "documentUrl",
                Message = $"Cannot download document: {ex.StatusCode} — {ex.Message}",
                Severity = "critical"
            });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            return report;
        }
        catch (TaskCanceledException)
        {
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error", Field = "documentUrl",
                Message = "Document download timed out after 60 seconds",
                Severity = "critical"
            });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            return report;
        }

        // ── STEP 2: Parse the user DOCX into heading sections ────────
        List<ParsedSection> userSections;
        try
        {
            userSections = ParseDocxSections(docxBytes);
            _logger.LogInformation("[{CId}] Parsed {Count} heading sections from user document", correlationId, userSections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CId}] Failed to parse DOCX", correlationId);
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error", Field = "document",
                Message = $"Cannot parse DOCX file: {ex.Message}. Ensure the file is a valid .docx (Office Open XML) document.",
                Severity = "critical"
            });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            return report;
        }

        if (userSections.Count == 0)
        {
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error", Field = "document",
                Message = "Document contains no identifiable headings or sections",
                Severity = "critical"
            });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            return report;
        }

        // ── STEP 3: Load blueprint JSON (from URL or local file) ─────
        List<BlueprintHeading> blueprintHeadings;
        try
        {
            var blueprintJson = await LoadResourceAsync(blueprintPathOrUrl, correlationId);
            blueprintHeadings = ExtractBlueprintHeadings(blueprintJson);
            _logger.LogInformation("[{CId}] Blueprint loaded: {Count} headings", correlationId, blueprintHeadings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CId}] Failed to load blueprint from {Path}", correlationId, blueprintPathOrUrl);
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error", Field = "blueprint",
                Message = $"Cannot load blueprint: {ex.Message}",
                Severity = "critical"
            });
            report.Summary = new ValidationSummary { TotalChecks = 1, Passed = 0, Failed = 1, Warnings = 0 };
            return report;
        }

        // ── STEP 4: Load policy JSON ────────────────────────────────
        PolicyConfig policy;
        try
        {
            var policyPathOrUrl = Environment.GetEnvironmentVariable("POLICY_PATH") ?? "template-policy.json";
            var policyJson = await LoadResourceAsync(policyPathOrUrl, correlationId);
            policy = JsonSerializer.Deserialize<PolicyConfig>(policyJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new PolicyConfig();
            _logger.LogInformation("[{CId}] Policy loaded: {Must} must, {Should} should, {Opt} optional",
                correlationId, policy.MustCanonicalTitles.Count, policy.ShouldCanonicalTitles.Count, policy.OptionalCanonicalTitles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{CId}] Policy load failed, continuing with blueprint-only comparison", correlationId);
            policy = new PolicyConfig();
        }

        // ── STEP 5: Compare user sections vs blueprint headings ──────
        var userCanonicals = userSections
            .Select(s => s.CanonicalTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blueprintCanonicals = blueprintHeadings
            .Select(h => h.CanonicalTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int totalChecks = 0;
        int passedChecks = 0;

        foreach (var bpTitle in blueprintCanonicals)
        {
            totalChecks++;
            bool found = userCanonicals.Contains(bpTitle);

            if (found)
            {
                passedChecks++;
                var userSection = userSections.FirstOrDefault(s =>
                    s.CanonicalTitle.Equals(bpTitle, StringComparison.OrdinalIgnoreCase));
                int wordCount = userSection?.WordCount ?? 0;
                string status = wordCount >= 5 ? "valid" : "warning";

                var checks = new List<string> { "Section found in user document" };
                checks.Add(wordCount > 0 ? $"Content: {wordCount} words" : "Section is empty (no content)");

                report.Details.Add(new SectionDetail
                {
                    Section = bpTitle,
                    Status = status,
                    Checks = checks
                });

                if (wordCount < 5)
                {
                    report.Warnings.Add(new ValidationIssue
                    {
                        Type = "warning", Field = bpTitle,
                        Message = $"Section '{bpTitle}' is present but has insufficient content ({wordCount} words)",
                        Severity = "low"
                    });
                }
            }
            else
            {
                report.Details.Add(new SectionDetail
                {
                    Section = bpTitle,
                    Status = "missing",
                    Checks = new List<string> { "Section NOT found in user document" }
                });
            }
        }

        // ── STEP 6: Policy compliance ────────────────────────────────
        foreach (var must in policy.MustCanonicalTitles)
        {
            if (!userCanonicals.Contains(must))
            {
                report.Errors.Add(new ValidationIssue
                {
                    Type = "error", Field = must,
                    Message = $"REQUIRED section '{must}' is missing (policy: MUST have)",
                    Severity = "critical"
                });
            }
        }

        foreach (var should in policy.ShouldCanonicalTitles)
        {
            if (!userCanonicals.Contains(should))
            {
                report.Warnings.Add(new ValidationIssue
                {
                    Type = "warning", Field = should,
                    Message = $"Recommended section '{should}' is missing (policy: SHOULD have)",
                    Severity = "medium"
                });
            }
        }

        // Report extra sections in user doc not in blueprint
        var bpSet = new HashSet<string>(blueprintCanonicals, StringComparer.OrdinalIgnoreCase);
        var extraSections = userCanonicals.Where(u => !bpSet.Contains(u)).ToList();
        if (extraSections.Count > 0)
        {
            report.Details.Add(new SectionDetail
            {
                Section = "Extra Sections (not in blueprint)",
                Status = "info",
                Checks = extraSections.Select(s => $"Extra: {s}").ToList()
            });
        }

        // ── STEP 7: Deterministic score calculation ──────────────────
        double coveragePercent = totalChecks > 0 ? (double)passedChecks / totalChecks * 100.0 : 0;
        int criticalPenalty = report.Errors.Count(e => e.Severity == "critical") * 15;
        int mediumPenalty = report.Warnings.Count(w => w.Severity == "medium") * 3;
        int lowPenalty = report.Warnings.Count(w => w.Severity == "low") * 1;

        report.Score = Math.Max(0, Math.Min(100, (int)Math.Round(coveragePercent) - criticalPenalty - mediumPenalty - lowPenalty));
        report.Summary = new ValidationSummary
        {
            TotalChecks = totalChecks,
            Passed = passedChecks,
            Failed = report.Errors.Count,
            Warnings = report.Warnings.Count
        };
        report.IsValid = report.Errors.Count == 0 && report.Score >= 70;

        _logger.LogInformation(
            "[{CId}] Real validation complete: {Total} blueprint headings checked, {Pass} found in user doc ({Cov:F1}% coverage), {Err} errors, {Warn} warnings, Score={Score}, IsValid={Valid}",
            correlationId, totalChecks, passedChecks, coveragePercent, report.Errors.Count, report.Warnings.Count, report.Score, report.IsValid);

        return report;
    }

    // ═══════════════════════════════════════════════════════════════
    // DOCX Parsing (OpenXML) — based on WordOpenXml.Core.WordParser
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a DOCX byte array into a list of heading sections with canonical titles and word counts.
    /// </summary>
    private static List<ParsedSection> ParseDocxSections(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body
                   ?? throw new InvalidOperationException("DOCX has no document body");

        var sections = new List<ParsedSection>();
        ParsedSection? current = null;

        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            var text = paragraph.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var level = GetHeadingLevel(paragraph);
            if (level.HasValue)
            {
                current = new ParsedSection
                {
                    Level = level.Value,
                    Title = text,
                    CanonicalTitle = CanonicalizeTitle(text),
                    ContentBuilder = new System.Text.StringBuilder()
                };
                sections.Add(current);
            }
            else
            {
                if (current == null)
                {
                    current = new ParsedSection
                    {
                        Level = 0,
                        Title = "(Preamble)",
                        CanonicalTitle = "preamble",
                        ContentBuilder = new System.Text.StringBuilder()
                    };
                    sections.Add(current);
                }
                if (current.ContentBuilder.Length > 0) current.ContentBuilder.Append('\n');
                current.ContentBuilder.Append(text);
            }
        }

        // Finalize content + word counts
        foreach (var s in sections)
        {
            s.Content = s.ContentBuilder.ToString();
            s.WordCount = string.IsNullOrWhiteSpace(s.Content)
                ? 0
                : s.Content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return sections;
    }

    private static int? GetHeadingLevel(Paragraph paragraph)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(styleId) && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = styleId.Substring("Heading".Length);
            if (int.TryParse(suffix, out var level)) return level;
            return 1;
        }

        var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outlineLevel is not null) return (int)outlineLevel + 1;

        return null;
    }

    /// <summary>
    /// Normalizes a heading title for comparison: strips numbering prefix, normalizes whitespace, lowercases.
    /// </summary>
    private static string CanonicalizeTitle(string title)
    {
        var result = Regex.Replace(title ?? "", @"^\d+(\.\d+)*\s*[-:.)\s]\s*", "");
        result = Regex.Replace(result, @"\s+", " ").Trim();
        return result.ToLowerInvariant();
    }

    // ═══════════════════════════════════════════════════════════════
    // Resource Loading (URL or local file)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads a text resource from an HTTP(S) URL or a local file path relative to the app directory.
    /// </summary>
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
            var canonical = h.TryGetProperty("CanonicalTitle", out var c) ? c.GetString() ?? "" : "";
            var level = h.TryGetProperty("Level", out var l) ? l.GetInt32() : 0;
            var wordCount = h.TryGetProperty("ContentWordCount", out var w) ? w.GetInt32() : 0;

            if (!string.IsNullOrWhiteSpace(canonical))
            {
                headings.Add(new BlueprintHeading
                {
                    Title = title,
                    CanonicalTitle = canonical.ToLowerInvariant(),
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

    private class ParsedSection
    {
        public int Level { get; set; }
        public string Title { get; set; } = "";
        public string CanonicalTitle { get; set; } = "";
        public string Content { get; set; } = "";
        public int WordCount { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Text.StringBuilder ContentBuilder { get; set; } = new();
    }

    private class BlueprintHeading
    {
        public string Title { get; set; } = "";
        public string CanonicalTitle { get; set; } = "";
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