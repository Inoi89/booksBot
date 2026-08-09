using System.Net;
using System.Text;
using booksBot.Core.Models;
using Telegram.Bot.Types.ReplyMarkups;

namespace booksBot.Application.TelegramBot;

public sealed record BotView(string Html, InlineKeyboardMarkup Keyboard);

public static class BotViewFactory
{
    public const int PageSize = 5;

    public static BotView Welcome()
    {
        const string html = """
            📚 <b>BoxBot</b>

            Отправь название книги, автора или серию — я поищу сразу везде.

            Например:
            • <code>Касс Маркус</code>
            • <code>Империя храмов</code>
            • <code>Святоша</code>
            """;

        return new BotView(
            html,
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайная книга", "random") },
                new[] { InlineKeyboardButton.WithCallbackData("ℹ️ Возможности", "help") }
            }));
    }

    public static BotView Help()
    {
        const string html = """
            <b>Как пользоваться BoxBot</b>

            🔎 Просто напиши запрос — поиск пройдёт по названию, автору и серии.
            📖 Нажми на книгу, чтобы открыть карточку.
            ⬇️ Кнопка скачивания пришлёт FB2 прямо в чат.

            Команды:
            /start — главное меню
            /random — случайная книга
            /help — эта справка
            """;

        return new BotView(
            html,
            new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🏠 В начало", "home") }
            }));
    }

    public static BotView SearchPage(SearchSession session, int requestedPage)
    {
        var result = session.Result;
        var pageCount = Math.Max(1, (int)Math.Ceiling(result.Books.Count / (double)PageSize));
        var page = Math.Clamp(requestedPage, 0, pageCount - 1);
        var pageBooks = result.Books.Skip(page * PageSize).Take(PageSize).ToArray();

        var builder = new StringBuilder();
        builder.Append("🔎 <b>").Append(Escape(result.Query)).AppendLine("</b>");
        builder.AppendLine();
        builder.Append("Найдено: <b>").Append(result.MatchedCount);
        if (result.IsTruncated)
        {
            builder.Append('+');
        }

        builder.AppendLine("</b>").AppendLine();

        for (var index = 0; index < pageBooks.Length; index++)
        {
            var book = pageBooks[index];
            var ordinal = page * PageSize + index + 1;
            builder.Append(ordinal).Append(". <b>").Append(Escape(book.Title)).AppendLine("</b>");
            builder.Append("   ").Append(Escape(Authors(book)));
            if (!string.IsNullOrWhiteSpace(book.Series))
            {
                builder.Append(" · ").Append(Escape(book.Series));
                if (book.SeriesOrder.HasValue)
                {
                    builder.Append(" #").Append(book.SeriesOrder.Value);
                }
            }

            builder.AppendLine();
        }

        builder.AppendLine().Append("Страница ").Append(page + 1).Append(" из ").Append(pageCount);

        var rows = pageBooks
            .Select((book, index) => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{page * PageSize + index + 1}. {Truncate(book.Title ?? "Без названия", 42)}",
                    $"book:{session.Id}:{book.LibId}")
            })
            .ToList();

        var navigation = new List<InlineKeyboardButton>();
        navigation.Add(page > 0
            ? InlineKeyboardButton.WithCallbackData("◀️", $"page:{session.Id}:{page - 1}")
            : InlineKeyboardButton.WithCallbackData("·", "noop"));
        navigation.Add(InlineKeyboardButton.WithCallbackData($"{page + 1}/{pageCount}", "noop"));
        navigation.Add(page < pageCount - 1
            ? InlineKeyboardButton.WithCallbackData("▶️", $"page:{session.Id}:{page + 1}")
            : InlineKeyboardButton.WithCallbackData("·", "noop"));
        rows.Add(navigation.ToArray());

        if (result.Field == BookSearchField.All)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("📕 Название", $"refine:{session.Id}:title"),
                InlineKeyboardButton.WithCallbackData("👤 Автор", $"refine:{session.Id}:author"),
                InlineKeyboardButton.WithCallbackData("📚 Серия", $"refine:{session.Id}:series")
            });
        }

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 В начало", "home") });
        return new BotView(builder.ToString(), new InlineKeyboardMarkup(rows));
    }

    public static BotView BookDetails(SearchSession? session, BookEntry book, int returnPage = 0)
    {
        var builder = new StringBuilder();
        builder.Append("📖 <b>").Append(Escape(book.Title)).AppendLine("</b>").AppendLine();
        builder.Append("👤 ").AppendLine(Escape(Authors(book)));

        if (!string.IsNullOrWhiteSpace(book.Series))
        {
            builder.Append("📚 ").Append(Escape(book.Series));
            if (book.SeriesOrder.HasValue)
            {
                builder.Append(" · книга ").Append(book.SeriesOrder.Value);
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(book.Language))
        {
            builder.Append("🌐 ").AppendLine(Escape(book.Language));
        }

        if (!string.IsNullOrWhiteSpace(book.Genre))
        {
            builder.Append("🏷 ").AppendLine(Escape(book.Genre));
        }

        builder.Append("🆔 <code>").Append(Escape(book.LibId)).AppendLine("</code>");

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("⬇️ Скачать FB2", $"download:{book.LibId}") }
        };

        if (session is not null)
        {
            var related = new List<InlineKeyboardButton>();
            if (book.Authors?.Count > 0)
            {
                related.Add(InlineKeyboardButton.WithCallbackData("👤 Книги автора", $"related:{session.Id}:{book.LibId}:author"));
            }

            if (!string.IsNullOrWhiteSpace(book.Series))
            {
                related.Add(InlineKeyboardButton.WithCallbackData("📚 Вся серия", $"related:{session.Id}:{book.LibId}:series"));
            }

            if (related.Count > 0)
            {
                rows.Add(related.ToArray());
            }

            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("↩️ К результатам", $"page:{session.Id}:{returnPage}") });
        }

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 В начало", "home") });
        return new BotView(builder.ToString(), new InlineKeyboardMarkup(rows));
    }

    public static string DownloadCaption(BookEntry? book)
    {
        if (book is null)
        {
            return "📚 Книга из BoxBot";
        }

        return $"📖 <b>{Escape(book.Title)}</b>\n👤 {Escape(Authors(book))}";
    }

    public static string Authors(BookEntry book)
    {
        var value = string.Join("; ", (book.Authors ?? []).Select(author => author.DisplayName)
            .Where(author => !string.IsNullOrWhiteSpace(author)));
        return string.IsNullOrWhiteSpace(value) ? "Автор не указан" : value;
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength
        ? value
        : $"{value[..(maximumLength - 1)].TrimEnd()}…";
}
