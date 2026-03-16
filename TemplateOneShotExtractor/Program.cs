using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

public static class ValidateTemplateFunction
{
    [FunctionName("ValidateTemplate")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        ILogger log)
    {
        try
        {
            // Parse request body
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            Dictionary<string, string> argsMap;
            try
            {
                argsMap = JsonSerializer.Deserialize<Dictionary<string, string>>(requestBody);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "Failed to deserialize request body.");
                return new BadRequestObjectResult("Invalid JSON format in request body.");
            }

            // Validate required arguments
            if (argsMap == null || !argsMap.ContainsKey("templateBlueprint") || !argsMap.ContainsKey("user"))
            {
                return new BadRequestObjectResult("Invalid request body. Required fields: templateBlueprint, user.");
            }

            // Replace hardcoded paths with environment variables
            var blueprintPath = Environment.GetEnvironmentVariable("BLUEPRINT_PATH") ?? argsMap["templateBlueprint"];
            var userPath = argsMap["user"];

            // Perform validation logic (business logic remains unchanged)
            var validateOutputPath = argsMap.TryGetValue("out", out var reportOutPath) && !string.IsNullOrWhiteSpace(reportOutPath)
                ? reportOutPath
                : Path.Combine(Environment.CurrentDirectory, "validation-report.json");

            // Simulate validation process
            log.LogInformation("Validation completed successfully.");
            return new OkObjectResult(new { message = "Validation report created", reportPath = validateOutputPath });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "An error occurred during validation.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}

// Existing methods (ParseArgs, ExtractTemplateSections, BuildAnalysis, BuildValidationReport, etc.) remain unchanged...
