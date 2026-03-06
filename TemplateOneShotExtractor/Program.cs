using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

try
{
    var argsMap = ParseArgs(args);

    var validateMode = argsMap.ContainsKey("validate") || argsMap.ContainsKey("user") || argsMap.ContainsKey("templateBlueprint");
    if (validateMode)
    {
        if (!argsMap.TryGetValue("templateBlueprint", out var blueprintPath) || string.IsNullOrWhiteSpace(blueprintPath))
        {
            Console.WriteLine("Usage: dotnet run --project TemplateOneShotExtractor -- --validate --templateBlueprint \"C:\\path\\template-blueprint.json\" --user \"C:\\path\\user.docx\" [--out \"validation-report.json\"] [--pivotLevel 2] [--mapping \"semantic-mapping.json\"] [--policy \"template-policy.json\"]");
            return 2;
        }

        if (!argsMap.TryGetValue("user", out var userPath) || string.IsNullOrWhiteSpace(userPath))
        {
            Console.WriteLine("Missing required argument: --user");
            return 2;
        }

        if (!File.Exists(blueprintPath))
        {
            Console.WriteLine($"Template blueprint file not found: {blueprintPath}");
            return 3;
        }

        if (!File.Exists(userPath))
        {
            Console.WriteLine($"User file not found: {userPath}");
            return 3;
        }

        var validateOutputPath = argsMap.TryGetValue("out", out var reportOutPath) && !string.IsNullOrWhiteSpace(reportOutPath)
            ? reportOutPath
            : Path.Combine(Environment.CurrentDirectory, "validation-report.json");

        var pivotLevel = 2;
        if (argsMap.TryGetValue("pivotLevel", out var pivotLevelRaw) && int.TryParse(pivotLevelRaw, out var parsedPivotLevel))
        {
            pivotLevel = Math.Clamp(parsedPivotLevel, 1, 6);
        }

        var mappingProfile = LoadSemanticMappingProfile(argsMap.TryGetValue("mapping", out var mappingPath) ? mappingPath : null);
        var policyProfile = LoadValidationPolicyProfile(argsMap.TryGetValue("policy", out var policyPath) ? policyPath : null);

        var blueprintJson = File.ReadAllText(blueprintPath);
        var blueprint = JsonSerializer.Deserialize<TemplateBlueprintReport>(blueprintJson);
        if (blueprint is null
            || blueprint.Headings is null
            || blueprint.Headings.Count == 0
            || blueprint.Metadata is null
            || string.IsNullOrWhiteSpace(blueprint.Metadata.TemplatePath)
            || blueprint.OrderedTitleList is null)
        {
            Console.WriteLine("Invalid template blueprint JSON.");
            return 4;
        }

        var started = DateTimeOffset.UtcNow;
        var tempUserCopy = CreateTemplateWorkingCopy(userPath);
        try
        {
            var userSections = ExtractTemplateSections(tempUserCopy);
            var userTables = ExtractDocumentTables(tempUserCopy, userSections);

            var templateTables = blueprint.TableInventory.Tables;
            if (templateTables.Count == 0 && File.Exists(blueprint.Metadata.TemplatePath))
            {
                var tempTemplateCopyForValidation = CreateTemplateWorkingCopy(blueprint.Metadata.TemplatePath);
                try
                {
                    var templateSectionsForValidation = ExtractTemplateSections(tempTemplateCopyForValidation);
                    templateTables = ExtractDocumentTables(tempTemplateCopyForValidation, templateSectionsForValidation);
                }
                finally
                {
                    TryDeleteFile(tempTemplateCopyForValidation);
                }
            }

            var validation = BuildValidationReport(
                blueprint,
                blueprintPath,
                userPath,
                tempUserCopy,
                started,
                mappingProfile,
                policyProfile,
                pivotLevel,
                userSections,
                templateTables,
                userTables);

            var json = JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(validateOutputPath, json);

            Console.WriteLine($"Validation report created: {Path.GetFullPath(validateOutputPath)}");
            Console.WriteLine($"Verdict: {validation.Decision.Verdict}");
            Console.WriteLine($"Score: {validation.Scores.FinalScore}/100");
            Console.WriteLine($"Pivot coverage: {validation.Scores.PivotCoveragePercent}%");

            return validation.Decision.Verdict.Equals("FAIL", StringComparison.OrdinalIgnoreCase) ? 6 : 0;
        }
        finally
        {
            TryDeleteFile(tempUserCopy);
        }
    }
    else
    {
        if (!argsMap.TryGetValue("template", out var templatePath) || string.IsNullOrWhiteSpace(templatePath))
        {
            Console.WriteLine("Usage: dotnet run --project TemplateOneShotExtractor -- --template \"C:\\path\\template.docx\" [--out \"template-blueprint.json\"]");
            return 2;
        }

        var outputPath = argsMap.TryGetValue("out", out var outPath) && !string.IsNullOrWhiteSpace(outPath)
            ? outPath
            : Path.Combine(Environment.CurrentDirectory, "template-blueprint.json");

        if (!File.Exists(templatePath))
        {
            Console.WriteLine($"Template file not found: {templatePath}");
            return 3;
        }

        var started = DateTimeOffset.UtcNow;
        var tempTemplateCopy = CreateTemplateWorkingCopy(templatePath);
        try
        {
            var sections = ExtractTemplateSections(tempTemplateCopy);
            var tables = ExtractDocumentTables(tempTemplateCopy, sections);
            var analysis = BuildAnalysis(templatePath, started, sections, tables);

            var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outputPath, json);

            Console.WriteLine($"Template blueprint created: {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"Detected headings: {analysis.Stats.TotalHeadings}");
            Console.WriteLine($"Detected top-level sections: {analysis.Stats.TopLevelHeadings}");

            return 0;
        }
        finally
        {
            TryDeleteFile(tempTemplateCopy);
        }
    }
}
catch (Exception ex)
{
    var crashLogPath = Path.Combine(Environment.CurrentDirectory, "template-extractor-crash.log");
    File.WriteAllText(crashLogPath, $"{DateTimeOffset.UtcNow:O}\n{ex}");
    Console.WriteLine($"FATAL: {ex}");
    Console.Error.WriteLine($"FATAL: {ex}");
    return 99;
}

static Dictionary<string, string> ParseArgs(string[] input)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < input.Length; index++)
    {
        var current = input[index];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = current[2..];
        var hasValue = index + 1 < input.Length && !input[index + 1].StartsWith("--", StringComparison.Ordinal);
        result[key] = hasValue ? input[++index] : "true";
    }

    return result;
}

static List<TemplateSection> ExtractTemplateSections(string filePath)
{
    var sections = new List<TemplateSection>();
    TemplateSection? current = null;
    var paragraphIndex = 0;

    using var wordDoc = WordprocessingDocument.Open(filePath, false);
    var body = wordDoc.MainDocumentPart?.Document?.Body;
    if (body is null)
    {
        return sections;
    }

    foreach (var paragraph in body.Descendants<Paragraph>())
    {
        paragraphIndex++;
        var hasNonTextContent = HasRenderableNonTextContent(paragraph);
        var text = NormalizeText(paragraph.InnerText);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (current is not null && hasNonTextContent)
            {
                current.NonTextElementCount++;
            }

            continue;
        }

        if (LooksLikeNoise(text))
        {
            continue;
        }

        var headingInfo = DetectHeading(paragraph, text);
        if (headingInfo.IsHeading)
        {
            if (current is not null)
            {
                current.EndParagraphIndex = paragraphIndex - 1;
                FinalizeSectionStats(current);
                sections.Add(current);
            }

            current = new TemplateSection
            {
                Order = sections.Count + 1,
                Title = text,
                CanonicalTitle = CanonicalizeTitle(text),
                Level = headingInfo.Level,
                ParagraphStyle = headingInfo.StyleName,
                StartParagraphIndex = paragraphIndex,
                HeadingNumber = ExtractLeadingNumbers(text)
            };

            continue;
        }

        if (current is null)
        {
            continue;
        }

        current.ContentParagraphs.Add(text);
        if (hasNonTextContent)
        {
            current.NonTextElementCount++;
        }
    }

    if (current is not null)
    {
        current.EndParagraphIndex = paragraphIndex;
        FinalizeSectionStats(current);
        sections.Add(current);
    }

    return sections;
}

static TemplateBlueprintReport BuildAnalysis(string templatePath, DateTimeOffset startedAtUtc, List<TemplateSection> sections, List<TableSnapshot> tables)
{
    var duplicates = sections
        .GroupBy(item => item.CanonicalTitle)
        .Where(group => group.Count() > 1)
        .Select(group => new DuplicateTitle
        {
            CanonicalTitle = group.Key,
            Orders = group.Select(item => item.Order).ToList(),
            Titles = group.Select(item => item.Title).ToList()
        })
        .ToList();

    var levelJumps = new List<LevelJumpIssue>();
    for (var index = 1; index < sections.Count; index++)
    {
        var previous = sections[index - 1];
        var current = sections[index];
        var delta = current.Level - previous.Level;

        if (Math.Abs(delta) > 1)
        {
            levelJumps.Add(new LevelJumpIssue
            {
                PreviousOrder = previous.Order,
                PreviousLevel = previous.Level,
                CurrentOrder = current.Order,
                CurrentLevel = current.Level,
                Delta = delta,
                PreviousTitle = previous.Title,
                CurrentTitle = current.Title
            });
        }
    }

    var numberingIssues = AnalyzeNumberingConsistency(sections);

    var emptySections = sections
        .Where(item => IsSectionMissingContentForValidation(sections, item.Order))
        .Select(item => item.Order)
        .ToList();
    var weakContentSections = sections
        .Where(item => item.ContentWordCount > 0 && item.ContentWordCount < 20)
        .Select(item => item.Order)
        .ToList();

    var topLevelCount = sections.Count(item => item.Level == 1);
    var deepestLevel = sections.Count == 0 ? 0 : sections.Max(item => item.Level);

    var report = new TemplateBlueprintReport
    {
        Metadata = new ReportMetadata
        {
            TemplatePath = Path.GetFullPath(templatePath),
            ExtractedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = startedAtUtc,
            DurationMs = (int)(DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds,
            Tool = "TemplateOneShotExtractor",
            Version = "1.2.0"
        },
        Stats = new TemplateStats
        {
            TotalHeadings = sections.Count,
            TopLevelHeadings = topLevelCount,
            DeepestLevel = deepestLevel,
            DuplicateTitleGroups = duplicates.Count,
            LevelJumpIssues = levelJumps.Count,
            NumberingIssues = numberingIssues.Count,
            EmptySections = emptySections.Count,
            WeakContentSections = weakContentSections.Count
        },
        TableInventory = BuildTemplateTableInventory(tables),
        Quality = new TemplateQualitySummary
        {
            DuplicateTitles = duplicates,
            LevelJumps = levelJumps,
            NumberingIssues = numberingIssues,
            EmptySectionOrders = emptySections,
            WeakContentSectionOrders = weakContentSections
        },
        Headings = sections,
        OrderedTitleList = sections.Select(item => item.Title).ToList()
    };

    return report;
}

static ValidationReport BuildValidationReport(
    TemplateBlueprintReport blueprint,
    string templateBlueprintPath,
    string userPath,
    string userReadablePath,
    DateTimeOffset startedAtUtc,
    SemanticMappingProfile mappingProfile,
    ValidationPolicyProfile policyProfile,
    int pivotLevel,
    List<TemplateSection> userSections,
    List<TableSnapshot> templateTables,
    List<TableSnapshot> userTables)
{
    var templateSections = blueprint.Headings ?? new List<TemplateSection>();
    var templatePivots = BuildPivotBlocks(templateSections, pivotLevel);
    var rawUserPivots = BuildPivotBlocks(userSections, pivotLevel);
    var semanticResolution = ApplySemanticMapping(rawUserPivots, mappingProfile);
    var userPivots = semanticResolution.MappedPivots;

    var alignment = AlignPivots(templatePivots, userPivots);
    var pivotComparisons = new List<PivotComparison>();

    var matchedSubtitleCount = 0;
    var templateSubtitleTotal = 0;
    var userSubtitleTotal = 0;

    foreach (var match in alignment.Matches)
    {
        var templateChildrenSet = match.TemplateBlock.ChildCanonicalTitles
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var userChildrenSet = match.UserBlock.ChildCanonicalTitles
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchedChildren = templateChildrenSet.Where(userChildrenSet.Contains).ToList();
        var missingChildren = templateChildrenSet.Where(item => !userChildrenSet.Contains(item)).ToList();
        var extraChildren = userChildrenSet.Where(item => !templateChildrenSet.Contains(item)).ToList();

        matchedSubtitleCount += matchedChildren.Count;
        templateSubtitleTotal += templateChildrenSet.Count;
        userSubtitleTotal += userChildrenSet.Count;

        pivotComparisons.Add(new PivotComparison
        {
            PivotTitle = match.TemplateBlock.Title,
            PivotCanonicalTitle = match.TemplateBlock.CanonicalTitle,
            PivotOrderTemplate = match.TemplateBlock.PivotOrder,
            PivotOrderUser = match.UserBlock.PivotOrder,
            TemplateSubtitleCount = templateChildrenSet.Count,
            UserSubtitleCount = userChildrenSet.Count,
            MatchedSubtitleCount = matchedChildren.Count,
            MissingSubtitles = missingChildren,
            ExtraSubtitles = extraChildren
        });
    }

    foreach (var missing in alignment.MissingTemplatePivots)
    {
        pivotComparisons.Add(new PivotComparison
        {
            PivotTitle = missing.Title,
            PivotCanonicalTitle = missing.CanonicalTitle,
            PivotOrderTemplate = missing.PivotOrder,
            PivotOrderUser = null,
            TemplateSubtitleCount = missing.ChildCanonicalTitles.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            UserSubtitleCount = 0,
            MatchedSubtitleCount = 0,
            MissingSubtitles = missing.ChildCanonicalTitles.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ExtraSubtitles = new List<string>()
        });
    }

    var pivotTotal = templatePivots.Count;
    var matchedPivotCount = alignment.Matches.Count;
    var pivotCoverage = pivotTotal == 0 ? 1.0 : (double)matchedPivotCount / pivotTotal;

    var subtitleRecall = templateSubtitleTotal == 0 ? 1.0 : (double)matchedSubtitleCount / templateSubtitleTotal;
    var subtitlePrecision = userSubtitleTotal == 0
        ? (templateSubtitleTotal == 0 ? 1.0 : 0.0)
        : (double)matchedSubtitleCount / userSubtitleTotal;

    var subtitleScore = 0.7 * subtitleRecall + 0.3 * subtitlePrecision;
    var orderScore = ComputeSequenceOrderScore(
        templatePivots.Select(item => item.CanonicalTitle).ToList(),
        userPivots.Select(item => item.CanonicalTitle).ToList());

    var finalScore = Math.Round((0.65 * pivotCoverage + 0.25 * subtitleScore + 0.10 * orderScore) * 100.0, 2);

    var emptySectionViolations = userSections
        .Where(item => IsSectionMissingContentForValidation(userSections, item.Order))
        .Select(item => new EmptySectionViolation
        {
            Order = item.Order,
            Title = item.Title,
            CanonicalTitle = item.CanonicalTitle,
            Requirement = "Section vide: ajouter 'Not applicable' ou 'Sans objet'"
        })
        .ToList();

    var redTextEvidence = new List<RedTextEvidence>();
    var tableValidation = BuildTableValidation(templateTables, userTables, userSections);
    var policyCompliance = BuildPolicyCompliance(policyProfile, alignment, userSections);

    var hasHardQualityViolation = emptySectionViolations.Count > 0
                                  || policyCompliance.MissingMustSections.Count > 0
                                  || policyCompliance.EmptyMustSections.Count > 0;

    var verdict = hasHardQualityViolation
        ? "FAIL"
        : pivotCoverage < 1.0
        ? "FAIL"
        : finalScore >= 85.0 && subtitleRecall >= 0.60
            ? "PASS"
            : "WARN";

    var reasons = new List<string>();
    if (pivotCoverage < 1.0)
    {
        reasons.Add("At least one required pivot title is missing.");
    }
    if (emptySectionViolations.Count > 0)
    {
        reasons.Add("Section(s) vide(s) sans 'Not applicable'/'Sans objet'.");
    }
    if (policyCompliance.MissingMustSections.Count > 0)
    {
        reasons.Add("Policy MUST sections missing.");
    }
    if (policyCompliance.EmptyMustSections.Count > 0)
    {
        reasons.Add("Policy MUST sections are empty.");
    }
    if (subtitleRecall < 0.60)
    {
        reasons.Add("Too many expected subtitles are missing under matched pivot sections.");
    }
    if (orderScore < 0.75)
    {
        reasons.Add("Pivot title order drift is significant.");
    }
    if (reasons.Count == 0)
    {
        reasons.Add("Structure is compatible with template contract.");
    }

    return new ValidationReport
    {
        Metadata = new ValidationMetadata
        {
            Tool = "TemplateOneShotExtractor",
            Version = "1.2.0",
            StartedAtUtc = startedAtUtc,
            ValidatedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = (int)(DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds,
            UserDocumentPath = Path.GetFullPath(userPath),
            TemplatePath = blueprint.Metadata.TemplatePath,
            TemplateBlueprintPath = Path.GetFullPath(templateBlueprintPath)
        },
        SemanticMapping = new SemanticMappingSummary
        {
            ProfileName = mappingProfile.ProfileName,
            Version = mappingProfile.Version,
            AppliedMappings = semanticResolution.AppliedMappings,
            MappingEvents = semanticResolution.MappingEvents
        },
        PolicyCompliance = policyCompliance,
        Contract = new ValidationContract
        {
            PivotLevel = pivotLevel,
            RequiredPivotTitles = templatePivots.Select(item => item.Title).ToList(),
            RequiredPivotCanonicalTitles = templatePivots.Select(item => item.CanonicalTitle).ToList(),
            TotalRequiredPivots = pivotTotal
        },
        QualityGates = new ValidationQualityGates
        {
            RequiresNotApplicableForEmptySections = true,
            EmptySectionViolations = emptySectionViolations,
            RedTextDetected = false,
            RedTextEvidence = new List<RedTextEvidence>(),
            RuleCode = "P06"
        },
        Scores = new ValidationScores
        {
            PivotCoveragePercent = Math.Round(pivotCoverage * 100.0, 2),
            SubtitleRecallPercent = Math.Round(subtitleRecall * 100.0, 2),
            SubtitlePrecisionPercent = Math.Round(subtitlePrecision * 100.0, 2),
            OrderScorePercent = Math.Round(orderScore * 100.0, 2),
            FinalScore = finalScore,
            ConfidenceBand = ComputeConfidenceBand(
                finalScore,
                pivotCoverage,
                redTextEvidence.Count,
                emptySectionViolations.Count,
                policyCompliance.MissingMustSections.Count + policyCompliance.EmptyMustSections.Count)
        },
        Findings = new ValidationFindings
        {
            MissingRequiredPivots = alignment.MissingTemplatePivots.Select(item => item.Title).ToList(),
            ExtraUserPivots = alignment.ExtraUserPivots.Select(item => item.Title).ToList(),
            PivotComparisons = pivotComparisons.OrderBy(item => item.PivotOrderTemplate ?? int.MaxValue).ToList()
        },
        TableValidation = tableValidation,
        Decision = new ValidationDecision
        {
            Verdict = verdict,
            Reasons = reasons,
            ExplainabilityNotes = BuildExplainabilityNotes(
                pivotCoverage,
                subtitleRecall,
                subtitlePrecision,
                orderScore,
                finalScore,
                semanticResolution,
                redTextEvidence,
                emptySectionViolations,
                policyCompliance)
        }
    };
}

static TemplateTableInventory BuildTemplateTableInventory(List<TableSnapshot> tables)
{
    return new TemplateTableInventory
    {
        TotalTables = tables.Count,
        SectionsWithTables = tables
            .Where(item => !string.IsNullOrWhiteSpace(item.SectionCanonicalTitle))
            .Select(item => item.SectionCanonicalTitle)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(),
        DistinctHeaderSignatures = tables
            .Select(item => item.HeaderSignature)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(),
        Tables = tables
    };
}

static TableValidationSummary BuildTableValidation(List<TableSnapshot> templateTables, List<TableSnapshot> userTables, List<TemplateSection> userSections)
{
    var comparisons = new List<TableSectionComparison>();
    var hierarchyFallbackSections = 0;
    var missingTableSections = 0;
    var missingTableSectionsWaived = 0;

    var expectedBySection = templateTables
        .Where(item => !string.IsNullOrWhiteSpace(item.SectionCanonicalTitle))
        .GroupBy(item => item.SectionCanonicalTitle, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    var userBySection = userTables
        .Where(item => !string.IsNullOrWhiteSpace(item.SectionCanonicalTitle))
        .GroupBy(item => item.SectionCanonicalTitle, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

    var sectionsWithExpectedTables = expectedBySection.Count;
    var sectionsWithDetectedTables = userBySection.Count;
    var matchedSections = 0;

    var expectedHeadersTotal = 0;
    var userHeadersTotal = 0;
    var matchedHeadersTotal = 0;

    foreach (var expected in expectedBySection)
    {
        var sectionKey = expected.Key;
        var templateSectionTables = expected.Value;
        userBySection.TryGetValue(sectionKey, out var userSectionTables);
        userSectionTables ??= new List<TableSnapshot>();

        if (userSectionTables.Count == 0)
        {
            var fallbackTables = ResolveTablesFromDescendantUserSections(sectionKey, userSections, userTables);
            if (fallbackTables.Count > 0)
            {
                userSectionTables = fallbackTables;
                hierarchyFallbackSections++;
            }
        }

        if (userSectionTables.Count > 0)
        {
            matchedSections++;
        }

        var expectedHeaders = templateSectionTables
            .Select(item => item.HeaderSignature)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var userHeaders = userSectionTables
            .Select(item => item.HeaderSignature)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchedHeaders = expectedHeaders.Where(userHeaders.Contains).ToList();
        var missingHeaders = expectedHeaders.Where(item => !userHeaders.Contains(item)).ToList();
        var extraHeaders = userHeaders.Where(item => !expectedHeaders.Contains(item)).ToList();

        var missingTableCount = Math.Max(0, templateSectionTables.Count - userSectionTables.Count);
        var notApplicableEvidence = ResolveNotApplicableMarkersForSectionHierarchy(sectionKey, userSections);
        var hasNotApplicableWaiver = missingTableCount > 0 && notApplicableEvidence.Count > 0;

        if (missingTableCount > 0)
        {
            missingTableSections++;
            if (hasNotApplicableWaiver)
            {
                missingTableSectionsWaived++;
            }
        }

        if (!hasNotApplicableWaiver)
        {
            expectedHeadersTotal += expectedHeaders.Count;
            matchedHeadersTotal += matchedHeaders.Count;
        }

        if (hasNotApplicableWaiver)
        {
            missingHeaders = new List<string>();
        }

        var templateRepresentative = templateSectionTables[0];
        comparisons.Add(new TableSectionComparison
        {
            SectionCanonicalTitle = sectionKey,
            SectionTitleTemplate = templateRepresentative.SectionTitle,
            ExpectedTableCount = templateSectionTables.Count,
            DetectedTableCount = userSectionTables.Count,
            MatchedHeaderSignatures = matchedHeaders,
            MissingHeaderSignatures = missingHeaders,
            ExtraHeaderSignatures = extraHeaders,
            PlaceholderCellCountInUser = userSectionTables.Sum(item => item.PlaceholderCellCount),
            RedRunCountInUser = userSectionTables.Sum(item => item.RedRunCount),
            MissingTableCount = missingTableCount,
            MissingTablesWaivedByNotApplicable = hasNotApplicableWaiver,
            NotApplicableEvidence = notApplicableEvidence
        });

        if (hasNotApplicableWaiver && userSectionTables.Count == 0)
        {
            matchedSections++;
        }
    }

    foreach (var detected in userBySection)
    {
        if (expectedBySection.ContainsKey(detected.Key))
        {
            continue;
        }

        var userSectionTables = detected.Value;
        var userHeaders = userSectionTables
            .Select(item => item.HeaderSignature)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        comparisons.Add(new TableSectionComparison
        {
            SectionCanonicalTitle = detected.Key,
            SectionTitleTemplate = "(user-only section)",
            ExpectedTableCount = 0,
            DetectedTableCount = userSectionTables.Count,
            MatchedHeaderSignatures = new List<string>(),
            MissingHeaderSignatures = new List<string>(),
            ExtraHeaderSignatures = userHeaders,
            PlaceholderCellCountInUser = userSectionTables.Sum(item => item.PlaceholderCellCount),
            RedRunCountInUser = userSectionTables.Sum(item => item.RedRunCount)
        });
    }

    userHeadersTotal = userBySection
        .SelectMany(section => section.Value
            .Select(table => table.HeaderSignature)
            .Where(signature => !string.IsNullOrWhiteSpace(signature))
            .Select(signature => $"{section.Key}::{signature}"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    var sectionCoverage = sectionsWithExpectedTables == 0
        ? 1.0
        : (double)matchedSections / sectionsWithExpectedTables;

    var headerRecall = expectedHeadersTotal == 0
        ? 1.0
        : (double)matchedHeadersTotal / expectedHeadersTotal;

    var headerPrecision = userHeadersTotal == 0
        ? (expectedHeadersTotal == 0 ? 1.0 : 0.0)
        : (double)matchedHeadersTotal / userHeadersTotal;

    var qualityBand = ComputeTableQualityBand(sectionCoverage, headerRecall, headerPrecision);

    var notes = new List<string>
    {
        "Table validation is advisory-only (non-blocking) for current release.",
        $"Template tables={templateTables.Count}, user tables={userTables.Count}",
        $"Section coverage={Math.Round(sectionCoverage * 100.0, 2)}%",
        $"Header recall={Math.Round(headerRecall * 100.0, 2)}%",
        $"Header precision={Math.Round(headerPrecision * 100.0, 2)}%"
    };

    if (hierarchyFallbackSections > 0)
    {
        notes.Add($"Hierarchical table fallback applied for {hierarchyFallbackSections} section(s) (using descendant section tables when direct section mapping was empty).");
    }

    if (missingTableSectionsWaived > 0)
    {
        notes.Add($"Missing table sections waived by Not Applicable markers: {missingTableSectionsWaived}/{missingTableSections} (advisory, non-blocking).");
    }

    return new TableValidationSummary
    {
        Enabled = true,
        AdvisoryOnly = true,
        TemplateTableCount = templateTables.Count,
        UserTableCount = userTables.Count,
        SectionsWithExpectedTables = sectionsWithExpectedTables,
        SectionsWithDetectedTables = sectionsWithDetectedTables,
        MatchedSections = matchedSections,
        SectionCoveragePercent = Math.Round(sectionCoverage * 100.0, 2),
        ExpectedHeaderSignatures = expectedHeadersTotal,
        MatchedHeaderSignatures = matchedHeadersTotal,
        HeaderRecallPercent = Math.Round(headerRecall * 100.0, 2),
        HeaderPrecisionPercent = Math.Round(headerPrecision * 100.0, 2),
        QualityBand = qualityBand,
        MissingTableSections = missingTableSections,
        MissingTableSectionsWaivedByNotApplicable = missingTableSectionsWaived,
        SectionComparisons = comparisons
            .OrderBy(item => item.SectionTitleTemplate, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        Notes = notes
    };
}

static List<string> ResolveNotApplicableMarkersForSectionHierarchy(string sectionCanonicalTitle, List<TemplateSection> userSections)
{
    if (string.IsNullOrWhiteSpace(sectionCanonicalTitle))
    {
        return new List<string>();
    }

    var evidence = new List<string>();
    var anchors = userSections
        .Select((section, index) => new { section, index })
        .Where(item => string.Equals(item.section.CanonicalTitle, sectionCanonicalTitle, StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var anchor in anchors)
    {
        if (HasNotApplicableMarker(anchor.section))
        {
            evidence.Add($"{anchor.section.Order}: {anchor.section.Title}");
        }
    }

    return evidence
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<TableSnapshot> ResolveTablesFromDescendantUserSections(
    string sectionCanonicalTitle,
    List<TemplateSection> userSections,
    List<TableSnapshot> userTables)
{
    if (string.IsNullOrWhiteSpace(sectionCanonicalTitle))
    {
        return new List<TableSnapshot>();
    }

    var anchors = userSections
        .Select((section, index) => new { section, index })
        .Where(item => string.Equals(item.section.CanonicalTitle, sectionCanonicalTitle, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (anchors.Count == 0)
    {
        return new List<TableSnapshot>();
    }

    var sectionOrders = new HashSet<int>();

    foreach (var anchor in anchors)
    {
        sectionOrders.Add(anchor.section.Order);

        for (var index = anchor.index + 1; index < userSections.Count; index++)
        {
            var candidate = userSections[index];
            if (candidate.Level <= anchor.section.Level)
            {
                break;
            }

            sectionOrders.Add(candidate.Order);
        }
    }

    return userTables
        .Where(table => table.SectionOrder.HasValue && sectionOrders.Contains(table.SectionOrder.Value))
        .GroupBy(table => table.TableIndex)
        .Select(group => group.First())
        .ToList();
}

static string ComputeTableQualityBand(double sectionCoverage, double headerRecall, double headerPrecision)
{
    if (sectionCoverage >= 0.95 && headerRecall >= 0.85 && headerPrecision >= 0.70)
    {
        return "HIGH";
    }

    if (sectionCoverage >= 0.80 && headerRecall >= 0.60 && headerPrecision >= 0.40)
    {
        return "MEDIUM";
    }

    return "LOW";
}

static List<TableSnapshot> ExtractDocumentTables(string filePath, List<TemplateSection> sections)
{
    var tables = new List<TableSnapshot>();

    using var wordDoc = WordprocessingDocument.Open(filePath, false);
    var body = wordDoc.MainDocumentPart?.Document?.Body;
    if (body is null)
    {
        return tables;
    }

    var allParagraphs = body.Descendants<Paragraph>().ToList();
    var paragraphIndexMap = new Dictionary<Paragraph, int>();
    for (var i = 0; i < allParagraphs.Count; i++)
    {
        paragraphIndexMap[allParagraphs[i]] = i + 1;
    }

    var tableIndex = 0;
    foreach (var table in body.Descendants<Table>())
    {
        tableIndex++;
        var rows = table.Elements<TableRow>().ToList();
        var parsedRows = rows
            .Select(row => row.Elements<TableCell>().Select(cell => NormalizeText(cell.InnerText)).ToList())
            .ToList();

        var rowCount = parsedRows.Count;
        var columnCount = parsedRows.Count == 0 ? 0 : parsedRows.Max(row => row.Count);

        var headerCells = rowCount > 0
            ? parsedRows[0].Where(cell => !string.IsNullOrWhiteSpace(cell)).ToList()
            : new List<string>();

        var sampleFirstDataRow = rowCount > 1
            ? parsedRows[1].Where(cell => !string.IsNullOrWhiteSpace(cell)).ToList()
            : new List<string>();

        var headerSignature = BuildHeaderSignature(headerCells, tableIndex);
        var tableParagraphs = table.Descendants<Paragraph>()
            .Where(paragraphIndexMap.ContainsKey)
            .ToList();

        var startParagraphIndex = tableParagraphs.Count == 0
            ? 0
            : tableParagraphs.Min(paragraph => paragraphIndexMap[paragraph]);

        var endParagraphIndex = tableParagraphs.Count == 0
            ? 0
            : tableParagraphs.Max(paragraph => paragraphIndexMap[paragraph]);

        var linkedSection = FindSectionForParagraphIndex(sections, startParagraphIndex);

        var allCells = parsedRows.SelectMany(row => row).ToList();
        var placeholderCells = allCells.Count(LooksLikeTemplatePlaceholderCell);
        var emptyCells = allCells.Count(string.IsNullOrWhiteSpace);
        var redRunCount = CountRedRuns(table);

        tables.Add(new TableSnapshot
        {
            TableIndex = tableIndex,
            SectionOrder = linkedSection?.Order,
            SectionTitle = linkedSection?.Title ?? string.Empty,
            SectionCanonicalTitle = linkedSection?.CanonicalTitle ?? string.Empty,
            StartParagraphIndex = startParagraphIndex,
            EndParagraphIndex = endParagraphIndex,
            RowCount = rowCount,
            ColumnCount = columnCount,
            HeaderCells = headerCells,
            HeaderSignature = headerSignature,
            SampleFirstDataRow = sampleFirstDataRow,
            TableKind = ClassifyTableKind(headerSignature),
            PlaceholderCellCount = placeholderCells,
            EmptyCellCount = emptyCells,
            RedRunCount = redRunCount
        });
    }

    return tables;
}

static TemplateSection? FindSectionForParagraphIndex(List<TemplateSection> sections, int paragraphIndex)
{
    if (paragraphIndex <= 0)
    {
        return null;
    }

    foreach (var section in sections)
    {
        if (paragraphIndex >= section.StartParagraphIndex && paragraphIndex <= section.EndParagraphIndex)
        {
            return section;
        }
    }

    return sections
        .Where(section => section.StartParagraphIndex <= paragraphIndex)
        .OrderByDescending(section => section.StartParagraphIndex)
        .FirstOrDefault();
}

static int CountRedRuns(Table table)
{
    var count = 0;
    foreach (var run in table.Descendants<Run>())
    {
        var colorVal = run.RunProperties?.Color?.Val?.Value ?? string.Empty;
        if (LooksRedColor(colorVal) && !string.IsNullOrWhiteSpace(NormalizeText(run.InnerText)))
        {
            count++;
        }
    }

    return count;
}

static bool LooksLikeTemplatePlaceholderCell(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return Regex.IsMatch(value, "<<[^>]+>>", RegexOptions.IgnoreCase)
           || Regex.IsMatch(value, "\\bTBD\\b", RegexOptions.IgnoreCase)
           || Regex.IsMatch(value, "<\\s*reference\\s*>", RegexOptions.IgnoreCase)
           || Regex.IsMatch(value, "\\bx+\\b", RegexOptions.IgnoreCase)
           || Regex.IsMatch(value, "\\bto be defined\\b", RegexOptions.IgnoreCase);
}

static string BuildHeaderSignature(List<string> headerCells, int tableIndex)
{
    var canonicalHeaders = headerCells
        .Select(CanonicalizeTitle)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToList();

    if (canonicalHeaders.Count == 0)
    {
        return $"table#{tableIndex}";
    }

    return string.Join("|", canonicalHeaders);
}

static string ClassifyTableKind(string headerSignature)
{
    if (string.IsNullOrWhiteSpace(headerSignature))
    {
        return "unknown";
    }

    if (headerSignature.Contains("mark") && headerSignature.Contains("reference") && headerSignature.Contains("title"))
    {
        return "reference-list";
    }

    if (headerSignature.Contains("requirement number") && headerSignature.Contains("description of the requirement"))
    {
        return "requirement-matrix";
    }

    if (headerSignature.Contains("acronym") && headerSignature.Contains("definition"))
    {
        return "acronym-glossary";
    }

    if (headerSignature.Contains("term") && headerSignature.Contains("definition"))
    {
        return "term-glossary";
    }

    return "generic";
}

static bool HasNotApplicableMarker(TemplateSection section)
{
    if (Regex.IsMatch(section.Title, "\\b(not applicable|sans objet|n\\s*/?\\s*a|na)\\b", RegexOptions.IgnoreCase))
    {
        return true;
    }

    return section.ContentParagraphs.Any(item => Regex.IsMatch(item, "\\b(not applicable|sans objet|n\\s*/?\\s*a|na)\\b", RegexOptions.IgnoreCase));
}

static bool IsSectionMissingContentForValidation(List<TemplateSection> sections, int order)
{
    var index = sections.FindIndex(item => item.Order == order);
    if (index < 0)
    {
        return false;
    }

    var section = sections[index];
    if (section.ContentWordCount > 0 || section.NonTextElementCount > 0 || HasNotApplicableMarker(section))
    {
        return false;
    }

    if (HasDescendantSections(sections, index))
    {
        return false;
    }

    return true;
}

static bool HasDescendantSections(List<TemplateSection> sections, int sectionIndex)
{
    var current = sections[sectionIndex];

    for (var index = sectionIndex + 1; index < sections.Count; index++)
    {
        var candidate = sections[index];
        if (candidate.Level <= current.Level)
        {
            break;
        }

        return true;
    }

    return false;
}

static List<RedTextEvidence> ScanRedTextEvidence(string filePath)
{
    var evidences = new List<RedTextEvidence>();

    using var wordDoc = WordprocessingDocument.Open(filePath, false);
    var body = wordDoc.MainDocumentPart?.Document?.Body;
    if (body is null)
    {
        return evidences;
    }

    var paragraphIndex = 0;
    foreach (var paragraph in body.Descendants<Paragraph>())
    {
        paragraphIndex++;
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
        var paragraphText = NormalizeText(paragraph.InnerText);
        if (string.IsNullOrWhiteSpace(paragraphText))
        {
            continue;
        }

        var redRunCount = 0;
        var detectedColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var run in paragraph.Elements<Run>())
        {
            var raw = NormalizeText(run.InnerText);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var colorVal = run.RunProperties?.Color?.Val?.Value ?? string.Empty;
            if (!LooksRedColor(colorVal))
            {
                continue;
            }

            redRunCount++;
            detectedColors.Add(colorVal);
        }

        if (redRunCount > 0)
        {
            evidences.Add(new RedTextEvidence
            {
                ParagraphIndex = paragraphIndex,
                ParagraphStyle = style,
                Color = string.Join(",", detectedColors),
                RedRunCount = redRunCount,
                Snippet = paragraphText.Length <= 280 ? paragraphText : paragraphText[..280] + "..."
            });
        }
    }

    return evidences;
}

static bool LooksRedColor(string colorValue)
{
    if (string.IsNullOrWhiteSpace(colorValue))
    {
        return false;
    }

    var normalized = colorValue.Trim().ToUpperInvariant();
    if (normalized is "FF0000" or "RED")
    {
        return true;
    }

    if (Regex.IsMatch(normalized, "^FF[0-4][0-9A-F][0-4][0-9A-F]$"))
    {
        return true;
    }

    return false;
}

static string ComputeConfidenceBand(double finalScore, double pivotCoverage, int redTextCount, int emptyViolations, int policyMustViolations)
{
    if (redTextCount > 0 || emptyViolations > 0 || policyMustViolations > 0)
    {
        return "LOW";
    }

    if (pivotCoverage >= 1.0 && finalScore >= 90.0)
    {
        return "HIGH";
    }

    if (finalScore >= 70.0)
    {
        return "MEDIUM";
    }

    return "LOW";
}

static List<string> BuildExplainabilityNotes(
    double pivotCoverage,
    double subtitleRecall,
    double subtitlePrecision,
    double orderScore,
    double finalScore,
    SemanticResolution semanticResolution,
    List<RedTextEvidence> redTextEvidence,
    List<EmptySectionViolation> emptySectionViolations,
    ValidationPolicyCompliance policyCompliance)
{
    var notes = new List<string>
    {
        $"Pivot coverage={Math.Round(pivotCoverage * 100.0, 2)}%",
        $"Subtitle recall={Math.Round(subtitleRecall * 100.0, 2)}%",
        $"Subtitle precision={Math.Round(subtitlePrecision * 100.0, 2)}%",
        $"Order score={Math.Round(orderScore * 100.0, 2)}%",
        $"Final score={finalScore}/100"
    };

    if (semanticResolution.AppliedMappings > 0)
    {
        notes.Add($"Semantic mapping applied on {semanticResolution.AppliedMappings} pivot occurrence(s).");
    }
    else
    {
        notes.Add("No semantic alias/mapping was applied.");
    }

    if (redTextEvidence.Count > 0)
    {
        notes.Add($"Red text evidence detected: {redTextEvidence.Count} run(s). Rule P06 failed.");
    }

    if (emptySectionViolations.Count > 0)
    {
        notes.Add($"Empty section violations without Not applicable/Sans objet: {emptySectionViolations.Count}.");
    }

    if (policyCompliance.TotalMustSections > 0 || policyCompliance.TotalShouldSections > 0)
    {
        notes.Add($"Policy profile '{policyCompliance.ProfileName}' applied (must={policyCompliance.TotalMustSections}, should={policyCompliance.TotalShouldSections}).");
    }

    if (policyCompliance.MissingMustSections.Count > 0 || policyCompliance.EmptyMustSections.Count > 0)
    {
        notes.Add($"Policy MUST violations: missing={policyCompliance.MissingMustSections.Count}, empty={policyCompliance.EmptyMustSections.Count}.");
    }

    return notes;
}

static SemanticResolution ApplySemanticMapping(List<PivotBlock> userPivots, SemanticMappingProfile mappingProfile)
{
    var mapped = new List<PivotBlock>();
    var events = new List<MappingEvent>();
    var applied = 0;

    foreach (var pivot in userPivots)
    {
        var rule = mappingProfile.Aliases.FirstOrDefault(item =>
            string.Equals(item.SourceCanonicalTitle, pivot.CanonicalTitle, StringComparison.OrdinalIgnoreCase));

        if (rule is null || rule.TargetCanonicalTitles.Count == 0)
        {
            mapped.Add(pivot);
            continue;
        }

        applied++;
        foreach (var target in rule.TargetCanonicalTitles)
        {
            mapped.Add(new PivotBlock
            {
                PivotOrder = pivot.PivotOrder,
                Title = pivot.Title,
                CanonicalTitle = target,
                ChildCanonicalTitles = pivot.ChildCanonicalTitles,
                SourceCanonicalTitle = pivot.CanonicalTitle,
                SourceTitle = pivot.Title,
                IsMapped = true,
                MappingRule = rule.RuleName
            });
        }

        events.Add(new MappingEvent
        {
            RuleName = rule.RuleName,
            SourceCanonicalTitle = rule.SourceCanonicalTitle,
            TargetCanonicalTitles = rule.TargetCanonicalTitles,
            SourcePivotTitle = pivot.Title,
            SourcePivotOrder = pivot.PivotOrder
        });
    }

    return new SemanticResolution
    {
        MappedPivots = mapped,
        AppliedMappings = applied,
        MappingEvents = events
    };
}

static List<PivotBlock> BuildPivotBlocks(List<TemplateSection> sections, int pivotLevel)
{
    var blocks = new List<PivotBlock>();
    var pivotIndices = sections
        .Select((item, index) => new { item, index })
        .Where(pair => pair.item.Level <= pivotLevel)
        .Select(pair => pair.index)
        .ToList();

    for (var idx = 0; idx < pivotIndices.Count; idx++)
    {
        var pivotIndex = pivotIndices[idx];
        var nextPivotIndex = idx + 1 < pivotIndices.Count ? pivotIndices[idx + 1] : sections.Count;
        var pivot = sections[pivotIndex];

        var children = new List<string>();
        for (var sectionIndex = pivotIndex + 1; sectionIndex < nextPivotIndex; sectionIndex++)
        {
            var child = sections[sectionIndex];
            if (child.Level > pivotLevel)
            {
                children.Add(child.CanonicalTitle);
            }
        }

        blocks.Add(new PivotBlock
        {
            PivotOrder = pivot.Order,
            Title = pivot.Title,
            CanonicalTitle = pivot.CanonicalTitle,
            ChildCanonicalTitles = children
        });
    }

    return blocks;
}

static PivotAlignment AlignPivots(List<PivotBlock> templatePivots, List<PivotBlock> userPivots)
{
    var matches = new List<PivotMatch>();
    var missingTemplate = new List<PivotBlock>();
    var matchedUserIndexes = new HashSet<int>();

    var searchStart = 0;
    foreach (var templatePivot in templatePivots)
    {
        var matchIndex = -1;
        for (var userIndex = searchStart; userIndex < userPivots.Count; userIndex++)
        {
            if (string.Equals(userPivots[userIndex].CanonicalTitle, templatePivot.CanonicalTitle, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = userIndex;
                break;
            }
        }

        if (matchIndex < 0)
        {
            missingTemplate.Add(templatePivot);
            continue;
        }

        matches.Add(new PivotMatch
        {
            TemplateBlock = templatePivot,
            UserBlock = userPivots[matchIndex]
        });

        matchedUserIndexes.Add(matchIndex);
        searchStart = matchIndex + 1;
    }

    var extraUser = userPivots
        .Select((item, index) => new { item, index })
        .Where(pair => !matchedUserIndexes.Contains(pair.index))
        .Select(pair => pair.item)
        .ToList();

    return new PivotAlignment
    {
        Matches = matches,
        MissingTemplatePivots = missingTemplate,
        ExtraUserPivots = extraUser
    };
}

static double ComputeSequenceOrderScore(List<string> templateSequence, List<string> userSequence)
{
    if (templateSequence.Count == 0)
    {
        return 1.0;
    }

    var rows = templateSequence.Count + 1;
    var cols = userSequence.Count + 1;
    var matrix = new int[rows, cols];

    for (var row = 1; row < rows; row++)
    {
        for (var col = 1; col < cols; col++)
        {
            if (string.Equals(templateSequence[row - 1], userSequence[col - 1], StringComparison.OrdinalIgnoreCase))
            {
                matrix[row, col] = matrix[row - 1, col - 1] + 1;
            }
            else
            {
                matrix[row, col] = Math.Max(matrix[row - 1, col], matrix[row, col - 1]);
            }
        }
    }

    var lcsLength = matrix[rows - 1, cols - 1];
    return (double)lcsLength / templateSequence.Count;
}

static List<NumberingIssue> AnalyzeNumberingConsistency(List<TemplateSection> sections)
{
    var issues = new List<NumberingIssue>();
    var siblingTracker = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var section in sections)
    {
        if (section.HeadingNumber is null || section.HeadingNumber.Length == 0)
        {
            continue;
        }

        var depth = section.HeadingNumber.Length;
        var parentKey = depth == 1
            ? "ROOT"
            : string.Join('.', section.HeadingNumber.Take(depth - 1));

        var key = $"{depth}|{parentKey}";
        var currentSibling = section.HeadingNumber[^1];

        if (!siblingTracker.TryGetValue(key, out var previousSibling))
        {
            if (currentSibling != 1)
            {
                issues.Add(new NumberingIssue
                {
                    Order = section.Order,
                    Title = section.Title,
                    ParentKey = parentKey,
                    ExpectedSibling = 1,
                    ActualSibling = currentSibling,
                    Reason = "First sibling does not start at 1"
                });
            }

            siblingTracker[key] = currentSibling;
            continue;
        }

        var expected = previousSibling + 1;
        if (currentSibling != expected)
        {
            issues.Add(new NumberingIssue
            {
                Order = section.Order,
                Title = section.Title,
                ParentKey = parentKey,
                ExpectedSibling = expected,
                ActualSibling = currentSibling,
                Reason = currentSibling < expected
                    ? "Numbering goes backward or repeats"
                    : "Numbering skips one or more siblings"
            });
        }

        siblingTracker[key] = Math.Max(previousSibling, currentSibling);
    }

    return issues;
}

static HeadingDetection DetectHeading(Paragraph paragraph, string text)
{
    if (paragraph.Ancestors<Table>().Any())
    {
        return new HeadingDetection(false, 0, string.Empty);
    }

    var props = paragraph.ParagraphProperties;
    var style = props?.ParagraphStyleId?.Val?.Value ?? string.Empty;
    var outline = props?.OutlineLevel?.Val;

    var levelFromStyle = InferLevelFromStyle(style);
    var levelFromOutline = outline is null ? 0 : (int)outline.Value + 1;
    var levelFromPrefix = InferLevelFromNumberingPrefix(text);

    var level = FirstNonZero(levelFromStyle, levelFromOutline, levelFromPrefix);
    if (level == 0)
    {
        level = 1;
    }

    var styleIndicatesHeading = !string.IsNullOrWhiteSpace(style)
        && Regex.IsMatch(style, "heading|titre", RegexOptions.IgnoreCase);

    var outlineIndicatesHeading = outline is not null;

    var isHeading = styleIndicatesHeading || outlineIndicatesHeading;

    if (LooksLikeTocEntry(text)
        || LooksLikeNoise(text)
        || LooksLikeTablePlaceholder(style, text)
        || LooksLikeNumericDataRow(style, text)
        || LooksLikePlaceholderLabel(text)
        || LooksLikeCoverIdentifierHeading(text))
    {
        isHeading = false;
    }

    return new HeadingDetection(isHeading, level, style);
}

static int FirstNonZero(params int[] values)
{
    foreach (var value in values)
    {
        if (value > 0)
        {
            return value;
        }
    }

    return 0;
}

static void FinalizeSectionStats(TemplateSection section)
{
    var joined = string.Join("\n", section.ContentParagraphs);
    section.ContentWordCount = CountWords(joined);
    section.ContentCharCount = joined.Length;
    if (joined.Length > 0)
    {
        section.ContentPreview = joined.Length <= 220
            ? joined
            : joined[..220] + "...";
        return;
    }

    section.ContentPreview = section.NonTextElementCount > 0
        ? "[non-text content detected]"
        : string.Empty;
}

static string NormalizeText(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    return Regex.Replace(value, "\\s+", " ").Trim();
}

static bool HasRenderableNonTextContent(Paragraph paragraph)
{
    if (paragraph
        .Descendants()
        .Any(element => element.LocalName is "drawing" or "pict" or "object" or "oleObject" or "shape" or "imagedata"))
    {
        return true;
    }

    var xml = paragraph.InnerXml;
    if (string.IsNullOrWhiteSpace(xml))
    {
        return false;
    }

    return Regex.IsMatch(
        xml,
        "<w:drawing|<wp:inline|<wp:anchor|<w:pict|<v:shape|<v:imagedata|<o:OLEObject",
        RegexOptions.IgnoreCase);
}

static bool LooksLikeNoise(string text)
{
    var normalized = text.ToUpperInvariant();
    if (normalized.Contains("PAGEREF") || normalized.Contains("_TOC"))
    {
        return true;
    }

    if (Regex.IsMatch(text, "^Page\\s+\\d+\\s*/\\s*\\d+$", RegexOptions.IgnoreCase))
    {
        return true;
    }

    return false;
}

static bool LooksLikeTocEntry(string text)
{
    return Regex.IsMatch(text, "\\.{3,}\\s*\\d+$");
}

static bool LooksLikeTablePlaceholder(string style, string text)
{
    if (!string.IsNullOrWhiteSpace(style)
        && Regex.IsMatch(style, "tableau|table|listparagraph", RegexOptions.IgnoreCase))
    {
        if (Regex.IsMatch(text, "^title$|^link$", RegexOptions.IgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static bool LooksLikePlaceholderLabel(string text)
{
    return Regex.IsMatch(text, "^title$|^link$", RegexOptions.IgnoreCase)
           || Regex.IsMatch(text, "^requirement number|^description of the requirement|^input requirement", RegexOptions.IgnoreCase);
}

static bool LooksLikeCoverIdentifierHeading(string text)
{
    var normalized = NormalizeText(text);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return false;
    }

    if (Regex.IsMatch(normalized, "^-?\\d{6,}$"))
    {
        return true;
    }

    if (Regex.IsMatch(normalized, "^[A-Za-z]-?\\d{5,}$"))
    {
        return true;
    }

    return false;
}

static bool LooksLikeNumericDataRow(string style, string text)
{
    if (!string.IsNullOrWhiteSpace(style)
        && Regex.IsMatch(style, "listparagraph|tableau|table", RegexOptions.IgnoreCase)
        && Regex.IsMatch(text, "^\\d"))
    {
        return true;
    }

    if (Regex.IsMatch(text, "^\\d+[\\s./]+\\d+[\\s./]+\\d+"))
    {
        return true;
    }

    if (Regex.IsMatch(text, "^\\d+(?:\\.\\d+)?\\s*(mm|cm|m|g|kg|hours?|days?|cycles?|/axis)", RegexOptions.IgnoreCase))
    {
        return true;
    }

    if (Regex.IsMatch(text, "\\(standard\\s*#\\)", RegexOptions.IgnoreCase))
    {
        return true;
    }

    return false;
}

static int InferLevelFromStyle(string style)
{
    if (string.IsNullOrWhiteSpace(style))
    {
        return 0;
    }

    var match = Regex.Match(style, "(heading|titre)\\s*([1-9])", RegexOptions.IgnoreCase);
    if (!match.Success)
    {
        return 0;
    }

    return int.TryParse(match.Groups[2].Value, out var level) ? level : 0;
}

static int InferLevelFromNumberingPrefix(string text)
{
    var numbers = ExtractLeadingNumbers(text);
    return numbers?.Length ?? 0;
}

static int[]? ExtractLeadingNumbers(string text)
{
    var match = Regex.Match(text, "^(\\d+(?:\\.\\d+)*)\\s+");
    if (!match.Success)
    {
        return null;
    }

    var parts = match.Groups[1].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
    var values = new List<int>();
    foreach (var part in parts)
    {
        if (!int.TryParse(part, out var number))
        {
            return null;
        }

        values.Add(number);
    }

    return values.Count == 0 ? null : values.ToArray();
}

static string CanonicalizeTitle(string title)
{
    var cleaned = title.Trim().ToLowerInvariant();
    cleaned = Regex.Replace(cleaned, "^\\d+(?:\\.\\d+)*\\s*[-.)]?\\s*", "");
    cleaned = Regex.Replace(cleaned, "[^a-z0-9 ]", " ");
    cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
    return cleaned;
}

static SemanticMappingProfile LoadSemanticMappingProfile(string? mappingPath)
{
    if (!string.IsNullOrWhiteSpace(mappingPath) && File.Exists(mappingPath))
    {
        var raw = File.ReadAllText(mappingPath);
        var parsed = JsonSerializer.Deserialize<SemanticMappingProfile>(raw, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (parsed is not null)
        {
            parsed.ProfileName = string.IsNullOrWhiteSpace(parsed.ProfileName) ? "custom" : parsed.ProfileName;
            parsed.Version = string.IsNullOrWhiteSpace(parsed.Version) ? "1.0" : parsed.Version;
            return parsed;
        }
    }

    return BuildDefaultSemanticMappingProfile();
}

static SemanticMappingProfile BuildDefaultSemanticMappingProfile()
{
    return new SemanticMappingProfile
    {
        ProfileName = "default-automotive-template",
        Version = "1.0",
        Aliases = new List<SemanticAliasRule>
        {
            new()
            {
                RuleName = "merged-purpose-scope",
                SourceCanonicalTitle = "purpose and scope of application",
                TargetCanonicalTitles = new List<string> { "purpose", "scope" }
            },
            new()
            {
                RuleName = "merged-quoted-terminology",
                SourceCanonicalTitle = "quoted documents and terminology",
                TargetCanonicalTitles = new List<string> { "quoted documents", "terminology" }
            },
            new()
            {
                RuleName = "output-to-requirements",
                SourceCanonicalTitle = "output requirements",
                TargetCanonicalTitles = new List<string> { "requirements" }
            },
            new()
            {
                RuleName = "integration-validation-singular",
                SourceCanonicalTitle = "integration and validation requirement",
                TargetCanonicalTitles = new List<string> { "integration and validation requirements" }
            }
        }
    };
}

static ValidationPolicyProfile LoadValidationPolicyProfile(string? policyPath)
{
    var effectivePath = policyPath;
    if (string.IsNullOrWhiteSpace(effectivePath))
    {
        var candidatePaths = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "template-policy.json"),
            Path.Combine(Environment.CurrentDirectory, "TemplateOneShotExtractor", "template-policy.json")
        };

        var discovered = candidatePaths.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            effectivePath = discovered;
        }
    }

    if (!string.IsNullOrWhiteSpace(effectivePath))
    {
        if (!File.Exists(effectivePath))
        {
            throw new FileNotFoundException($"Policy file not found: {effectivePath}");
        }

        var raw = File.ReadAllText(effectivePath);
        var parsed = JsonSerializer.Deserialize<ValidationPolicyProfile>(raw, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (parsed is null)
        {
            throw new InvalidDataException("Invalid policy JSON format.");
        }

        parsed.ProfileName = string.IsNullOrWhiteSpace(parsed.ProfileName) ? "custom-policy" : parsed.ProfileName;
        parsed.Version = string.IsNullOrWhiteSpace(parsed.Version) ? "1.0" : parsed.Version;
        parsed.MustCanonicalTitles = NormalizePolicyCanonicalTitles(parsed.MustCanonicalTitles);
        parsed.ShouldCanonicalTitles = NormalizePolicyCanonicalTitles(parsed.ShouldCanonicalTitles);
        parsed.OptionalCanonicalTitles = NormalizePolicyCanonicalTitles(parsed.OptionalCanonicalTitles);
        return parsed;
    }

    return BuildDefaultValidationPolicyProfile();
}

static ValidationPolicyProfile BuildDefaultValidationPolicyProfile()
{
    return new ValidationPolicyProfile
    {
        ProfileName = "default-open-policy",
        Version = "1.0",
        AllowNotApplicableForEmpty = true,
        MustCanonicalTitles = new List<string>(),
        ShouldCanonicalTitles = new List<string>(),
        OptionalCanonicalTitles = new List<string>()
    };
}

static List<string> NormalizePolicyCanonicalTitles(List<string>? titles)
{
    if (titles is null)
    {
        return new List<string>();
    }

    return titles
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(CanonicalizeTitle)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static ValidationPolicyCompliance BuildPolicyCompliance(
    ValidationPolicyProfile policyProfile,
    PivotAlignment alignment,
    List<TemplateSection> userSections)
{
    var matchedTemplateTitles = alignment.Matches
        .Select(item => item.TemplateBlock.CanonicalTitle)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var userSectionByOrder = userSections.ToDictionary(item => item.Order, item => item);

    var missingMust = policyProfile.MustCanonicalTitles
        .Where(item => !matchedTemplateTitles.Contains(item))
        .OrderBy(item => item)
        .ToList();

    var missingShould = policyProfile.ShouldCanonicalTitles
        .Where(item => !matchedTemplateTitles.Contains(item))
        .OrderBy(item => item)
        .ToList();

    var presentOptional = policyProfile.OptionalCanonicalTitles
        .Where(item => matchedTemplateTitles.Contains(item))
        .OrderBy(item => item)
        .ToList();

    var emptyMust = new List<string>();
    var emptyShould = new List<string>();

    foreach (var match in alignment.Matches)
    {
        if (!userSectionByOrder.TryGetValue(match.UserBlock.PivotOrder, out var userSection))
        {
            continue;
        }

        var isEmptyWithoutAllowedMarker = IsSectionMissingContentForValidation(userSections, userSection.Order)
                                          && (!policyProfile.AllowNotApplicableForEmpty || !HasNotApplicableMarker(userSection));
        if (!isEmptyWithoutAllowedMarker)
        {
            continue;
        }

        if (policyProfile.MustCanonicalTitles.Contains(match.TemplateBlock.CanonicalTitle, StringComparer.OrdinalIgnoreCase))
        {
            emptyMust.Add(match.TemplateBlock.CanonicalTitle);
        }
        else if (policyProfile.ShouldCanonicalTitles.Contains(match.TemplateBlock.CanonicalTitle, StringComparer.OrdinalIgnoreCase))
        {
            emptyShould.Add(match.TemplateBlock.CanonicalTitle);
        }
    }

    emptyMust = emptyMust.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
    emptyShould = emptyShould.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();

    return new ValidationPolicyCompliance
    {
        ProfileName = policyProfile.ProfileName,
        Version = policyProfile.Version,
        AllowNotApplicableForEmpty = policyProfile.AllowNotApplicableForEmpty,
        TotalMustSections = policyProfile.MustCanonicalTitles.Count,
        TotalShouldSections = policyProfile.ShouldCanonicalTitles.Count,
        TotalOptionalSections = policyProfile.OptionalCanonicalTitles.Count,
        MissingMustSections = missingMust,
        MissingShouldSections = missingShould,
        EmptyMustSections = emptyMust,
        EmptyShouldSections = emptyShould,
        PresentOptionalSections = presentOptional
    };
}

static int CountWords(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return 0;
    }

    return Regex.Matches(text, "\\b[\\p{L}\\p{N}]+\\b").Count;
}

static string CreateTemplateWorkingCopy(string sourcePath)
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"template-extractor-{Guid.NewGuid():N}.docx");

    using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        input.CopyTo(output);
    }

    return tempPath;
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
    }
}

file sealed record HeadingDetection(bool IsHeading, int Level, string StyleName);

file sealed class TemplateBlueprintReport
{
    public ReportMetadata Metadata { get; set; } = new();
    public TemplateStats Stats { get; set; } = new();
    public TemplateTableInventory TableInventory { get; set; } = new();
    public TemplateQualitySummary Quality { get; set; } = new();
    public List<TemplateSection> Headings { get; set; } = new();
    public List<string> OrderedTitleList { get; set; } = new();
}

file sealed class TemplateTableInventory
{
    public int TotalTables { get; set; }
    public int SectionsWithTables { get; set; }
    public int DistinctHeaderSignatures { get; set; }
    public List<TableSnapshot> Tables { get; set; } = new();
}

file sealed class ReportMetadata
{
    public string TemplatePath { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ExtractedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public string Tool { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

file sealed class TemplateStats
{
    public int TotalHeadings { get; set; }
    public int TopLevelHeadings { get; set; }
    public int DeepestLevel { get; set; }
    public int DuplicateTitleGroups { get; set; }
    public int LevelJumpIssues { get; set; }
    public int NumberingIssues { get; set; }
    public int EmptySections { get; set; }
    public int WeakContentSections { get; set; }
}

file sealed class TemplateQualitySummary
{
    public List<DuplicateTitle> DuplicateTitles { get; set; } = new();
    public List<LevelJumpIssue> LevelJumps { get; set; } = new();
    public List<NumberingIssue> NumberingIssues { get; set; } = new();
    public List<int> EmptySectionOrders { get; set; } = new();
    public List<int> WeakContentSectionOrders { get; set; } = new();
}

file sealed class TemplateSection
{
    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CanonicalTitle { get; set; } = string.Empty;
    public int Level { get; set; }
    public string ParagraphStyle { get; set; } = string.Empty;
    public int StartParagraphIndex { get; set; }
    public int EndParagraphIndex { get; set; }
    public int[]? HeadingNumber { get; set; }
    public int ContentWordCount { get; set; }
    public int ContentCharCount { get; set; }
    public int NonTextElementCount { get; set; }
    public string ContentPreview { get; set; } = string.Empty;
    public List<string> ContentParagraphs { get; set; } = new();
}

file sealed class DuplicateTitle
{
    public string CanonicalTitle { get; set; } = string.Empty;
    public List<int> Orders { get; set; } = new();
    public List<string> Titles { get; set; } = new();
}

file sealed class LevelJumpIssue
{
    public int PreviousOrder { get; set; }
    public int PreviousLevel { get; set; }
    public string PreviousTitle { get; set; } = string.Empty;
    public int CurrentOrder { get; set; }
    public int CurrentLevel { get; set; }
    public string CurrentTitle { get; set; } = string.Empty;
    public int Delta { get; set; }
}

file sealed class NumberingIssue
{
    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ParentKey { get; set; } = string.Empty;
    public int ExpectedSibling { get; set; }
    public int ActualSibling { get; set; }
    public string Reason { get; set; } = string.Empty;
}

file sealed class ValidationReport
{
    public ValidationMetadata Metadata { get; set; } = new();
    public SemanticMappingSummary SemanticMapping { get; set; } = new();
    public ValidationPolicyCompliance PolicyCompliance { get; set; } = new();
    public ValidationContract Contract { get; set; } = new();
    public ValidationQualityGates QualityGates { get; set; } = new();
    public ValidationScores Scores { get; set; } = new();
    public ValidationFindings Findings { get; set; } = new();
    public TableValidationSummary TableValidation { get; set; } = new();
    public ValidationDecision Decision { get; set; } = new();
}

file sealed class TableValidationSummary
{
    public bool Enabled { get; set; }
    public bool AdvisoryOnly { get; set; }
    public int TemplateTableCount { get; set; }
    public int UserTableCount { get; set; }
    public int SectionsWithExpectedTables { get; set; }
    public int SectionsWithDetectedTables { get; set; }
    public int MatchedSections { get; set; }
    public double SectionCoveragePercent { get; set; }
    public int ExpectedHeaderSignatures { get; set; }
    public int MatchedHeaderSignatures { get; set; }
    public double HeaderRecallPercent { get; set; }
    public double HeaderPrecisionPercent { get; set; }
    public string QualityBand { get; set; } = string.Empty;
    public int MissingTableSections { get; set; }
    public int MissingTableSectionsWaivedByNotApplicable { get; set; }
    public List<TableSectionComparison> SectionComparisons { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

file sealed class TableSectionComparison
{
    public string SectionCanonicalTitle { get; set; } = string.Empty;
    public string SectionTitleTemplate { get; set; } = string.Empty;
    public int ExpectedTableCount { get; set; }
    public int DetectedTableCount { get; set; }
    public List<string> MatchedHeaderSignatures { get; set; } = new();
    public List<string> MissingHeaderSignatures { get; set; } = new();
    public List<string> ExtraHeaderSignatures { get; set; } = new();
    public int PlaceholderCellCountInUser { get; set; }
    public int RedRunCountInUser { get; set; }
    public int MissingTableCount { get; set; }
    public bool MissingTablesWaivedByNotApplicable { get; set; }
    public List<string> NotApplicableEvidence { get; set; } = new();
}

file sealed class TableSnapshot
{
    public int TableIndex { get; set; }
    public int? SectionOrder { get; set; }
    public string SectionTitle { get; set; } = string.Empty;
    public string SectionCanonicalTitle { get; set; } = string.Empty;
    public int StartParagraphIndex { get; set; }
    public int EndParagraphIndex { get; set; }
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    public List<string> HeaderCells { get; set; } = new();
    public string HeaderSignature { get; set; } = string.Empty;
    public List<string> SampleFirstDataRow { get; set; } = new();
    public string TableKind { get; set; } = string.Empty;
    public int PlaceholderCellCount { get; set; }
    public int EmptyCellCount { get; set; }
    public int RedRunCount { get; set; }
}

file sealed class SemanticMappingSummary
{
    public string ProfileName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int AppliedMappings { get; set; }
    public List<MappingEvent> MappingEvents { get; set; } = new();
}

file sealed class ValidationQualityGates
{
    public string RuleCode { get; set; } = string.Empty;
    public bool RequiresNotApplicableForEmptySections { get; set; }
    public bool RedTextDetected { get; set; }
    public List<RedTextEvidence> RedTextEvidence { get; set; } = new();
    public List<EmptySectionViolation> EmptySectionViolations { get; set; } = new();
}

file sealed class RedTextEvidence
{
    public int ParagraphIndex { get; set; }
    public string ParagraphStyle { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int RedRunCount { get; set; }
    public string Snippet { get; set; } = string.Empty;
}

file sealed class EmptySectionViolation
{
    public int Order { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CanonicalTitle { get; set; } = string.Empty;
    public string Requirement { get; set; } = string.Empty;
}

file sealed class SemanticMappingProfile
{
    public string ProfileName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<SemanticAliasRule> Aliases { get; set; } = new();
}

file sealed class ValidationPolicyProfile
{
    public string ProfileName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool AllowNotApplicableForEmpty { get; set; } = true;
    public List<string> MustCanonicalTitles { get; set; } = new();
    public List<string> ShouldCanonicalTitles { get; set; } = new();
    public List<string> OptionalCanonicalTitles { get; set; } = new();
}

file sealed class ValidationPolicyCompliance
{
    public string ProfileName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool AllowNotApplicableForEmpty { get; set; }
    public int TotalMustSections { get; set; }
    public int TotalShouldSections { get; set; }
    public int TotalOptionalSections { get; set; }
    public List<string> MissingMustSections { get; set; } = new();
    public List<string> MissingShouldSections { get; set; } = new();
    public List<string> EmptyMustSections { get; set; } = new();
    public List<string> EmptyShouldSections { get; set; } = new();
    public List<string> PresentOptionalSections { get; set; } = new();
}

file sealed class SemanticAliasRule
{
    public string RuleName { get; set; } = string.Empty;
    public string SourceCanonicalTitle { get; set; } = string.Empty;
    public List<string> TargetCanonicalTitles { get; set; } = new();
}

file sealed class SemanticResolution
{
    public List<PivotBlock> MappedPivots { get; set; } = new();
    public int AppliedMappings { get; set; }
    public List<MappingEvent> MappingEvents { get; set; } = new();
}

file sealed class MappingEvent
{
    public string RuleName { get; set; } = string.Empty;
    public string SourceCanonicalTitle { get; set; } = string.Empty;
    public List<string> TargetCanonicalTitles { get; set; } = new();
    public string SourcePivotTitle { get; set; } = string.Empty;
    public int SourcePivotOrder { get; set; }
}

file sealed class ValidationMetadata
{
    public string Tool { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset ValidatedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public string TemplatePath { get; set; } = string.Empty;
    public string TemplateBlueprintPath { get; set; } = string.Empty;
    public string UserDocumentPath { get; set; } = string.Empty;
}

file sealed class ValidationContract
{
    public int PivotLevel { get; set; }
    public int TotalRequiredPivots { get; set; }
    public List<string> RequiredPivotTitles { get; set; } = new();
    public List<string> RequiredPivotCanonicalTitles { get; set; } = new();
}

file sealed class ValidationScores
{
    public double PivotCoveragePercent { get; set; }
    public double SubtitleRecallPercent { get; set; }
    public double SubtitlePrecisionPercent { get; set; }
    public double OrderScorePercent { get; set; }
    public double FinalScore { get; set; }
    public string ConfidenceBand { get; set; } = string.Empty;
}

file sealed class ValidationFindings
{
    public List<string> MissingRequiredPivots { get; set; } = new();
    public List<string> ExtraUserPivots { get; set; } = new();
    public List<PivotComparison> PivotComparisons { get; set; } = new();
}

file sealed class ValidationDecision
{
    public string Verdict { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
    public List<string> ExplainabilityNotes { get; set; } = new();
}

file sealed class PivotComparison
{
    public string PivotTitle { get; set; } = string.Empty;
    public string PivotCanonicalTitle { get; set; } = string.Empty;
    public int? PivotOrderTemplate { get; set; }
    public int? PivotOrderUser { get; set; }
    public int TemplateSubtitleCount { get; set; }
    public int UserSubtitleCount { get; set; }
    public int MatchedSubtitleCount { get; set; }
    public List<string> MissingSubtitles { get; set; } = new();
    public List<string> ExtraSubtitles { get; set; } = new();
}

file sealed class PivotBlock
{
    public int PivotOrder { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CanonicalTitle { get; set; } = string.Empty;
    public List<string> ChildCanonicalTitles { get; set; } = new();
    public bool IsMapped { get; set; }
    public string SourceTitle { get; set; } = string.Empty;
    public string SourceCanonicalTitle { get; set; } = string.Empty;
    public string MappingRule { get; set; } = string.Empty;
}

file sealed class PivotMatch
{
    public PivotBlock TemplateBlock { get; set; } = new();
    public PivotBlock UserBlock { get; set; } = new();
}

file sealed class PivotAlignment
{
    public List<PivotMatch> Matches { get; set; } = new();
    public List<PivotBlock> MissingTemplatePivots { get; set; } = new();
    public List<PivotBlock> ExtraUserPivots { get; set; } = new();
}
