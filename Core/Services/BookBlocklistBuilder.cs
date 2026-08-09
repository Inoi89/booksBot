using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using booksBot.Core.Models;
using Microsoft.VisualBasic.FileIO;

namespace booksBot.Core.Services;

public static partial class BookBlocklistBuilder
{
    static BookBlocklistBuilder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly string[] LiteraryMarkers =
    [
        "книга", "книги", "брошюра", "брошюры", "печатное издание", "печатный материал",
        "текст книги", "литературное произведение", "произведение", "роман", "трактат", "сборник"
    ];

    private static readonly string[] MetadataMarkers =
    [
        "издательств", "источник публикации", "типограф", "сайт", "интернет-ресурс", "газета", "журнал"
    ];

    private static readonly HashSet<string> IgnoredAuthorTokens = new(StringComparer.Ordinal)
    {
        "автор", "авторы", "неизвестный", "неизвестен", "редактор", "составитель", "переводчик"
    };

    private static readonly HashSet<string> NounCaseSuffixes = new(StringComparer.Ordinal)
    {
        "а", "я", "у", "ю", "е", "и", "ом", "ем", "ым", "ои"
    };

    private static readonly HashSet<string> AdjectiveCaseSuffixes = new(StringComparer.Ordinal)
    {
        "ии", "ыи", "ои", "ая", "яя", "ое", "ее", "ые", "ие", "ого", "его", "ому", "ему",
        "ым", "им", "ом", "ем", "ую", "юю", "ых", "их", "ыми", "ими"
    };

    public static async Task<BookBlocklistBuildResult> BuildAsync(
        IEnumerable<BookEntry> books,
        string sourcePath,
        string outputPath,
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        var materials = ReadMaterials(sourcePath);
        var candidates = materials
            .SelectMany(ExtractCandidates)
            .GroupBy(candidate => (candidate.MaterialId, candidate.NormalizedTitle))
            .Select(group => group.OrderByDescending(candidate => candidate.IsPrimaryTitle).First())
            .ToArray();
        var candidatesByTitle = candidates
            .GroupBy(candidate => candidate.NormalizedTitle, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var reportRows = new List<ReportRow>();
        var blocked = new Dictionary<string, BlockedMatch>(StringComparer.Ordinal);
        var exactCatalogIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(book.LibId)
                || !candidatesByTitle.TryGetValue(book.TitleNormalized ?? string.Empty, out var titleCandidates))
            {
                continue;
            }

            exactCatalogIds.Add(book.LibId);
            foreach (var candidate in titleCandidates)
            {
                var authorEvidence = FindAuthorEvidence(book, candidate.MaterialText);
                var shouldBlock = candidate.IsPrimaryTitle && authorEvidence is not null;
                var status = shouldBlock ? "BLOCKED" : "REVIEW";
                var evidence = shouldBlock
                    ? $"exact title; catalog author evidence '{authorEvidence}' occurs in source material"
                    : !candidate.IsPrimaryTitle
                        ? "exact title, but the quoted text is not confidently the material title"
                        : "exact title, but catalog author is not explicitly confirmed by the source material";

                reportRows.Add(new ReportRow(
                    status,
                    candidate.MaterialId,
                    candidate.Title,
                    book.LibId,
                    book.Title,
                    string.Join("; ", (book.Authors ?? []).Select(author => author.DisplayName)),
                    evidence,
                    candidate.MaterialText));

                if (shouldBlock)
                {
                    blocked.TryAdd(book.LibId, new BlockedMatch(
                        book.LibId,
                        candidate.MaterialId,
                        book.Title,
                        string.Join("; ", (book.Authors ?? []).Select(author => author.DisplayName))));
                }
            }
        }

        string sourceHash;
        await using (var sourceStream = File.OpenRead(sourcePath))
        {
            sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream, cancellationToken));
        }
        var blocklistLines = new List<string>
        {
            "# BookBot exact-ID blocklist",
            $"# Source: {Path.GetFileName(sourcePath)}",
            $"# Source SHA-256: {sourceHash}",
            "# Generated conservatively: exact normalized title + catalog author evidence in the source material.",
            "# REVIEW rows are intentionally not blocked; see the companion report.",
            string.Empty
        };
        blocklistLines.AddRange(blocked.Values
            .OrderBy(match => int.TryParse(match.BookId, out var id) ? id : int.MaxValue)
            .ThenBy(match => match.BookId, StringComparer.Ordinal)
            .Select(match => $"{match.BookId} # RKN {match.MaterialId} | {OneLine(match.Title)} | {OneLine(match.Authors)}"));

        var reportLines = new List<string>
        {
            "status\trkn_id\tcandidate_title\tbook_id\tcatalog_title\tcatalog_authors\tevidence\tmaterial"
        };
        reportLines.AddRange(reportRows
            .OrderBy(row => row.Status, StringComparer.Ordinal)
            .ThenBy(row => row.MaterialId)
            .ThenBy(row => row.BookId, StringComparer.Ordinal)
            .Select(row => string.Join('\t', new[]
            {
                row.Status,
                row.MaterialId.ToString(),
                Tsv(row.CandidateTitle),
                Tsv(row.BookId),
                Tsv(row.CatalogTitle),
                Tsv(row.CatalogAuthors),
                Tsv(row.Evidence),
                Tsv(row.MaterialText)
            })));

        await WriteAtomicallyAsync(outputPath, blocklistLines, cancellationToken);
        await WriteAtomicallyAsync(reportPath, reportLines, cancellationToken);

        return new BookBlocklistBuildResult(
            materials.Count,
            candidates.Length,
            exactCatalogIds.Count,
            blocked.Count,
            reportRows.Count(row => row.Status == "REVIEW"));
    }

    private static IReadOnlyList<RknMaterial> ReadMaterials(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"RKN source file not found: {sourcePath}", sourcePath);
        }

        return Path.GetExtension(sourcePath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsvMaterials(sourcePath)
            : ReadTextMaterials(sourcePath);
    }

    private static IReadOnlyList<RknMaterial> ReadCsvMaterials(string sourcePath)
    {
        var materials = new List<RknMaterial>();
        using var parser = new TextFieldParser(sourcePath, DetectEncoding(sourcePath))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(";");

        var firstRow = true;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length < 2)
            {
                continue;
            }

            if (firstRow)
            {
                firstRow = false;
                if (!int.TryParse(fields[0].TrimStart('\uFEFF'), out _))
                {
                    continue;
                }
            }

            if (int.TryParse(fields[0].TrimStart('\uFEFF'), out var id)
                && !string.IsNullOrWhiteSpace(fields[1]))
            {
                materials.Add(new RknMaterial(id, fields[1].Trim()));
            }
        }

        return materials;
    }

    private static IReadOnlyList<RknMaterial> ReadTextMaterials(string sourcePath)
    {
        var materials = new List<RknMaterial>();
        int? currentId = null;
        var currentText = new StringBuilder();

        void Flush()
        {
            if (currentId is not null && currentText.Length > 0)
            {
                materials.Add(new RknMaterial(currentId.Value, currentText.ToString().Trim()));
            }

            currentText.Clear();
        }

        foreach (var line in File.ReadLines(sourcePath, DetectEncoding(sourcePath)))
        {
            var match = NumberedLineRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups["id"].Value, out var id))
            {
                Flush();
                currentId = id;
                currentText.Append(match.Groups["text"].Value);
            }
            else if (currentId is not null && !string.IsNullOrWhiteSpace(line))
            {
                currentText.Append(' ').Append(line.Trim());
            }
        }

        Flush();
        return materials;
    }

    private static Encoding DetectEncoding(string path)
    {
        var prefix = new byte[Math.Min(16 * 1024, checked((int)Math.Min(new FileInfo(path).Length, int.MaxValue)))];
        using (var stream = File.OpenRead(path))
        {
            _ = stream.Read(prefix, 0, prefix.Length);
        }

        if (prefix.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (prefix.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode;
        }

        if (prefix.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(prefix);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251);
        }
    }

    private static IEnumerable<TitleCandidate> ExtractCandidates(RknMaterial material)
    {
        var normalizedMaterial = BookTextNormalizer.Normalize(material.Text);
        if (!LiteraryMarkers.Any(marker => normalizedMaterial.Contains(marker, StringComparison.Ordinal)))
        {
            yield break;
        }

        foreach (Match match in QuotedTextRegex().Matches(material.Text))
        {
            var title = match.Groups["title"].Value.Trim();
            var normalizedTitle = BookTextNormalizer.Normalize(title);
            if (normalizedTitle.Length < 4
                || normalizedTitle.Length > 300
                || normalizedTitle.Contains("http", StringComparison.Ordinal)
                || normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 35)
            {
                continue;
            }

            yield return new TitleCandidate(
                material.Id,
                material.Text,
                title,
                normalizedTitle,
                IsPrimaryTitle(material.Text, match.Index, match.Length));
        }
    }

    private static bool IsPrimaryTitle(string material, int quoteIndex, int quoteLength)
    {
        var segmentStart = material.LastIndexOf(';', Math.Max(quoteIndex - 1, 0));
        var localPrefix = BookTextNormalizer.Normalize(material[(segmentStart + 1)..quoteIndex]);
        if (MetadataMarkers.Any(marker => localPrefix.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        var suffix = material[(quoteIndex + quoteLength)..].TrimStart();
        if (suffix.Length > 0 && suffix[0] is '«' or '„' or '“' or '"')
        {
            return false;
        }

        var globalPrefix = BookTextNormalizer.Normalize(material[..quoteIndex]);
        return LiteraryMarkers.Any(marker => globalPrefix.Contains(marker, StringComparison.Ordinal));
    }

    private static string? FindAuthorEvidence(BookEntry book, string material)
    {
        var materialTokens = BookTextNormalizer.Normalize(material)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var author in book.Authors ?? [])
        {
            foreach (var token in BookTextNormalizer.Tokens(author.LastName))
            {
                var matchingMaterialToken = FindMatchingMaterialToken(token, materialTokens);
                if (matchingMaterialToken is not null)
                {
                    return token == matchingMaterialToken ? token : $"{token}~{matchingMaterialToken}";
                }
            }

            var fullNameMatches = BookTextNormalizer.Tokens(author.DisplayName)
                .Where(token => token.Length >= 4 && !IgnoredAuthorTokens.Contains(token))
                .Select(token => (Catalog: token, Material: FindMatchingMaterialToken(token, materialTokens)))
                .Where(match => match.Material is not null)
                .DistinctBy(match => match.Catalog, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (fullNameMatches.Length >= 2)
            {
                return string.Join(',', fullNameMatches.Select(match =>
                    match.Catalog == match.Material ? match.Catalog : $"{match.Catalog}~{match.Material}"));
            }
        }

        return null;
    }

    private static string? FindMatchingMaterialToken(string catalogToken, IReadOnlySet<string> materialTokens)
    {
        if (catalogToken.Length < 3 || IgnoredAuthorTokens.Contains(catalogToken))
        {
            return null;
        }

        if (materialTokens.Contains(catalogToken))
        {
            return catalogToken;
        }

        foreach (var materialToken in materialTokens)
        {
            if (materialToken.StartsWith(catalogToken, StringComparison.Ordinal)
                && NounCaseSuffixes.Contains(materialToken[catalogToken.Length..]))
            {
                return materialToken;
            }

            if (catalogToken.Length >= 6
                && catalogToken.EndsWith('и')
                && materialToken.StartsWith(catalogToken[..^2], StringComparison.Ordinal)
                && AdjectiveCaseSuffixes.Contains(materialToken[(catalogToken.Length - 2)..]))
            {
                return materialToken;
            }

            if (catalogToken.Length >= 5
                && catalogToken.EndsWith("ии", StringComparison.Ordinal)
                && materialToken.StartsWith(catalogToken[..^1], StringComparison.Ordinal)
                && NounCaseSuffixes.Contains(materialToken[(catalogToken.Length - 1)..]))
            {
                return materialToken;
            }
        }

        return null;
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        IReadOnlyCollection<string> lines,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Output path has no parent directory: {path}");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllLinesAsync(tempPath, lines, new UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string Tsv(string value) => OneLine(value).Replace('\t', ' ');

    private static string OneLine(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace('#', ' ')
        .Trim();

    [GeneratedRegex(@"^\s*(?<id>\d+)[\.)]\s*(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLineRegex();

    [GeneratedRegex("«(?<title>[^»\\r\\n]{2,400})»|„(?<title>[^“\\r\\n]{2,400})“|“(?<title>[^”\\r\\n]{2,400})”|\\\"(?<title>[^\\\"\\r\\n]{2,400})\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedTextRegex();

    private sealed record RknMaterial(int Id, string Text);

    private sealed record TitleCandidate(
        int MaterialId,
        string MaterialText,
        string Title,
        string NormalizedTitle,
        bool IsPrimaryTitle);

    private sealed record BlockedMatch(string BookId, int MaterialId, string Title, string Authors);

    private sealed record ReportRow(
        string Status,
        int MaterialId,
        string CandidateTitle,
        string BookId,
        string CatalogTitle,
        string CatalogAuthors,
        string Evidence,
        string MaterialText);
}
