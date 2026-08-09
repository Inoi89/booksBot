using System.Xml;
using System.Xml.Linq;
using booksBot.Core.Models;

namespace booksBot.Core.Services;

public static class Fb2PreviewParser
{
    private const int MaxAnnotationLength = 3_000;
    private const int MaxCoverEncodedCharacters = 13_000_000;
    private const int MaxCoverBytes = 9_500_000;

    private static readonly HashSet<string> SupportedCoverTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png"
    };

    public static BookPreview Parse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 100_000_000,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var annotation = ReadAnnotation(document);
        var (coverBytes, contentType) = ReadCover(document);
        return new BookPreview(annotation, null, coverBytes, contentType);
    }

    private static string ReadAnnotation(XDocument document)
    {
        var annotation = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "annotation");
        if (annotation is null)
        {
            return string.Empty;
        }

        var paragraphs = annotation
            .Descendants()
            .Where(element => element.Name.LocalName == "p")
            .Select(element => NormalizeWhitespace(element.Value))
            .Where(value => value.Length > 0)
            .ToArray();
        var value = paragraphs.Length > 0
            ? string.Join("\n\n", paragraphs)
            : NormalizeWhitespace(annotation.Value);

        return value.Length <= MaxAnnotationLength
            ? value
            : $"{value[..(MaxAnnotationLength - 1)].TrimEnd()}…";
    }

    private static (byte[]? Bytes, string? ContentType) ReadCover(XDocument document)
    {
        var coverPage = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "coverpage");
        var image = coverPage?.Descendants().FirstOrDefault(element => element.Name.LocalName == "image");
        var reference = image?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, null);
        }

        var binaryId = reference.Trim().TrimStart('#');
        var binary = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "binary"
            && string.Equals(
                element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value,
                binaryId,
                StringComparison.Ordinal));
        var contentType = binary?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "content-type")?.Value;
        if (binary is null
            || string.IsNullOrWhiteSpace(contentType)
            || !SupportedCoverTypes.Contains(contentType)
            || binary.Value.Length > MaxCoverEncodedCharacters)
        {
            return (null, null);
        }

        try
        {
            var bytes = Convert.FromBase64String(binary.Value);
            return bytes.Length is > 0 and <= MaxCoverBytes
                ? (bytes, contentType)
                : (null, null);
        }
        catch (FormatException)
        {
            return (null, null);
        }
    }

    private static string NormalizeWhitespace(string value) => string.Join(
        ' ',
        value.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
