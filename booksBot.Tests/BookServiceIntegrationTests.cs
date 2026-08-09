using System.IO.Compression;
using System.Text;
using booksBot.Core.Models;
using booksBot.Core.Services;
using booksBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace booksBot.Tests;

public sealed class BookServiceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"boxbot-service-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task IndexSearchDownloadAndTelegramCache_WorkTogether()
    {
        Directory.CreateDirectory(_root);
        var inpxPath = Path.Combine(_root, "collection.inpx");
        var archivePath = Path.Combine(_root, "fb2-806000-806999.zip");
        CreateInpx(inpxPath);
        CreateBookArchive(archivePath);

        var settings = Options.Create(new AppSettings
        {
            BotToken = "test-token",
            InpxCollectionPath = inpxPath,
            ArchivesPath = _root,
            LiteDbPath = Path.Combine(_root, "books.db"),
            StateDbPath = Path.Combine(_root, "state.db"),
            TempPath = Path.Combine(_root, "temp")
        });
        var service = new BookService(settings, NullLogger<BookService>.Instance);

        await service.LoadCollectionAsync();

        var byAuthor = await service.SearchAsync("Маркус Касс", BookSearchField.Author);
        Assert.Equal(2, byAuthor.Books.Count);
        Assert.All(byAuthor.Books, book => Assert.Equal("Касс Маркус", book.Authors[0].DisplayName));

        var universal = await service.SearchAsync("Касс Империя");
        var empire = Assert.Single(universal.Books);
        Assert.Equal("806581", empire.LibId);
        Assert.Equal("Святоша", empire.Series);

        var missingSeries = await service.SearchAsync("Тестов", BookSearchField.Author);
        Assert.Equal("806583", Assert.Single(missingSeries.Books).LibId);

        var rankedAuthor = await service.SearchAsync("Аркадий Стругацкий");
        Assert.Equal(new[] { "806585", "806587", "806584", "806586" }, rankedAuthor.Books.Select(book => book.LibId));

        await using (var download = await service.PrepareBookFileAsync("806581"))
        await using (var stream = download.OpenRead())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var content = await reader.ReadToEndAsync();
            Assert.Contains("Империя храмов", content);
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "temp"), "*.fb2"));

        var preview = await service.GetBookPreviewAsync("806581");
        Assert.Equal("Первая строка.\n\nВторая строка.", preview.Annotation);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, preview.CoverBytes);
        Assert.Equal("image/jpeg", preview.CoverContentType);

        await service.SaveBookPreviewAsync("806581", preview.Annotation, true, "telegram-cover-id");
        var cachedPreview = await service.GetBookPreviewAsync("806581");
        Assert.Equal("telegram-cover-id", cachedPreview.TelegramCoverFileId);
        Assert.Null(cachedPreview.CoverBytes);

        await service.SaveTelegramFileIdAsync("806581", "telegram-file-id");
        Assert.Equal("telegram-file-id", await service.GetTelegramFileIdAsync("806581"));

        await service.LoadCollectionAsync();
        Assert.Equal("Империя храмов", (await service.GetBookAsync("806581"))?.Title);
    }

    [Fact]
    public async Task ExactIdBlocklist_HidesOnlyTheSelectedBookAcrossEveryReadPath()
    {
        Directory.CreateDirectory(_root);
        var inpxPath = Path.Combine(_root, "collection.inpx");
        var archivePath = Path.Combine(_root, "fb2-806000-806999.zip");
        var blocklistPath = Path.Combine(_root, "blocked-book-ids.txt");
        CreateInpx(inpxPath);
        CreateBookArchive(archivePath);
        await File.WriteAllLinesAsync(blocklistPath, ["806581 # exact ID only"]);

        var service = CreateService(inpxPath, blocklistPath);
        await service.LoadCollectionAsync();

        Assert.Empty((await service.SearchAsync("Империя храмов")).Books);
        Assert.Equal("806582", Assert.Single((await service.SearchAsync("Путь защитника")).Books).LibId);
        Assert.Null(await service.GetBookAsync("806581"));
        Assert.NotNull(await service.GetBookAsync("806582"));
        Assert.Null(await service.GetTelegramFileIdAsync("806581"));
        await Assert.ThrowsAsync<BookUnavailableException>(() => service.PrepareBookFileAsync("806581"));
        await Assert.ThrowsAsync<BookUnavailableException>(() => service.GetBookPreviewAsync("806581"));

        for (var attempt = 0; attempt < 30; attempt++)
        {
            Assert.NotEqual("806581", (await service.GetRandomBookAsync())?.LibId);
        }
    }

    [Fact]
    public async Task BlocklistBuilder_BlocksOnlyExactTitleWithExplicitCatalogAuthor()
    {
        Directory.CreateDirectory(_root);
        var inpxPath = Path.Combine(_root, "collection.inpx");
        var archivePath = Path.Combine(_root, "fb2-806000-806999.zip");
        var sourcePath = Path.Combine(_root, "rkn.csv");
        var outputPath = Path.Combine(_root, "blocked-book-ids.txt");
        var reportPath = Path.Combine(_root, "blocked-book-report.tsv");
        CreateInpx(inpxPath);
        CreateBookArchive(archivePath);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        await File.WriteAllTextAsync(
            sourcePath,
            "#;Материал;Дата\n"
            + "10;\"Книга \"\"Империя храмов\"\", автор - Касса Маркуса, решение суда;\";\n"
            + "11;\"Книга \"\"Путь защитника\"\", автор - Совсем Другой, решение суда;\";\n"
            + "12;\"Книга \"\"Несуществующий заголовок\"\", автор - Касс Маркус;\";\n"
            + "13;\"Книги Касса Маркуса: \"\"Несуществующий заголовок\"\"; \"\"Путь защитника\"\";\";\n",
            Encoding.GetEncoding(1251));

        var service = CreateService(inpxPath, outputPath);
        await service.LoadCollectionAsync();
        var result = await service.BuildBlocklistAsync(sourcePath, outputPath, reportPath);

        Assert.Equal(4, result.SourceMaterialCount);
        Assert.Equal(2, result.ExactCatalogMatchCount);
        Assert.Equal(2, result.BlockedBookCount);
        Assert.Equal(1, result.ReviewMatchCount);
        Assert.Contains(File.ReadAllLines(outputPath), line => line.StartsWith("806581 #", StringComparison.Ordinal));
        Assert.Contains(File.ReadAllLines(outputPath), line => line.StartsWith("806582 #", StringComparison.Ordinal));
        Assert.Contains("REVIEW\t11\tПуть защитника\t806582", await File.ReadAllTextAsync(reportPath));
    }

    private BookService CreateService(string inpxPath, string blocklistPath)
    {
        var settings = Options.Create(new AppSettings
        {
            BotToken = "test-token",
            InpxCollectionPath = inpxPath,
            ArchivesPath = _root,
            LiteDbPath = Path.Combine(_root, "books.db"),
            StateDbPath = Path.Combine(_root, "state.db"),
            TempPath = Path.Combine(_root, "temp"),
            BlockedBookIdsPath = blocklistPath
        });
        return new BookService(settings, NullLogger<BookService>.Instance);
    }

    private static void CreateInpx(string path)
    {
        var records = new[]
        {
            CreateRecord("Касс,Маркус", "Империя храмов", "Святоша", "806581", "1"),
            CreateRecord("Касс,Маркус", "Путь защитника", "Святоша", "806582", "2"),
            CreateRecord("Тестов,Оченьдлинный", new string('А', 2_000), string.Empty, "806583", string.Empty),
            CreateRecord("Бережной,Сергей", "Аркадий Стругацкий — инструкция", string.Empty, "806584", string.Empty),
            CreateRecord("Стругацкий,Аркадий,Натанович", "Пикник на обочине", string.Empty, "806585", string.Empty),
            CreateRecord("Стругацкий,Борис:Шушпанов,Аркадий", "Журнал Если", string.Empty, "806586", string.Empty),
            CreateRecord("Стругацкий,Аркадий,Натанович", "A Foreign Edition", string.Empty, "806587", string.Empty)
        };

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("books.inp");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var record in records)
        {
            writer.WriteLine(record);
        }
    }

    private static string CreateRecord(
        string author,
        string title,
        string series,
        string id,
        string order,
        string language = "ru")
    {
        var parts = new string[13];
        parts[0] = author;
        parts[1] = "sf_fantasy";
        parts[2] = title;
        parts[3] = series;
        parts[5] = id;
        parts[10] = order;
        parts[12] = language;
        return string.Join('\u0004', parts);
    }

    private static void CreateBookArchive(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteBook(archive, "806581", "Империя храмов");
        WriteBook(archive, "806582", "Путь защитника");
        WriteBook(archive, "806583", "Очень длинное название");
    }

    private static void WriteBook(ZipArchive archive, string id, string title)
    {
        var entry = archive.CreateEntry($"{id}.fb2");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (id == "806581")
        {
            writer.Write($"""
                <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
                  <description>
                    <title-info>
                      <book-title>{title}</book-title>
                      <annotation><p>Первая строка.</p><p>Вторая строка.</p></annotation>
                      <coverpage><image l:href="#cover.jpg" /></coverpage>
                    </title-info>
                  </description>
                  <binary id="cover.jpg" content-type="image/jpeg">AQIDBA==</binary>
                </FictionBook>
                """);
            return;
        }

        writer.Write($"<FictionBook><description><title-info><book-title>{title}</book-title></title-info></description></FictionBook>");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
