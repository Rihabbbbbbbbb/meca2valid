namespace WordOpenXml.Core.Models;

public class NormalizedSection
{
    public int Level { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string CanonicalTitle { get; set; } = string.Empty;
}

public class DocumentAnalysis
{
    public string SourceFile { get; set; } = string.Empty;
    public int RawSectionCount { get; set; }
    public int FilteredOutCount { get; set; }
    public IReadOnlyList<NormalizedSection> Sections { get; set; } = [];
}
