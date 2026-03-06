using System.Text.RegularExpressions;
using WordOpenXml.Core.Models;

namespace WordOpenXml.Core;

public class CdcAnalysisService
{
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NumberingPrefixRegex = new(@"^\d+(\.\d+)*\s*[-:.)]?\s*", RegexOptions.Compiled);

    public DocumentAnalysis Analyze(string sourceFile, IReadOnlyList<Section> rawSections)
    {
        var normalizedSections = new List<NormalizedSection>();
        var filteredOutCount = 0;

        foreach (var section in rawSections)
        {
            if (IsNoise(section))
            {
                filteredOutCount++;
                continue;
            }

            var title = NormalizeWhitespace(section.Title);
            var canonicalTitle = CanonicalizeTitle(title);
            if (string.IsNullOrWhiteSpace(canonicalTitle))
            {
                filteredOutCount++;
                continue;
            }

            normalizedSections.Add(new NormalizedSection
            {
                Level = section.Level,
                Title = title,
                Content = section.Content,
                CanonicalTitle = canonicalTitle
            });
        }

        return new DocumentAnalysis
        {
            SourceFile = sourceFile,
            RawSectionCount = rawSections.Count,
            FilteredOutCount = filteredOutCount,
            Sections = normalizedSections
        };
    }

    public ComparisonResult Compare(DocumentAnalysis user, DocumentAnalysis reference)
    {
        var userTitles = user.Sections
            .Select(section => section.CanonicalTitle)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var referenceTitles = reference.Sections
            .Select(section => section.CanonicalTitle)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return BuildComparison(userTitles, referenceTitles);
    }

    public ComparisonResult Compare(DocumentAnalysis user, DocumentAnalysis referenceA, DocumentAnalysis referenceB)
    {
        var mergedReferences = referenceA.Sections
            .Concat(referenceB.Sections)
            .Select(section => section.CanonicalTitle)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var userTitles = user.Sections
            .Select(section => section.CanonicalTitle)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        return BuildComparison(userTitles, mergedReferences);
    }

    private static ComparisonResult BuildComparison(HashSet<string> userTitles, HashSet<string> referenceTitles)
    {
        var common = referenceTitles.Intersect(userTitles, StringComparer.Ordinal).OrderBy(item => item).ToList();
        var missingInUser = referenceTitles.Except(userTitles, StringComparer.Ordinal).OrderBy(item => item).ToList();
        var extraInUser = userTitles.Except(referenceTitles, StringComparer.Ordinal).OrderBy(item => item).ToList();

        var coverage = referenceTitles.Count == 0
            ? 100d
            : Math.Round((double)common.Count / referenceTitles.Count * 100d, 2);

        return new ComparisonResult
        {
            ReferenceCount = referenceTitles.Count,
            UserCount = userTitles.Count,
            Common = common,
            MissingInUser = missingInUser,
            ExtraInUser = extraInUser,
            CoveragePercent = coverage
        };
    }

    private static bool IsNoise(Section section)
    {
        var title = NormalizeWhitespace(section.Title).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        if (title.Contains("PAGEREF", StringComparison.Ordinal))
        {
            return true;
        }

        if (title.StartsWith("TOC ", StringComparison.Ordinal) || title.Contains("TOC \\O", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string CanonicalizeTitle(string title)
    {
        var result = NumberingPrefixRegex.Replace(title ?? string.Empty, string.Empty);
        result = NormalizeWhitespace(result).ToUpperInvariant();
        return result.Trim();
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return MultiSpaceRegex.Replace(value, " ").Trim();
    }
}
