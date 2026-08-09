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
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddOptions<AppSettings>()
            .Bind(builder.Configuration.GetSection(AppSettings.SectionName))
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.BotToken), "Bot token is required")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.InpxCollectionPath), "INPX path is required")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.ArchivesPath), "Archives path is required")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.LiteDbPath), "LiteDB path is required")
            .ValidateOnStart();

        builder.Services.AddSingleton<ITelegramBotClient>(services =>
        {
            var settings = services.GetRequiredService<IOptions<AppSettings>>().Value;
            return new TelegramBotClient(settings.BotToken);
        });

        builder.Services.AddSingleton<IBookService, BookService>();
        builder.Services.AddSingleton<BotSessionStore>();
        builder.Services.AddHostedService<TelegramBotService>();

        await builder.Build().RunAsync();
    }
}
