using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using booksBot.Core.Interfaces;
using booksBot.Core.Services;
using booksBot.Infrastructure.Configuration;
using System;
using System.Threading.Tasks;
using Telegram.Bot;
using booksBot.Application.TelegramBot;
using Microsoft.Extensions.Options;

namespace booksBot
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Чтение конфигурации
                services.Configure<AppSettings>(context.Configuration.GetSection("AppSettings"));

                // Регистрация ITelegramBotClient как Singleton
                services.AddSingleton<ITelegramBotClient>(provider =>
                {
                    var appSettings = provider.GetRequiredService<IOptions<AppSettings>>().Value;
                    var botToken = appSettings.BotToken;
                    return new TelegramBotClient(botToken);
                });

                // Регистрация сервисов
                services.AddTransient<IOutputService, ConsoleOutputService>(); // Это для BookService
                services.AddTransient<IBookService, BookService>();

                // Регистрация IOutputService для TelegramBotService
                services.AddSingleton(provider =>
                {
                    var botClient = provider.GetRequiredService<ITelegramBotClient>();
                    return new TelegramOutputService(botClient);
                });

                services.AddSingleton<TelegramBotService>();
            })
            .Build();

            // Получение необходимых сервисов
            var bookService = host.Services.GetRequiredService<IBookService>();
            var telegramBotService = host.Services.GetRequiredService<TelegramBotService>();

            try
            {
                // Сначала выполняем загрузку коллекции
                Console.WriteLine("Загрузка коллекции книг...");
                await bookService.LoadCollectionAsync();

                // Запуск Telegram бота
                telegramBotService.Start();

                Console.WriteLine("Бот запущен. Нажмите Ctrl+C для остановки.");

                // Ожидание завершения работы приложения
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запуске бота: {ex.Message}");
            }
        }
    }
}
