namespace WordOpenXml.Core.Models;

public class ComparisonResult
{
    public int ReferenceCount { get; set; }
    public int UserCount { get; set; }
    public IReadOnlyList<string> MissingInUser { get; set; } = [];
    public IReadOnlyList<string> ExtraInUser { get; set; } = [];
    public IReadOnlyList<string> Common { get; set; } = [];
    public double CoveragePercent { get; set; }
}

public class CdcComparisonReport
{
    public DocumentAnalysis User { get; set; } = new();
    public DocumentAnalysis Template { get; set; } = new();
    public DocumentAnalysis Guide { get; set; } = new();
    public ComparisonResult VsTemplate { get; set; } = new();
    public ComparisonResult VsGuide { get; set; } = new();
    public ComparisonResult VsTemplateAndGuide { get; set; } = new();
}
