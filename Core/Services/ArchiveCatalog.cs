using System.Text.RegularExpressions;

namespace booksBot.Core.Services;

public sealed partial class ArchiveCatalog
{
    private readonly string _archivesPath;
    private readonly object _sync = new();
    private IReadOnlyList<ArchiveRange> _ranges = [];

    public ArchiveCatalog(string archivesPath)
    {
        _archivesPath = archivesPath;
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            return _ranges.Count;
        }
    }

    public string FindArchive(int bookId)
    {
        EnsureLoaded();

        var match = _ranges.FirstOrDefault(range => bookId >= range.Start && bookId <= range.End);
        return match?.Path
            ?? throw new FileNotFoundException($"No ZIP or 7z archive found for book ID: {bookId}");
    }

    public void Refresh()
    {
        if (!Directory.Exists(_archivesPath))
        {
            throw new DirectoryNotFoundException($"Archives directory not found: {_archivesPath}");
        }

        var ranges = Directory
            .EnumerateFiles(_archivesPath)
            .Where(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            .Select(TryParse)
            .Where(range => range is not null)
            .Cast<ArchiveRange>()
            .OrderBy(range => range.FormatPriority)
            .ThenBy(range => range.Start)
            .ThenBy(range => range.End - range.Start)
            .ToArray();

        lock (_sync)
        {
            _ranges = ranges;
        }
    }

    public static ArchiveRange? TryParse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var match = RangeSuffixRegex().Match(name);
        if (!match.Success
            || !int.TryParse(match.Groups["start"].Value, out var start)
            || !int.TryParse(match.Groups["end"].Value, out var end)
            || start > end)
        {
            return null;
        }

        var priority = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return new ArchiveRange(start, end, path, priority);
    }

    private void EnsureLoaded()
    {
        if (_ranges.Count > 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_ranges.Count == 0)
            {
                Refresh();
            }
        }
    }

    [GeneratedRegex(@"(?<start>\d+)-(?<end>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RangeSuffixRegex();
}

public sealed record ArchiveRange(int Start, int End, string Path, int FormatPriority);
