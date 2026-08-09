using System.Collections.Concurrent;
using booksBot.Core.Models;

namespace booksBot.Application.TelegramBot;

public sealed class BotSessionStore
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SearchSession> _sessions = new(StringComparer.Ordinal);

    public SearchSession Create(long chatId, long userId, BookSearchResult result)
    {
        RemoveExpired();

        SearchSession session;
        do
        {
            session = new SearchSession(
                Guid.NewGuid().ToString("N")[..8],
                chatId,
                userId,
                result,
                DateTime.UtcNow.Add(SessionLifetime));
        }
        while (!_sessions.TryAdd(session.Id, session));

        foreach (var staleSession in _sessions.Values
                     .Where(item => item.ChatId == chatId && item.UserId == userId && item.Id != session.Id)
                     .OrderByDescending(item => item.ExpiresAtUtc)
                     .Skip(8))
        {
            _sessions.TryRemove(staleSession.Id, out _);
        }

        return session;
    }

    public bool TryGet(string id, long chatId, long userId, out SearchSession session)
    {
        session = null!;
        if (!_sessions.TryGetValue(id, out var candidate))
        {
            return false;
        }

        if (candidate.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(id, out _);
            return false;
        }

        if (candidate.ChatId != chatId || candidate.UserId != userId)
        {
            return false;
        }

        session = candidate;
        return true;
    }

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAtUtc <= now)
            {
                _sessions.TryRemove(item.Key, out _);
            }
        }
    }
}

public sealed record SearchSession(
    string Id,
    long ChatId,
    long UserId,
    BookSearchResult Result,
    DateTime ExpiresAtUtc);
