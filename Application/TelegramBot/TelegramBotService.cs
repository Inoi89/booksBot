using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace booksBot.Application.TelegramBot
{
    public class TelegramBotService
    {
        private readonly IBookService _bookService;
        private readonly ITelegramBotClient _botClient;

        // Словарь для хранения запросов пользователей (chatId -> query)
        private readonly ConcurrentDictionary<long, string> _userQueries = new ConcurrentDictionary<long, string>();

        // Словарь для хранения текущей страницы (chatId -> current page)
        private readonly ConcurrentDictionary<long, int> _userPageIndex = new ConcurrentDictionary<long, int>();

        // Словарь для хранения результатов (chatId -> list of books)
        private readonly ConcurrentDictionary<long, List<BookEntry>> _userResults = new ConcurrentDictionary<long, List<BookEntry>>();

        public TelegramBotService(IBookService bookService, ITelegramBotClient botClient)
        {
            _bookService = bookService;
            _botClient = botClient;
        }

        public void Start()
        {
            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                new Telegram.Bot.Polling.ReceiverOptions
                {
                    AllowedUpdates = { } // Получать все типы обновлений
                }
            );

            var me = _botClient.GetMeAsync().Result;
            Console.WriteLine($"Бот запущен: @{me.Username}");
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Type == UpdateType.Message && update.Message.Type == MessageType.Text)
                {
                    var message = update.Message;
                    var chatId = message.Chat.Id;
                    var userInput = message.Text.Trim();

                    if (userInput.Equals("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        await botClient.SendTextMessageAsync(chatId, "Привет! Введите текст для поиска книги.");
                        return;
                    }

                    if (userInput.StartsWith("/download@"))
                    {
                        var bookId = userInput.Replace("/download@", "").Trim();
                        await HandleDownloadAsync(chatId, bookId);
                        return;
                    }

                    _userQueries[chatId] = userInput;

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("По автору", "search_author"),
                            InlineKeyboardButton.WithCallbackData("По названию", "search_title"),
                            InlineKeyboardButton.WithCallbackData("По серии", "search_series")
                        }
                    });

                    await botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: "Где искать?",
                        replyMarkup: keyboard
                    );
                }
                else if (update.Type == UpdateType.CallbackQuery)
                {
                    var callbackQuery = update.CallbackQuery;
                    var chatId = callbackQuery.Message.Chat.Id;
                    var data = callbackQuery.Data;

                    if (data.StartsWith("search_"))
                    {
                        if (!_userQueries.TryRemove(chatId, out var query))
                        {
                            await botClient.SendTextMessageAsync(chatId, "Пожалуйста, введите текст для поиска.");
                            return;
                        }

                        List<BookEntry> results = data switch
                        {
                            "search_author" => await _bookService.SearchBooksByAuthorAsync(query),
                            "search_title" => await _bookService.SearchBooksByTitleAsync(query),
                            "search_series" => await _bookService.SearchBooksBySeriesAsync(query),
                            _ => null
                        };

                        if (results == null || results.Count == 0)
                        {
                            await botClient.SendTextMessageAsync(chatId, "Ничего не найдено по вашему запросу.");
                        }
                        else
                        {
                            // Сохраняем результаты и сбрасываем страницу
                            _userResults[chatId] = results;
                            _userPageIndex[chatId] = 0;

                            await SendPage(chatId, 0);
                        }
                    }
                    else if (data == "next_page" || data == "prev_page")
                    {
                        if (_userPageIndex.TryGetValue(chatId, out var currentPage))
                        {
                            var newPage = data == "next_page" ? currentPage + 1 : currentPage - 1;
                            await SendPage(chatId, newPage);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке обновления: {ex.Message}");
            }
        }

        private async Task SendPage(long chatId, int pageIndex)
        {
            const int pageSize = 10;
            if (_userResults.TryGetValue(chatId, out var results))
            {
                var totalPages = (int)Math.Ceiling((double)results.Count / pageSize);

                // Проверка на допустимый диапазон страниц
                if (pageIndex < 0 || pageIndex >= totalPages)
                    return;

                _userPageIndex[chatId] = pageIndex;

                // Получаем книги для текущей страницы
                var booksOnPage = results.Skip(pageIndex * pageSize).Take(pageSize);

                // Формируем сообщение
                var messageText = string.Join("\n\n", booksOnPage.Select((book, index) =>
                {
                    var authors = string.Join("; ", book.Authors.Select(a => $"{a.LastName} {a.FirstName} {a.MiddleName}".Trim()));
                    var seriesInfo = !string.IsNullOrEmpty(book.Series)
                     ? $"Серия: {book.Series} {(book.SeriesOrder.HasValue ? $"(Книга {book.SeriesOrder})" : "")}\n"
                     : "";
                    var languageInfo = !string.IsNullOrEmpty(book.Language) ? $" - {book.Language}" : "";
                    var downloadLink = $"/download@{book.LibId}";
                    return $"{index + 1 + pageIndex * pageSize}. {book.Title}{languageInfo}\n{seriesInfo}Автор(ы): {authors}\nСкачать: {downloadLink}\n";
                }));

                messageText += $"\n\nСтраница {pageIndex + 1} из {totalPages}";

                // Создаем клавиатуру для навигации по страницам
                var navigationButtons = new List<InlineKeyboardButton[]>();

                if (pageIndex > 0)
                {
                    navigationButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅ Назад", "prev_page") });
                }

                if (pageIndex < totalPages - 1)
                {
                    navigationButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("Вперед ➡", "next_page") });
                }

                var keyboard = new InlineKeyboardMarkup(navigationButtons);

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: messageText,
                    replyMarkup: keyboard
                );
            }
        }

        private async Task HandleDownloadAsync(long chatId, string bookId)
        {
            try
            {
                var bookFile = await _bookService.GetBookFileAsync(bookId);

                using (var stream = new MemoryStream(bookFile))
                {
                    var inputOnlineFile = new Telegram.Bot.Types.InputFileStream(stream, $"{bookId}.fb2");
                    await _botClient.SendDocumentAsync(chatId, inputOnlineFile);
                }
            }
            catch (FileNotFoundException ex)
            {
                await _botClient.SendTextMessageAsync(chatId, $"Файл не найден: {ex.Message}");
            }
            catch (Exception ex)
            {
                await _botClient.SendTextMessageAsync(chatId, $"Ошибка при скачивании файла: {ex.Message}");
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Ошибка Telegram бота: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}
