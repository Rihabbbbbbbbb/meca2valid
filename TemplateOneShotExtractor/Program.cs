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
using Newtonsoft.Json.Serialization;

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
        /// Optional: Base64-encoded DOCX file content for direct upload
        /// </summary>
        public string? FileContent { get; set; }
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
        _logger.LogInformation("ValidateTemplate function triggered");

        try
        {
            // Parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogDebug("Request body received: {RequestBody}", requestBody);

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

            // Replace hardcoded paths with environment variables
            var blueprintPath = Environment.GetEnvironmentVariable("BLUEPRINT_PATH") ?? argsMap["templateBlueprint"];
            var userPath = argsMap["user"];

            _logger.LogInformation("Processing validation for user: {User}, blueprint: {Blueprint}", userPath, blueprintPath);

            // Perform template validation and build detailed report
            var validationReport = PerformValidation(blueprintPath, userPath);

            _logger.LogInformation("Validation completed for user: {User} - IsValid: {IsValid}, Score: {Score}", 
                userPath, validationReport.IsValid, validationReport.Score);
            
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
                Report = validationReport
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

    /// <summary>
    /// Performs the actual template validation logic
    /// </summary>
    private ValidationReport PerformValidation(string blueprintPath, string userPath)
    {
        var report = new ValidationReport
        {
            Summary = new ValidationSummary(),
            Errors = new List<ValidationIssue>(),
            Warnings = new List<ValidationIssue>(),
            Details = new List<SectionDetail>()
        };

        try
        {
            // Simulate comprehensive validation checks
            int totalChecks = 0;
            int passedChecks = 0;

            // Check 1: Header validation
            var headerSection = ValidateSection("Header", new[] { "Company Logo", "Document Title", "Date" });
            report.Details.Add(headerSection);
            totalChecks += headerSection.Checks.Count;
            if (headerSection.Status == "valid") passedChecks += headerSection.Checks.Count;

            // Check 2: Content structure
            var contentSection = ValidateSection("Content Structure", new[] { "Introduction", "Main Body", "Conclusion" });
            report.Details.Add(contentSection);
            totalChecks += contentSection.Checks.Count;
            if (contentSection.Status == "valid") passedChecks += contentSection.Checks.Count;

            // Check 3: Formatting
            var formatSection = ValidateSection("Formatting", new[] { "Font Consistency", "Spacing", "Margins" });
            report.Details.Add(formatSection);
            totalChecks += formatSection.Checks.Count;
            if (formatSection.Status == "valid") passedChecks += formatSection.Checks.Count;

            // Check 4: Metadata
            var metadataSection = ValidateSection("Metadata", new[] { "Author", "Company", "Version" });
            report.Details.Add(metadataSection);
            totalChecks += metadataSection.Checks.Count;
            if (metadataSection.Status == "valid") passedChecks += metadataSection.Checks.Count;

            // Add some demo warnings (non-blocking issues)
            if (blueprintPath.Contains("advanced") || blueprintPath.Contains("v2"))
            {
                report.Warnings.Add(new ValidationIssue
                {
                    Type = "warning",
                    Field = "Metadata.Keywords",
                    Message = "Optional field 'Keywords' is missing. Consider adding for better searchability.",
                    Severity = "low"
                });
            }

            // Add demo errors for specific blueprints
            if (blueprintPath.Contains("strict") || blueprintPath.Contains("production"))
            {
                report.Errors.Add(new ValidationIssue
                {
                    Type = "error",
                    Field = "Header.CompanyLogo",
                    Message = "Company logo dimensions must be exactly 200x50 pixels.",
                    Severity = "medium"
                });
                report.Errors.Add(new ValidationIssue
                {
                    Type = "error",
                    Field = "Content.Introduction",
                    Message = "Introduction section must contain at least 100 words.",
                    Severity = "high"
                });
            }

            // Calculate summary
            report.Summary.TotalChecks = totalChecks;
            report.Summary.Passed = passedChecks;
            report.Summary.Failed = report.Errors.Count;
            report.Summary.Warnings = report.Warnings.Count;

            // Calculate score (0-100)
            if (totalChecks > 0)
            {
                int baseScore = (passedChecks * 100) / totalChecks;
                int errorPenalty = report.Errors.Count * 10;
                int warningPenalty = report.Warnings.Count * 3;
                report.Score = Math.Max(0, baseScore - errorPenalty - warningPenalty);
            }
            else
            {
                report.Score = 0;
            }

            // Determine overall validity
            report.IsValid = report.Errors.Count == 0 && report.Score >= 70;

            _logger.LogInformation("Validation report generated: {TotalChecks} checks, {Passed} passed, {Failed} errors, {Warnings} warnings",
                totalChecks, passedChecks, report.Errors.Count, report.Warnings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during validation logic");
            report.IsValid = false;
            report.Score = 0;
            report.Errors.Add(new ValidationIssue
            {
                Type = "error",
                Field = "System",
                Message = "An error occurred during validation: " + ex.Message,
                Severity = "critical"
            });
        }

        return report;
    }

    /// <summary>
    /// Validates a specific section (demo implementation)
    /// </summary>
    private SectionDetail ValidateSection(string sectionName, string[] checks)
    {
        // In a real implementation, this would perform actual document analysis
        // For now, we simulate successful validation for most cases
        var random = new Random();
        bool isValid = random.Next(100) > 10; // 90% success rate for demo

        return new SectionDetail
        {
            Section = sectionName,
            Status = isValid ? "valid" : "warning",
            Checks = new List<string>(checks)
        };
    }
}
}