using booksBot.Application.TelegramBot;
using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using booksBot.Core.Services;
using booksBot.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace booksBot;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var probeBookId = GetOptionValue(args, "--probe-book");
        var probeBlockedId = GetOptionValue(args, "--probe-blocked");
        var probeQuery = GetOptionValue(args, "--probe-query");
        var probePreviewId = GetOptionValue(args, "--probe-preview");
        var blocklistSource = GetOptionValue(args, "--build-blocklist");
        var maintenanceMode = args.Contains("--index-only", StringComparer.OrdinalIgnoreCase)
            || probeBookId is not null
            || probeBlockedId is not null
            || probeQuery is not null
            || probePreviewId is not null
            || blocklistSource is not null;

        var builder = Host.CreateApplicationBuilder(args);

        var options = builder.Services
            .AddOptions<AppSettings>()
            .Bind(builder.Configuration.GetSection(AppSettings.SectionName))
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.InpxCollectionPath), "INPX path is required")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.ArchivesPath), "Archives path is required")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.LiteDbPath), "LiteDB path is required")
            .ValidateOnStart();

        if (!maintenanceMode)
        {
            options.Validate(settings => !string.IsNullOrWhiteSpace(settings.BotToken), "Bot token is required");
        }

        builder.Services.AddSingleton<IBookService, BookService>();

        if (maintenanceMode)
        {
            using var maintenanceHost = builder.Build();
            var books = maintenanceHost.Services.GetRequiredService<IBookService>();
            var settings = maintenanceHost.Services.GetRequiredService<IOptions<AppSettings>>().Value;
            await books.LoadCollectionAsync();

            if (blocklistSource is not null)
            {
                var defaultDirectory = Path.GetDirectoryName(settings.LiteDbPath) ?? AppContext.BaseDirectory;
                var outputPath = GetOptionValue(args, "--blocklist-output")
                    ?? settings.BlockedBookIdsPath
                    ?? Path.Combine(defaultDirectory, "blocked-book-ids.txt");
                var reportPath = GetOptionValue(args, "--blocklist-report")
                    ?? Path.Combine(defaultDirectory, "blocked-book-report.tsv");
                var result = await books.BuildBlocklistAsync(blocklistSource, outputPath, reportPath);
                Console.WriteLine(
                    $"BLOCKLIST_OK materials={result.SourceMaterialCount} candidates={result.CandidateTitleCount} "
                    + $"catalog_matches={result.ExactCatalogMatchCount} blocked={result.BlockedBookCount} "
                    + $"review={result.ReviewMatchCount} output={outputPath} report={reportPath}");
            }

            if (probeQuery is not null)
            {
                var result = await books.SearchAsync(probeQuery);
                Console.WriteLine($"SEARCH_OK query={probeQuery} matches={result.MatchedCount} returned={result.Books.Count}");
                foreach (var hit in result.Books.Take(5))
                {
                    Console.WriteLine($"SEARCH_HIT id={hit.LibId} title={hit.Title}");
                }
            }

            if (probeBookId is not null)
            {
                var book = await books.GetBookAsync(probeBookId)
                    ?? throw new InvalidOperationException($"Book {probeBookId} is missing from the index.");
                await using var download = await books.PrepareBookFileAsync(probeBookId);
                Console.WriteLine($"BOOK_OK id={book.LibId} bytes={download.Length} title={book.Title}");
            }

            if (probeBlockedId is not null)
            {
                if (await books.GetBookAsync(probeBlockedId) is not null)
                {
                    throw new InvalidOperationException($"Book {probeBlockedId} is still visible despite the blocklist.");
                }

                try
                {
                    await using var _ = await books.PrepareBookFileAsync(probeBlockedId);
                    throw new InvalidOperationException($"Book {probeBlockedId} can still be prepared despite the blocklist.");
                }
                catch (BookUnavailableException)
                {
                    Console.WriteLine($"BLOCKED_OK id={probeBlockedId}");
                }
            }

            if (probePreviewId is not null)
            {
                var preview = await books.GetBookPreviewAsync(probePreviewId);
                Console.WriteLine(
                    $"PREVIEW_OK id={probePreviewId} annotation_chars={preview.Annotation.Length} "
                    + $"cover_bytes={preview.CoverBytes?.Length ?? 0} cover_type={preview.CoverContentType ?? "none"}");
            }

            return;
        }

        builder.Services.AddSingleton<ITelegramBotClient>(services =>
        {
            var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
            return new TelegramBotClient(settings.BotToken);
        });

        builder.Services.AddSingleton<BotSessionStore>();
        builder.Services.AddHostedService<TelegramBotService>();

        await builder.Build().RunAsync();
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
