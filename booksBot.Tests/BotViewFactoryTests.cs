using booksBot.Application.TelegramBot;
using booksBot.Core.Models;

namespace booksBot.Tests;

public sealed class BotViewFactoryTests
{
    [Fact]
    public void SearchPage_UsesNumberButtonsAndSummarizesLongAuthorLists()
    {
        var books = new[]
        {
            CreateBook("1", "Понедельник начинается в субботу", "Стругацкий", "Аркадий", "Натанович"),
            new BookEntry
            {
                LibId = "2",
                Title = "Антология",
                Authors =
                [
                    new AuthorPart { LastName = "Первый", FirstName = "Автор" },
                    new AuthorPart { LastName = "Второй", FirstName = "Автор" },
                    new AuthorPart { LastName = "Третий", FirstName = "Автор" },
                    new AuthorPart { LastName = "Четвёртый", FirstName = "Автор" }
                ]
            }
        };
        var result = new BookSearchResult("Стругацкий", BookSearchField.All, books, 2, false);
        var session = new SearchSession("session1", 1, 2, result, DateTime.UtcNow.AddMinutes(1));

        var view = BotViewFactory.SearchPage(session, 0);
        var rows = view.Keyboard.InlineKeyboard.Select(row => row.ToArray()).ToArray();

        Assert.Equal(new[] { "1", "2" }, rows[0].Select(button => button.Text));
        Assert.Contains("Понедельник начинается в субботу", view.Html);
        Assert.Contains("ещё 2", view.Html);
        Assert.DoesNotContain(rows.SelectMany(row => row), button => button.Text.Contains("Понедельник"));
    }

    [Fact]
    public void PreviewCard_CombinesMetadataDescriptionAndNavigation()
    {
        var book = CreateBook("42", "Пикник на обочине", "Стругацкий", "Аркадий", "Натанович");
        book.Series = "Мир Полудня";
        book.SeriesOrder = 3;
        var result = new BookSearchResult("Стругацкий", BookSearchField.All, [book], 1, false);
        var session = new SearchSession("session2", 1, 2, result, DateTime.UtcNow.AddMinutes(1));

        var card = BotViewFactory.PreviewCard(session, book, "Краткое содержание книги.");
        var buttons = card.Keyboard.InlineKeyboard.SelectMany(row => row).Select(button => button.Text).ToArray();

        Assert.Contains("Пикник на обочине", card.Html);
        Assert.Contains("Мир Полудня", card.Html);
        Assert.Contains("Краткое содержание книги.", card.Html);
        Assert.Contains("⬇️ Скачать FB2", buttons);
        Assert.Contains("↩️ К результатам", buttons);
        Assert.DoesNotContain("Обложка и описание", buttons);
    }

    private static BookEntry CreateBook(
        string id,
        string title,
        string lastName,
        string firstName,
        string middleName) => new()
        {
            LibId = id,
            Title = title,
            Authors =
        [
            new AuthorPart
            {
                LastName = lastName,
                FirstName = firstName,
                MiddleName = middleName
            }
        ]
        };
}
