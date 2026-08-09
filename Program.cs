using booksBot.Application.TelegramBot;
using booksBot.Core.Interfaces;
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
        var probeQuery = GetOptionValue(args, "--probe-query");
        var maintenanceMode = args.Contains("--index-only", StringComparer.OrdinalIgnoreCase)
            || probeBookId is not null
            || probeQuery is not null;

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
            await books.LoadCollectionAsync();

            if (probeQuery is not null)
            {
                var result = await books.SearchAsync(probeQuery);
                Console.WriteLine($"SEARCH_OK query={probeQuery} matches={result.MatchedCount} returned={result.Books.Count}");
            }

            if (probeBookId is not null)
            {
                var book = await books.GetBookAsync(probeBookId)
                    ?? throw new InvalidOperationException($"Book {probeBookId} is missing from the index.");
                await using var download = await books.PrepareBookFileAsync(probeBookId);
                Console.WriteLine($"BOOK_OK id={book.LibId} bytes={download.Length} title={book.Title}");
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
