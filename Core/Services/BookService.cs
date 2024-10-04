using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using booksBot.Infrastructure.Configuration;
using LiteDB;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;
using booksBot.Core.Services;

public class BookService : IBookService
{
    private readonly AppSettings _appSettings;
    private readonly string _dbPath;
    private readonly IOutputService _logger;

    public BookService(IOptions<AppSettings> appSettings, IOutputService logger)
    {
        _appSettings = appSettings.Value;
        _dbPath = _appSettings.LiteDbPath;
        _logger = logger;
    }

    // Метод для загрузки INPX коллекции и обновления базы данных
    public async Task LoadCollectionAsync()
    {
        try
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}; Connection=shared"))
            {
                var inpxFilePath = _appSettings.InpxCollectionPath;

                if (!File.Exists(inpxFilePath))
                {
                    throw new FileNotFoundException($"INPX file not found: {inpxFilePath}");
                }

                var collectionInfo = new FileInfo(inpxFilePath);
                var booksCollection = db.GetCollection<BookEntry>("books");
                var authorsCollection = db.GetCollection<AuthorEntry>("authors");

                // Проверка на обновление INPX файла
                var collectionMeta = db.GetCollection<CollectionMeta>("metadata").FindOne(x => x.Id == 1);
                if (collectionMeta != null && collectionMeta.Size == collectionInfo.Length)
                {
                    await _logger.LogMessageAsync("Коллекция уже загружена. Используем кэшированную базу данных.");
                    return;
                }

                await _logger.LogMessageAsync("Загрузка новой коллекции и индексация...");

                // Очистка текущих коллекций
                if (db.CollectionExists("books"))
                    db.DropCollection("books");

                if (db.CollectionExists("authors"))
                    db.DropCollection("authors");

                // Начинаем транзакцию
                db.BeginTrans();
                try
                {
                    // Хэшсет для отслеживания дубликатов LibId
                    var processedLibIds = new HashSet<string>();

                    using (var archive = ZipFile.OpenRead(inpxFilePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith(".inp", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var stream = entry.Open())
                                using (var reader = new StreamReader(stream))
                                {
                                    string line;
                                    while ((line = reader.ReadLine()) != null)
                                    {
                                        var parts = line.Split('');
                                        if (parts.Length >= 7)
                                        {
                                            var libId = parts[5].Trim().Trim('"');

                                            if (string.IsNullOrEmpty(libId))
                                            {
                                                // Пропускаем записи с пустым LibId
                                                continue;
                                            }

                                            // Проверка на дубликаты в текущей загрузке
                                            if (!processedLibIds.Add(libId))
                                            {
                                                // Дубликат обнаружен, пропускаем
                                                continue;
                                            }

                                            var bookEntry = new BookEntry
                                            {
                                                LibId = libId,
                                                Title = parts[2],
                                                TitleNormalized = parts[2].ToLowerInvariant(),
                                                Series = parts.Length > 3 ? parts[3] : null,
                                                Genre = parts[1],
                                                SeriesOrder = int.TryParse(parts.Length > 10 ? parts[10] : null, out var order) ? (int?)order : null,
                                                Language = parts.Length > 12 ? parts[12] : null
                                            };

                                            booksCollection.Insert(bookEntry);

                                            // Обработка авторов
                                            var authors = parts[0].Split(':');
                                            foreach (var authorStr in authors)
                                            {
                                                var authorParts = authorStr.Split(',');
                                                var authorEntry = new AuthorEntry
                                                {
                                                    BookLibId = libId,
                                                    LastName = authorParts.Length > 0 ? authorParts[0].Trim() : null,
                                                    FirstName = authorParts.Length > 1 ? authorParts[1].Trim() : null,
                                                    MiddleName = authorParts.Length > 2 ? authorParts[2].Trim() : null
                                                };

                                                authorsCollection.Insert(authorEntry);
                                            }
                                        }
                                        else
                                        {
                                            await _logger.LogMessageAsync($"Строка имеет недостаточно частей: {line}");
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Сохраняем метаинформацию о коллекции
                    if (collectionMeta == null)
                    {
                        collectionMeta = new CollectionMeta { Id = 1 };
                        db.GetCollection<CollectionMeta>("metadata").Insert(collectionMeta);
                    }
                    collectionMeta.Size = collectionInfo.Length;
                    db.GetCollection<CollectionMeta>("metadata").Update(collectionMeta);

                    // Фиксируем транзакцию
                    db.Commit();
                }
                catch (Exception ex)
                {
                    db.Rollback();
                    await _logger.LogMessageAsync($"Ошибка при загрузке коллекции: {ex.Message}");
                    throw;
                }

                // Создание индексов после вставки данных
                await _logger.LogMessageAsync("Создание индексов... (может занять время)");
                booksCollection.EnsureIndex(x => x.LibId, true);
                booksCollection.EnsureIndex(x => x.TitleNormalized);
                booksCollection.EnsureIndex(x => x.Series);
                authorsCollection.EnsureIndex(x => x.BookLibId);
                authorsCollection.EnsureIndex(x => x.LastName);
                authorsCollection.EnsureIndex(x => x.FirstName);
                authorsCollection.EnsureIndex(x => x.MiddleName);

                await _logger.LogMessageAsync($"Загрузка завершена. Книг в коллекции: {booksCollection.Count()}");
            }
        }
        catch (LiteException ex)
        {
            await _logger.LogMessageAsync($"LiteDB Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            await _logger.LogMessageAsync($"Unexpected Error: {ex.Message}");
        }
    }

    // Метод для поиска книг по автору
    public async Task<List<BookEntry>> SearchBooksByAuthorAsync(string authorName)
    {
        using (var db = new LiteDatabase($"Filename={_dbPath}; Connection=shared"))
        {
            var authorsCollection = db.GetCollection<AuthorEntry>("authors");
            var booksCollection = db.GetCollection<BookEntry>("books");

            var searchParts = authorName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(s => s.ToLowerInvariant())
                                        .ToArray();

            var potentialAuthors = authorsCollection.Find(author =>
                (author.FirstName != null && searchParts.Contains(author.FirstName.ToLowerInvariant())) ||
                (author.LastName != null && searchParts.Contains(author.LastName.ToLowerInvariant())) ||
                (author.MiddleName != null && searchParts.Contains(author.MiddleName.ToLowerInvariant()))
            ).ToList();

            var matchingAuthors = potentialAuthors.Where(author =>
            {
                var authorNames = new List<string>();
                if (!string.IsNullOrEmpty(author.FirstName)) authorNames.Add(author.FirstName.ToLowerInvariant());
                if (!string.IsNullOrEmpty(author.LastName)) authorNames.Add(author.LastName.ToLowerInvariant());
                if (!string.IsNullOrEmpty(author.MiddleName)) authorNames.Add(author.MiddleName.ToLowerInvariant());

                return searchParts.All(part => authorNames.Contains(part));
            }).ToList();

            var bookLibIds = matchingAuthors.Select(a => a.BookLibId).Distinct().ToList();
            var resultBooks = booksCollection.Find(b => bookLibIds.Contains(b.LibId)).ToList();

            foreach (var book in resultBooks)
            {
                var bookAuthors = authorsCollection.Find(a => a.BookLibId == book.LibId)
                    .Select(a => new AuthorPart
                    {
                        FirstName = a.FirstName,
                        LastName = a.LastName,
                        MiddleName = a.MiddleName
                    }).ToList();

                book.Authors = bookAuthors;
            }

            return resultBooks;
        }
    }


    // Метод для поиска книг по названию
    public async Task<List<BookEntry>> SearchBooksByTitleAsync(string title)
    {
        using (var db = new LiteDatabase($"Filename={_dbPath}; Connection=shared"))
        {
            var booksCollection = db.GetCollection<BookEntry>("books");
            var authorsCollection = db.GetCollection<AuthorEntry>("authors");

            // Разбиваем запрос пользователя на слова и приводим к нижнему регистру
            var searchWords = title.ToLowerInvariant().Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // Проверяем, есть ли слова для поиска
            if (searchWords.Length == 0)
            {
                return new List<BookEntry>();
            }

            // Создаём список условий для каждого слова
            var expressionParts = new List<string>();

            foreach (var word in searchWords)
            {
                // Создаём строку выражения для нечувствительного к регистру поиска
                var escapedWord = word.Replace("\"", "\\\""); // Экранируем кавычки
                expressionParts.Add($"LOWER($.TitleNormalized) LIKE \"%{escapedWord}%\"");
            }

            // Объединяем условия с помощью оператора AND
            var combinedExpression = string.Join(" AND ", expressionParts);

            // Создаём финальное выражение
            var expr = BsonExpression.Create(combinedExpression);

            // Получаем потенциальные книги
            var potentialBooks = booksCollection.Find(expr).ToList();

            // Фильтруем книги в памяти, чтобы убедиться, что все поисковые слова являются отдельными словами в названии
            var matchingBooks = potentialBooks.Where(b =>
            {
                var titleWords = b.TitleNormalized.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '—', '«', '»', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
                return searchWords.All(word => titleWords.Contains(word));
            }).ToList();

            // Заполняем авторов для каждой книги
            foreach (var book in matchingBooks)
            {
                var bookAuthors = authorsCollection.Find(a => a.BookLibId == book.LibId)
                    .Select(a => new AuthorPart
                    {
                        FirstName = a.FirstName,
                        LastName = a.LastName,
                        MiddleName = a.MiddleName
                    }).ToList();

                book.Authors = bookAuthors;
            }

            return matchingBooks;
        }
    }

    public async Task<List<BookEntry>> SearchBooksBySeriesAsync(string seriesName)
    {
        using (var db = new LiteDatabase($"Filename={_dbPath}; Connection=shared"))
        {
            var booksCollection = db.GetCollection<BookEntry>("books");
            var authorsCollection = db.GetCollection<AuthorEntry>("authors");

            // Приводим запрос пользователя к нижнему регистру и удаляем лишние пробелы
            var searchQuery = seriesName.ToLowerInvariant().Trim();

            // Находим книги, серия которых соответствует запросу
            var matchingBooks = booksCollection.Find(b =>
                b.Series != null && b.Series.ToLowerInvariant().Contains(searchQuery)).ToList();

            // Заполняем авторов для каждой книги
            foreach (var book in matchingBooks)
            {
                var bookAuthors = authorsCollection.Find(a => a.BookLibId == book.LibId)
                    .Select(a => new AuthorPart
                    {
                        FirstName = a.FirstName,
                        LastName = a.LastName,
                        MiddleName = a.MiddleName
                    }).ToList();

                book.Authors = bookAuthors;
            }

            return matchingBooks;
        }
    }


    // Метод для получения FB2-файла книги
    public async Task<byte[]> GetBookFileAsync(string bookId)
    {
        var archivesPath = _appSettings.ArchivesPath;

        // Определяем архив, в котором должна находиться книга
        var zipFileName = GetZipFileNameForBook(bookId);
        var zipFilePath = Path.Combine(archivesPath, zipFileName);

        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException($"Archive file not found: {zipFilePath}");
        }

        // Извлекаем FB2 файл из архива
        using (var archive = ZipFile.OpenRead(zipFilePath))
        {
            var entry = archive.GetEntry($"{bookId}.fb2");
            if (entry == null)
            {
                throw new FileNotFoundException($"FB2 file not found in archive: {bookId}.fb2 in {zipFileName}");
            }

            using (var stream = entry.Open())
            using (var memoryStream = new MemoryStream())
            {
                stream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
    }

    // Определение имени архива, в котором находится книга
    private string GetZipFileNameForBook(string bookId)
    {
        var bookIdNum = int.Parse(bookId);
        var zipFiles = Directory.GetFiles(_appSettings.ArchivesPath, "*.zip");

        foreach (var zipFile in zipFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(zipFile);
            var rangeParts = fileName.Split('-');
            if (rangeParts.Length >= 3 && int.TryParse(rangeParts[1], out int rangeStart) && int.TryParse(rangeParts[2], out int rangeEnd))
            {
                if (bookIdNum >= rangeStart && bookIdNum <= rangeEnd)
                {
                    return Path.GetFileName(zipFile);
                }
            }
        }

        throw new FileNotFoundException($"No archive found for book ID: {bookId}");
    }
}
