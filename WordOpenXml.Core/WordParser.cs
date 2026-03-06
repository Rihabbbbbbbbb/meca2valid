using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordOpenXml.Core.Models;

namespace WordOpenXml.Core;

public class WordParser : IWordParser
{
	public IReadOnlyList<Section> Parse(byte[] docxBytes)
	{
		if (docxBytes is null || docxBytes.Length == 0)
		{
			return [];
		}

		using var stream = new MemoryStream(docxBytes);
		using var document = WordprocessingDocument.Open(stream, false);

		var body = document.MainDocumentPart?.Document?.Body;
		if (body is null)
		{
			return [];
		}

		var sections = new List<Section>();
		Section? currentSection = null;

		foreach (var paragraph in body.Descendants<Paragraph>())
		{
			var text = paragraph.InnerText?.Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}

			var headingLevel = GetHeadingLevel(paragraph);
			if (headingLevel.HasValue)
			{
				currentSection = new Section
				{
					Level = headingLevel.Value,
					Title = text,
					Content = string.Empty
				};
				sections.Add(currentSection);
				continue;
			}

			if (currentSection is null)
			{
				currentSection = new Section
				{
					Level = 0,
					Title = "Document",
					Content = string.Empty
				};
				sections.Add(currentSection);
			}

			if (currentSection.Content.Length == 0)
			{
				currentSection.Content = text;
			}
			else
			{
				currentSection.Content += Environment.NewLine + text;
			}
		}

		return sections;
	}

	private static int? GetHeadingLevel(Paragraph paragraph)
	{
		var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
		if (!string.IsNullOrWhiteSpace(styleId) && styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
		{
			var suffix = new string(styleId.Skip("Heading".Length).ToArray());
			if (int.TryParse(suffix, out var parsedLevel))
			{
				return parsedLevel;
			}

			return 1;
		}

		var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
		if (outlineLevel is not null)
		{
			return (int)outlineLevel + 1;
		}

		return null;
	}
}
