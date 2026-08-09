using booksBot.Core.Interfaces;
using booksBot.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Xml;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace booksBot.Application.TelegramBot;

public sealed class TelegramBotService : BackgroundService
{
    private readonly IBookService _bookService;
    private readonly ITelegramBotClient _bot;
    private readonly BotSessionStore _sessions;
    private readonly ILogger<TelegramBotService> _logger;

    public TelegramBotService(
        IBookService bookService,
        ITelegramBotClient bot,
        BotSessionStore sessions,
        ILogger<TelegramBotService> logger)
    {
        _bookService = bookService;
        _bot = bot;
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Loading the book collection.");
        await _bookService.LoadCollectionAsync(stoppingToken);

        var me = await _bot.GetMe(stoppingToken);
        await _bot.SetMyCommands(
            [
                new BotCommand("start", "Открыть главное меню"),
                new BotCommand("random", "Показать случайную книгу"),
                new BotCommand("help", "Как пользоваться ботом")
            ],
            cancellationToken: stoppingToken);

        _bot.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
                DropPendingUpdates = false
            },
            stoppingToken);

        _logger.LogInformation("BookBot V2 started as @{Username}.", me.Username);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private Task HandleUpdateAsync(
        ITelegramBotClient bot,
        Update update,
        CancellationToken cancellationToken) => update switch
        {
            { Message.Text: not null } => HandleMessageAsync(update.Message, cancellationToken),
            { CallbackQuery: not null } => HandleCallbackAsync(update.CallbackQuery, cancellationToken),
            _ => Task.CompletedTask
        };

    private async Task HandleMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var text = message.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var command = text.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
            switch (command)
            {
                case "/start":
                    await SendViewAsync(message.Chat.Id, BotViewFactory.Welcome(), cancellationToken);
                    return;
                case "/help":
                    await SendViewAsync(message.Chat.Id, BotViewFactory.Help(), cancellationToken);
                    return;
                case "/random":
                    await SendRandomBookAsync(message.Chat.Id, cancellationToken);
                    return;
            }

            if (TryParseLegacyDownload(text, out var bookId))
            {
                await SendBookAsync(message.Chat.Id, bookId, cancellationToken);
                return;
            }

            if (text.StartsWith('/'))
            {
                await _bot.SendMessage(
                    message.Chat.Id,
                    "Не знаю такой команды. Просто отправь название книги, автора или серию.",
                    replyMarkup: BotViewFactory.Welcome().Keyboard,
                    cancellationToken: cancellationToken);
                return;
            }

            if (text.Length < 2)
            {
                await _bot.SendMessage(
                    message.Chat.Id,
                    "Запрос слишком короткий — напиши хотя бы два символа.",
                    cancellationToken: cancellationToken);
                return;
            }

            await SearchAndShowAsync(
                message.Chat.Id,
                message.From?.Id ?? message.Chat.Id,
                text[..Math.Min(text.Length, 160)],
                BookSearchField.All,
                progressMessage: null,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to handle message {MessageId} in chat {ChatId}.", message.Id, message.Chat.Id);
            await SendGenericErrorAsync(message.Chat.Id, cancellationToken);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken cancellationToken)
    {
        if (callback.Message is null || string.IsNullOrWhiteSpace(callback.Data))
        {
            await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        var chatId = callback.Message.Chat.Id;
        var userId = callback.From.Id;
        var data = callback.Data;

        try
        {
            if (data.StartsWith("preview:", StringComparison.Ordinal))
            {
                await _bot.AnswerCallbackQuery(callback.Id, "Достаю обложку и описание…", cancellationToken: cancellationToken);
                await SendPreviewAsync(chatId, data["preview:".Length..], cancellationToken);
                return;
            }

            if (data.StartsWith("download:", StringComparison.Ordinal))
            {
                await _bot.AnswerCallbackQuery(callback.Id, "Готовлю FB2…", cancellationToken: cancellationToken);
                await SendBookAsync(chatId, data["download:".Length..], cancellationToken);
                return;
            }

            await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken);

            switch (data)
            {
                case "noop":
                    return;
                case "home":
                    await EditViewAsync(callback.Message, BotViewFactory.Welcome(), cancellationToken);
                    return;
                case "help":
                    await EditViewAsync(callback.Message, BotViewFactory.Help(), cancellationToken);
                    return;
                case "random":
                    await EditRandomBookAsync(callback.Message, cancellationToken);
                    return;
            }

            var parts = data.Split(':');
            if (parts.Length < 2)
            {
                return;
            }

            if (parts[0] == "page" && parts.Length == 3 && int.TryParse(parts[2], out var page))
            {
                if (!TryGetSession(parts[1], chatId, userId, out var session))
                {
                    await ShowExpiredSessionAsync(callback.Message, cancellationToken);
                    return;
                }

                await EditViewAsync(callback.Message, BotViewFactory.SearchPage(session, page), cancellationToken);
                return;
            }

            if (parts[0] == "book" && parts.Length == 3)
            {
                if (!TryGetSession(parts[1], chatId, userId, out var session))
                {
                    await ShowExpiredSessionAsync(callback.Message, cancellationToken);
                    return;
                }

                var book = await _bookService.GetBookAsync(parts[2], cancellationToken);
                if (book is null)
                {
                    await _bot.SendMessage(chatId, "Эта книга больше не найдена в индексе.", cancellationToken: cancellationToken);
                    return;
                }

                var index = session.Result.Books.ToList().FindIndex(item => item.LibId == book.LibId);
                var returnPage = Math.Max(0, index) / BotViewFactory.PageSize;
                await SendBookCardAsync(
                    chatId,
                    book,
                    session,
                    returnPage,
                    callback.Message,
                    cancellationToken);
                return;
            }

            if (parts[0] == "refine" && parts.Length == 3)
            {
                if (!TryGetSession(parts[1], chatId, userId, out var session))
                {
                    await ShowExpiredSessionAsync(callback.Message, cancellationToken);
                    return;
                }

                var field = ParseField(parts[2]);
                await SearchAndShowAsync(
                    chatId,
                    userId,
                    session.Result.Query,
                    field,
                    callback.Message,
                    cancellationToken);
                return;
            }

            if (parts[0] == "related" && parts.Length == 4)
            {
                if (!TryGetSession(parts[1], chatId, userId, out _))
                {
                    await ShowExpiredSessionAsync(callback.Message, cancellationToken);
                    return;
                }

                var book = await _bookService.GetBookAsync(parts[2], cancellationToken);
                if (book is null)
                {
                    return;
                }

                var field = ParseField(parts[3]);
                var query = field == BookSearchField.Series
                    ? book.Series
                    : book.Authors?.FirstOrDefault()?.DisplayName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(query))
                {
                    await SearchAndShowAsync(chatId, userId, query, field, callback.Message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to handle callback {CallbackData} in chat {ChatId}.", data, chatId);
            await SendGenericErrorAsync(chatId, cancellationToken);
        }
    }

    private async Task SearchAndShowAsync(
        long chatId,
        long userId,
        string query,
        BookSearchField field,
        Message? progressMessage,
        CancellationToken cancellationToken)
    {
        progressMessage ??= await _bot.SendMessage(
            chatId,
            $"🔎 Ищу: <b>{System.Net.WebUtility.HtmlEncode(query)}</b>…",
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);

        var result = await _bookService.SearchAsync(query, field, cancellationToken: cancellationToken);
        if (result.Books.Count == 0)
        {
            var emptyView = new BotView(
                $"Ничего не нашёл по запросу <b>{System.Net.WebUtility.HtmlEncode(query)}</b>.\n\nПопробуй другое написание или более короткий запрос.",
                new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🏠 В начало", "home") }
                }));
            await EditViewAsync(progressMessage, emptyView, cancellationToken);
            return;
        }

        var session = _sessions.Create(chatId, userId, result);
        await EditViewAsync(progressMessage, BotViewFactory.SearchPage(session, 0), cancellationToken);
    }

    private async Task SendBookAsync(long chatId, string bookId, CancellationToken cancellationToken)
    {
        var book = await _bookService.GetBookAsync(bookId, cancellationToken);
        if (book is null)
        {
            await _bot.SendMessage(
                chatId,
                "Эта книга недоступна в каталоге.",
                cancellationToken: cancellationToken);
            return;
        }

        var caption = BotViewFactory.DownloadCaption(book);
        var cachedFileId = await _bookService.GetTelegramFileIdAsync(bookId, cancellationToken);

        await _bot.SendChatAction(chatId, ChatAction.UploadDocument, cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(cachedFileId))
        {
            try
            {
                await _bot.SendDocument(
                    chatId,
                    cachedFileId,
                    caption: caption,
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);
                return;
            }
            catch (ApiRequestException exception)
            {
                _logger.LogWarning(exception, "Cached Telegram file ID for {BookId} is invalid; uploading again.", bookId);
                await _bookService.SaveTelegramFileIdAsync(bookId, string.Empty, cancellationToken);
            }
        }

        try
        {
            await using var download = await _bookService.PrepareBookFileAsync(bookId, cancellationToken);
            await using var stream = download.OpenRead();
            var sentMessage = await _bot.SendDocument(
                chatId,
                new InputFileStream(stream, download.FileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(sentMessage.Document?.FileId))
            {
                await _bookService.SaveTelegramFileIdAsync(bookId, sentMessage.Document.FileId, cancellationToken);
            }
        }
        catch (BookUnavailableException exception)
        {
            _logger.LogInformation(exception, "Book {BookId} is blocked by the exact-ID registry.", bookId);
            await _bot.SendMessage(
                chatId,
                "Эта книга недоступна в каталоге.",
                cancellationToken: cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            _logger.LogWarning(exception, "Book file {BookId} was not found.", bookId);
            await _bot.SendMessage(
                chatId,
                "Не смог найти FB2 в архивах. Я записал ошибку в журнал.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendPreviewAsync(long chatId, string bookId, CancellationToken cancellationToken)
    {
        var book = await _bookService.GetBookAsync(bookId, cancellationToken);
        if (book is null)
        {
            await _bot.SendMessage(chatId, "Эта книга больше не найдена в индексе.", cancellationToken: cancellationToken);
            return;
        }

        await SendBookCardAsync(
            chatId,
            book,
            session: null,
            returnPage: 0,
            sourceMessage: null,
            cancellationToken);
    }

    private async Task SendBookCardAsync(
        long chatId,
        BookEntry book,
        SearchSession? session,
        int returnPage,
        Message? sourceMessage,
        CancellationToken cancellationToken)
    {
        var fallbackCard = BotViewFactory.BookDetails(session, book, returnPage);
        await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: cancellationToken);
        BookPreview preview;
        try
        {
            preview = await _bookService.GetBookPreviewAsync(book.LibId, cancellationToken);
        }
        catch (XmlException exception)
        {
            _logger.LogWarning(exception, "Could not parse FB2 preview metadata for {BookId}.", book.LibId);
            await ReplaceOrSendViewAsync(sourceMessage, fallbackCard, chatId, cancellationToken);
            return;
        }

        if (!preview.HasContent)
        {
            await ReplaceOrSendViewAsync(sourceMessage, fallbackCard, chatId, cancellationToken);
            return;
        }

        var card = BotViewFactory.PreviewCard(session, book, preview.Annotation, returnPage);
        if (!string.IsNullOrWhiteSpace(preview.TelegramCoverFileId))
        {
            try
            {
                await _bot.SendPhoto(
                    chatId,
                    preview.TelegramCoverFileId,
                    caption: card.Html,
                    parseMode: ParseMode.Html,
                    replyMarkup: card.Keyboard,
                    cancellationToken: cancellationToken);
                await DeleteSourceMessageAsync(sourceMessage, cancellationToken);
                return;
            }
            catch (ApiRequestException exception)
            {
                _logger.LogWarning(exception, "Cached Telegram cover ID for {BookId} is invalid; uploading again.", book.LibId);
                await _bookService.SaveBookPreviewAsync(
                    book.LibId,
                    preview.Annotation,
                    hasCover: true,
                    telegramCoverFileId: string.Empty,
                    cancellationToken);
                preview = await _bookService.GetBookPreviewAsync(book.LibId, cancellationToken);
            }
        }

        if (preview.CoverBytes is { Length: > 0 } coverBytes)
        {
            try
            {
                await using var stream = new MemoryStream(coverBytes, writable: false);
                var extension = preview.CoverContentType?.Equals("image/png", StringComparison.OrdinalIgnoreCase) == true
                    ? ".png"
                    : ".jpg";
                var sentMessage = await _bot.SendPhoto(
                    chatId,
                    new InputFileStream(stream, $"cover-{book.LibId}{extension}"),
                    caption: card.Html,
                    parseMode: ParseMode.Html,
                    replyMarkup: card.Keyboard,
                    cancellationToken: cancellationToken);
                var telegramFileId = sentMessage.Photo?.LastOrDefault()?.FileId ?? string.Empty;
                await _bookService.SaveBookPreviewAsync(
                    book.LibId,
                    preview.Annotation,
                    hasCover: true,
                    telegramFileId,
                    cancellationToken);
                await DeleteSourceMessageAsync(sourceMessage, cancellationToken);
                return;
            }
            catch (ApiRequestException exception)
            {
                _logger.LogWarning(exception, "Could not upload the embedded cover for {BookId}.", book.LibId);
            }
        }

        await ReplaceOrSendViewAsync(sourceMessage, card, chatId, cancellationToken);
        await _bookService.SaveBookPreviewAsync(
            book.LibId,
            preview.Annotation,
            hasCover: false,
            telegramCoverFileId: string.Empty,
            cancellationToken);
    }

    private async Task SendRandomBookAsync(long chatId, CancellationToken cancellationToken)
    {
        var book = await _bookService.GetRandomBookAsync(cancellationToken);
        if (book is null)
        {
            await _bot.SendMessage(chatId, "Коллекция пока пуста.", cancellationToken: cancellationToken);
            return;
        }

        await SendBookCardAsync(chatId, book, null, 0, null, cancellationToken);
    }

    private async Task EditRandomBookAsync(Message message, CancellationToken cancellationToken)
    {
        var book = await _bookService.GetRandomBookAsync(cancellationToken);
        if (book is null)
        {
            return;
        }

        await SendBookCardAsync(message.Chat.Id, book, null, 0, message, cancellationToken);
    }

    private Task SendViewAsync(long chatId, BotView view, CancellationToken cancellationToken) => _bot.SendMessage(
        chatId,
        view.Html,
        parseMode: ParseMode.Html,
        replyMarkup: view.Keyboard,
        cancellationToken: cancellationToken);

    private async Task EditViewAsync(Message message, BotView view, CancellationToken cancellationToken)
    {
        if (message.Text is null)
        {
            await DeleteSourceMessageAsync(message, cancellationToken);
            await SendViewAsync(message.Chat.Id, view, cancellationToken);
            return;
        }

        try
        {
            await _bot.EditMessageText(
                message.Chat.Id,
                message.Id,
                view.Html,
                parseMode: ParseMode.Html,
                replyMarkup: view.Keyboard,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException exception) when (exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Double taps on navigation buttons are harmless.
        }
    }

    private Task ReplaceOrSendViewAsync(
        Message? sourceMessage,
        BotView view,
        long chatId,
        CancellationToken cancellationToken) => sourceMessage is null
        ? SendViewAsync(chatId, view, cancellationToken)
        : EditViewAsync(sourceMessage, view, cancellationToken);

    private async Task DeleteSourceMessageAsync(Message? sourceMessage, CancellationToken cancellationToken)
    {
        if (sourceMessage is null)
        {
            return;
        }

        try
        {
            await _bot.DeleteMessage(sourceMessage.Chat.Id, sourceMessage.Id, cancellationToken);
        }
        catch (ApiRequestException exception)
        {
            _logger.LogDebug(exception, "Could not remove replaced BookBot message {MessageId}.", sourceMessage.Id);
        }
    }

    private bool TryGetSession(string id, long chatId, long userId, out SearchSession session) =>
        _sessions.TryGet(id, chatId, userId, out session);

    private async Task ShowExpiredSessionAsync(Message message, CancellationToken cancellationToken)
    {
        var view = new BotView(
            "Этот поиск уже устарел. Отправь запрос ещё раз.",
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🏠 В начало", "home") }
            }));
        await EditViewAsync(message, view, cancellationToken);
    }

    private Task SendGenericErrorAsync(long chatId, CancellationToken cancellationToken) => _bot.SendMessage(
        chatId,
        "Что-то пошло не так. Ошибка записана в журнал — попробуй ещё раз.",
        cancellationToken: cancellationToken);

    private Task HandlePollingErrorAsync(
        ITelegramBotClient bot,
        Exception exception,
        HandleErrorSource source,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram polling error from {ErrorSource}.", source);
        return Task.CompletedTask;
    }

    private static BookSearchField ParseField(string value) => value.ToLowerInvariant() switch
    {
        "title" => BookSearchField.Title,
        "author" => BookSearchField.Author,
        "series" => BookSearchField.Series,
        _ => BookSearchField.All
    };

    private static bool TryParseLegacyDownload(string text, out string bookId)
    {
        bookId = string.Empty;
        if (text.StartsWith("/download@", StringComparison.OrdinalIgnoreCase))
        {
            bookId = text["/download@".Length..].Trim();
        }
        else if (text.StartsWith("/download ", StringComparison.OrdinalIgnoreCase))
        {
            bookId = text["/download ".Length..].Trim();
        }

        return bookId.All(char.IsDigit) && bookId.Length > 0;
    }
}
