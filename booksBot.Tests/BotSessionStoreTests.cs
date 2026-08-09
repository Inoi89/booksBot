using booksBot.Application.TelegramBot;
using booksBot.Core.Models;

namespace booksBot.Tests;

public sealed class BotSessionStoreTests
{
    [Fact]
    public void Session_IsBoundToChatAndUser()
    {
        var store = new BotSessionStore();
        var result = new BookSearchResult("Касс", BookSearchField.All, [], 0, false);
        var session = store.Create(chatId: 100, userId: 200, result);

        Assert.True(store.TryGet(session.Id, 100, 200, out _));
        Assert.False(store.TryGet(session.Id, 999, 200, out _));
        Assert.False(store.TryGet(session.Id, 100, 999, out _));
    }
}
