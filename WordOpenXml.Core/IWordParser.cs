using WordOpenXml.Core.Models;

namespace WordOpenXml.Core;

public interface IWordParser
{
    IReadOnlyList<Section> Parse(byte[] docxBytes);
}