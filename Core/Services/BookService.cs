using System.IO.Compression;
using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using booksBot.Infrastructure.Configuration;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;

namespace booksBot.Core.Services;

public sealed class BookService : IBookService
{
    private const int CollectionSchemaVersion = 4;
    private const int BatchSize = 5_000;
    private const int CandidateLimit = 2_000;
    private const int MaxIndexedTokenLength = 128;

    private readonly AppSettings _settings;
    private readonly ILogger<BookService> _logger;
    private readonly ArchiveCatalog _archiveCatalog;
    private readonly string _databasePath;
    private readonly string _stateDatabasePath;
    private readonly string _tempPath;

    public BookService(IOptions<AppSettings> settings, ILogger<BookService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _databasePath = _settings.LiteDbPath;

        var applicationDirectory = Path.GetDirectoryName(_databasePath) ?? AppContext.BaseDirectory;
        _stateDatabasePath = string.IsNullOrWhiteSpace(_settings.StateDbPath)
            ? Path.Combine(applicationDirectory, "boxbot-state.db")
            : _settings.StateDbPath;
        _tempPath = string.IsNullOrWhiteSpace(_settings.TempPath)
            ? Path.Combine(applicationDirectory, "temp")
            : _settings.TempPath;

        _archiveCatalog = new ArchiveCatalog(_settings.ArchivesPath);
    }

    public async Task LoadCollectionAsync(CancellationToken cancellationToken = default)
    {
        var sourcePath = _settings.InpxCollectionPath;
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"INPX file not found: {sourcePath}");
        }

        if (!Directory.Exists(_settings.ArchivesPath))
        {
            throw new DirectoryNotFoundException($"Archives directory not found: {_settings.ArchivesPath}");
        }

        EnsureParentDirectory(_databasePath);
        EnsureParentDirectory(_stateDatabasePath);
        Directory.CreateDirectory(_tempPath);
        CleanupOldTempFiles();

        var sourceInfo = new FileInfo(sourcePath);
        if (IsCurrentCollection(sourceInfo))
        {
            _archiveCatalog.Refresh();
            _logger.LogInformation(
                "Book collection is current. Loaded {ArchiveCount} ZIP/7z archive ranges.",
                _archiveCatalog.Count);
            return;
        }

        var rebuildPath = $"{_databasePath}.rebuild";
        TryDelete(rebuildPath);
        TryDelete($"{rebuildPath}-log");

        _logger.LogInformation("Building a new book index from {InpxPath}.", sourcePath);
        await BuildCollectionAsync(sourceInfo, rebuildPath, cancellationToken);
        ReplaceDatabase(rebuildPath);

        _archiveCatalog.Refresh();
        _logger.LogInformation(
            "Book index is ready. Loaded {ArchiveCount} ZIP/7z archive ranges.",
            _archiveCatalog.Count);
    }

    public Task<BookSearchResult> SearchAsync(
        string query,
        BookSearchField field = BookSearchField.All,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = BookTextNormalizer.Normalize(query);
        var tokens = BookTextNormalizer.Tokens(query);
        if (tokens.Length == 0)
        {
            return Task.FromResult(new BookSearchResult(query, field, [], 0, false));
        }

        limit = Math.Clamp(limit, 1, 500);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var database = OpenCollectionDatabase();
            var books = database.GetCollection<BookEntry>("books");
            var anchorToken = tokens.OrderByDescending(token => token.Length).First();
            var indexedAnchor = ToIndexedToken(anchorToken);
            var tokenIndex = GetTokenIndexName(field);

            var candidates = books.Find(
                    Query.EQ(tokenIndex, indexedAnchor),
                    skip: 0,
                    limit: CandidateLimit + 1)
                .ToList();

            // Preserve partial-word search for short/incomplete user input. Exact token
            // queries take the indexed path; this slower fallback is only needed when
            // the user has not typed a complete indexed word yet.
            if (candidates.Count == 0)
            {
                candidates = books
                    .Find(
                        Query.Contains(GetNormalizedFieldName(field), anchorToken),
                        skip: 0,
                        limit: CandidateLimit + 1)
                    .ToList();
            }

            var candidateSetWasTruncated = candidates.Count > CandidateLimit;
            if (candidateSetWasTruncated)
            {
                candidates.RemoveAt(candidates.Count - 1);
            }

            var preferRussian = ContainsCyrillic(normalizedQuery);
            var matches = candidates
                .Where(book => tokens.All(token => GetNormalizedField(book, field).Contains(token, StringComparison.Ordinal)))
                .OrderByDescending(book => Score(book, normalizedQuery, field))
                .ThenByDescending(book => !preferRussian
                    || string.Equals(book.Language, "ru", StringComparison.OrdinalIgnoreCase))
                .ThenBy(book => book.TitleNormalized, StringComparer.Ordinal)
                .ToList();

            var visible = matches.Take(limit).ToArray();
            var isTruncated = candidateSetWasTruncated || matches.Count > limit;
            return new BookSearchResult(query.Trim(), field, visible, matches.Count, isTruncated);
        }, cancellationToken);
    }

    public Task<BookEntry?> GetBookAsync(string bookId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var database = OpenCollectionDatabase();
        return (BookEntry?)database.GetCollection<BookEntry>("books").FindById(bookId);
    }, cancellationToken);

    public Task<BookEntry?> GetRandomBookAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var database = OpenCollectionDatabase();
        var books = database.GetCollection<BookEntry>("books");
        var count = books.Count();
        if (count == 0)
        {
            return null;
        }

        return books.Find(Query.All(), Random.Shared.Next(count), 1).FirstOrDefault();
    }, cancellationToken);

    public async Task<BookDownload> PrepareBookFileAsync(
        string bookId,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(bookId, out var numericBookId))
        {
            throw new ArgumentException($"Invalid book ID: {bookId}", nameof(bookId));
        }

        var archivePath = _archiveCatalog.FindArchive(numericBookId);
        var book = await GetBookAsync(bookId, cancellationToken);
        var fileName = BuildDownloadFileName(book, bookId);
        var tempFilePath = Path.Combine(_tempPath, $"{bookId}-{Guid.NewGuid():N}.fb2");

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entry = archive.Entries.FirstOrDefault(candidate =>
                !candidate.IsDirectory
                && string.Equals(Path.GetFileName(candidate.Key), $"{bookId}.fb2", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                throw new FileNotFoundException(
                    $"FB2 file not found in archive: {bookId}.fb2 in {Path.GetFileName(archivePath)}");
            }

            await using var input = entry.OpenEntryStream();
            await using var output = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);

            return new BookDownload(tempFilePath, fileName);
        }
        catch
        {
            TryDelete(tempFilePath);
            throw;
        }
    }

    public async Task<BookPreview> GetBookPreviewAsync(
        string bookId,
        CancellationToken cancellationToken = default)
    {
        BookPreviewCacheEntry? cached;
        using (var database = OpenStateDatabase())
        {
            cached = database.GetCollection<BookPreviewCacheEntry>("book_previews").FindById(bookId);
        }

        if (cached is not null && (!cached.HasCover || !string.IsNullOrWhiteSpace(cached.TelegramCoverFileId)))
        {
            return new BookPreview(
                cached.Annotation ?? string.Empty,
                cached.TelegramCoverFileId,
                null,
                null);
        }

        await using var download = await PrepareBookFileAsync(bookId, cancellationToken);
        await using var stream = download.OpenRead();
        var preview = Fb2PreviewParser.Parse(stream);
        if (!preview.HasCover)
        {
            await SaveBookPreviewAsync(
                bookId,
                preview.Annotation,
                hasCover: false,
                telegramCoverFileId: string.Empty,
                cancellationToken);
        }

        return preview;
    }

    public Task SaveBookPreviewAsync(
        string bookId,
        string annotation,
        bool hasCover,
        string telegramCoverFileId,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var database = OpenStateDatabase();
        database.GetCollection<BookPreviewCacheEntry>("book_previews").Upsert(new BookPreviewCacheEntry
        {
            BookId = bookId,
            Annotation = annotation,
            HasCover = hasCover,
            TelegramCoverFileId = telegramCoverFileId,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }, cancellationToken);

    public Task<string?> GetTelegramFileIdAsync(string bookId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var database = OpenStateDatabase();
        return database.GetCollection<TelegramFileCacheEntry>("telegram_files").FindById(bookId)?.FileId;
    }, cancellationToken);

    public Task SaveTelegramFileIdAsync(
        string bookId,
        string fileId,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var database = OpenStateDatabase();
        database.GetCollection<TelegramFileCacheEntry>("telegram_files").Upsert(new TelegramFileCacheEntry
        {
            BookId = bookId,
            FileId = fileId,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }, cancellationToken);

    private bool IsCurrentCollection(FileInfo sourceInfo)
    {
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        try
        {
            using var database = OpenCollectionDatabase();
            var metadata = database.GetCollection<CollectionMeta>("metadata").FindById(1);
            return metadata is not null
                && metadata.SchemaVersion == CollectionSchemaVersion
                && metadata.SourceSize == sourceInfo.Length
                && metadata.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks
                && metadata.BookCount > 0;
        }
        catch (Exception exception) when (exception is LiteException or IOException)
        {
            _logger.LogWarning(exception, "Existing book index is incompatible and will be rebuilt.");
            return false;
        }
    }

    private async Task BuildCollectionAsync(
        FileInfo sourceInfo,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var processedBookIds = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<BookEntry>(BatchSize);
        var bookCount = 0;

        using var database = new LiteDatabase($"Filename={targetPath};Connection=direct");
        var books = database.GetCollection<BookEntry>("books");

        using var inpxArchive = ZipFile.OpenRead(sourceInfo.FullName);
        foreach (var entry in inpxArchive.Entries.Where(entry => entry.FullName.EndsWith(".inp", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var book = ParseBook(line);
                if (book is null || !processedBookIds.Add(book.LibId))
                {
                    continue;
                }

                batch.Add(book);
                if (batch.Count < BatchSize)
                {
                    continue;
                }

                books.InsertBulk(batch);
                bookCount += batch.Count;
                batch.Clear();

                if (bookCount % 100_000 == 0)
                {
                    _logger.LogInformation("Indexed {BookCount:N0} books.", bookCount);
                }
            }
        }

        if (batch.Count > 0)
        {
            books.InsertBulk(batch);
            bookCount += batch.Count;
        }

        books.EnsureIndex("title_tokens", BsonExpression.Create("$.TitleTokens[*]"));
        books.EnsureIndex("author_tokens", BsonExpression.Create("$.AuthorTokens[*]"));
        books.EnsureIndex("series_tokens", BsonExpression.Create("$.SeriesTokens[*]"));
        books.EnsureIndex("search_tokens", BsonExpression.Create("$.SearchTokens[*]"));

        database.GetCollection<CollectionMeta>("metadata").Upsert(new CollectionMeta
        {
            Id = 1,
            SchemaVersion = CollectionSchemaVersion,
            SourceSize = sourceInfo.Length,
            SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
            BookCount = bookCount
        });

        database.Checkpoint();
        _logger.LogInformation("Built a new index containing {BookCount:N0} books.", bookCount);
    }

    private static BookEntry? ParseBook(string line)
    {
        var parts = line.Split('\u0004');
        if (parts.Length < 7)
        {
            return null;
        }

        var bookId = parts[5].Trim().Trim('"');
        var title = parts[2].Trim();
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var authors = parts[0]
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseAuthor)
            .Where(author => !string.IsNullOrWhiteSpace(author.DisplayName))
            .ToList();
        var series = parts.Length > 3 ? parts[3].Trim() : string.Empty;
        var titleNormalized = BookTextNormalizer.Normalize(title);
        var authorsNormalized = BookTextNormalizer.Normalize(string.Join(' ', authors.Select(author => author.DisplayName)));
        var seriesNormalized = BookTextNormalizer.Normalize(series);
        var titleTokens = CreateIndexTokens(titleNormalized);
        var authorTokens = CreateIndexTokens(authorsNormalized);
        var seriesTokens = CreateIndexTokens(seriesNormalized);

        return new BookEntry
        {
            LibId = bookId,
            Title = title,
            TitleNormalized = titleNormalized,
            Series = series,
            SeriesNormalized = seriesNormalized,
            Genre = parts[1].Trim(),
            SeriesOrder = int.TryParse(parts.Length > 10 ? parts[10] : null, out var order) ? order : null,
            Language = parts.Length > 12 ? parts[12].Trim() : string.Empty,
            Authors = authors,
            AuthorsNormalized = authorsNormalized,
            SearchTextNormalized = string.Join(' ', new[] { titleNormalized, authorsNormalized, seriesNormalized }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            TitleTokens = titleTokens,
            AuthorTokens = authorTokens,
            SeriesTokens = seriesTokens,
            SearchTokens = titleTokens
                .Concat(authorTokens)
                .Concat(seriesTokens)
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private static AuthorPart ParseAuthor(string value)
    {
        var parts = value.Split(',');
        return new AuthorPart
        {
            LastName = parts.ElementAtOrDefault(0)?.Trim() ?? string.Empty,
            FirstName = parts.ElementAtOrDefault(1)?.Trim() ?? string.Empty,
            MiddleName = parts.ElementAtOrDefault(2)?.Trim() ?? string.Empty
        };
    }

    private void ReplaceDatabase(string rebuildPath)
    {
        var backupPath = $"{_databasePath}.v1-backup";
        TryDelete(backupPath);

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Replace(rebuildPath, _databasePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(rebuildPath, _databasePath);
            }

            using var verificationDatabase = OpenCollectionDatabase();
            var metadata = verificationDatabase.GetCollection<CollectionMeta>("metadata").FindById(1)
                ?? throw new InvalidDataException("Rebuilt database metadata is missing.");
            if (metadata.BookCount <= 0)
            {
                throw new InvalidDataException("Rebuilt database is empty.");
            }

            TryDelete(backupPath);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                TryDelete(_databasePath);
                File.Move(backupPath, _databasePath);
            }

            throw;
        }
        finally
        {
            TryDelete(rebuildPath);
            TryDelete($"{rebuildPath}-log");
        }
    }

    private LiteDatabase OpenCollectionDatabase() => new($"Filename={_databasePath};Connection=shared");

    private LiteDatabase OpenStateDatabase() => new($"Filename={_stateDatabasePath};Connection=shared");

    private static string GetNormalizedFieldName(BookSearchField field) => field switch
    {
        BookSearchField.Title => nameof(BookEntry.TitleNormalized),
        BookSearchField.Author => nameof(BookEntry.AuthorsNormalized),
        BookSearchField.Series => nameof(BookEntry.SeriesNormalized),
        _ => nameof(BookEntry.SearchTextNormalized)
    };

    private static string GetTokenIndexName(BookSearchField field) => field switch
    {
        BookSearchField.Title => "title_tokens",
        BookSearchField.Author => "author_tokens",
        BookSearchField.Series => "series_tokens",
        _ => "search_tokens"
    };

    private static List<string> CreateIndexTokens(string normalizedValue) => normalizedValue
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ToIndexedToken)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private static string ToIndexedToken(string token) => token.Length <= MaxIndexedTokenLength
        ? token
        : token[..MaxIndexedTokenLength];

    private static string GetNormalizedField(BookEntry book, BookSearchField field) => field switch
    {
        BookSearchField.Title => book.TitleNormalized ?? string.Empty,
        BookSearchField.Author => book.AuthorsNormalized ?? string.Empty,
        BookSearchField.Series => book.SeriesNormalized ?? string.Empty,
        _ => book.SearchTextNormalized ?? string.Empty
    };

    private static int Score(BookEntry book, string normalizedQuery, BookSearchField field)
    {
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var titleScore = MatchScore(book.TitleNormalized, normalizedQuery, tokens, exact: 240, startsWith: 130, contains: 80);
        var combinedAuthorScore = MatchScore(
            book.AuthorsNormalized,
            normalizedQuery,
            tokens,
            exact: 220,
            startsWith: 190,
            contains: tokens.Length == 1 ? 170 : 90);
        var individualAuthorScore = (book.Authors ?? [])
            .Select(author => BookTextNormalizer.Normalize(author.DisplayName))
            .Select(author => MatchScore(author, normalizedQuery, tokens, exact: 230, startsWith: 215, contains: 205))
            .DefaultIfEmpty(0)
            .Max();
        if (individualAuthorScore > 0)
        {
            individualAuthorScore -= Math.Min(Math.Max((book.Authors?.Count ?? 1) - 1, 0) * 8, 80);
        }

        var authorScore = Math.Max(combinedAuthorScore, individualAuthorScore);
        var seriesScore = MatchScore(book.SeriesNormalized, normalizedQuery, tokens, exact: 200, startsWith: 110, contains: 70);

        return field switch
        {
            BookSearchField.Title => titleScore,
            BookSearchField.Author => authorScore,
            BookSearchField.Series => seriesScore,
            _ => Math.Max(titleScore, Math.Max(authorScore, seriesScore))
        };
    }

    private static int MatchScore(
        string? value,
        string normalizedQuery,
        IReadOnlyList<string> tokens,
        int exact,
        int startsWith,
        int contains)
    {
        value ??= string.Empty;
        if (value.Equals(normalizedQuery, StringComparison.Ordinal))
        {
            return exact;
        }

        if (value.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return startsWith;
        }

        return tokens.All(token => value.Contains(token, StringComparison.Ordinal)) ? contains : 0;
    }

    private static bool ContainsCyrillic(string value) => value.Any(character =>
        character is >= '\u0400' and <= '\u04FF');

    private static string BuildDownloadFileName(BookEntry? book, string bookId)
    {
        var author = book?.Authors?.FirstOrDefault()?.DisplayName;
        var displayName = string.Join(" — ", new[] { author, book?.Title }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = bookId;
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            displayName = displayName.Replace(invalidCharacter, '_');
        }

        displayName = displayName.Length > 120 ? displayName[..120].Trim() : displayName;
        return $"{displayName} [{bookId}].fb2";
    }

    private void CleanupOldTempFiles()
    {
        var threshold = DateTime.UtcNow.AddDays(-1);
        foreach (var path in Directory.EnumerateFiles(_tempPath, "*.fb2"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    File.Delete(path);
                }
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Could not clean temporary file {TempFile}.", path);
            }
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the original error is more useful to the caller.
        }
    }
}
