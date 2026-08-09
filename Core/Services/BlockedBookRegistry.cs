using Microsoft.Extensions.Logging;

namespace booksBot.Core.Services;

public sealed class BlockedBookRegistry
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly object _refreshLock = new();
    private HashSet<string> _snapshot = new(StringComparer.Ordinal);
    private long _nextRefreshUtcTicks;
    private DateTime _loadedLastWriteUtc = DateTime.MinValue;
    private long _loadedLength = -1;

    public BlockedBookRegistry(
        string path,
        ILogger logger,
        TimeSpan? refreshInterval = null)
    {
        _path = path;
        _logger = logger;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(5);
        Refresh(force: true);
    }

    public bool IsBlocked(string bookId) => GetSnapshot().Contains(bookId);

    public IReadOnlySet<string> GetSnapshot()
    {
        Refresh(force: false);
        return Volatile.Read(ref _snapshot);
    }

    private void Refresh(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now.Ticks < Volatile.Read(ref _nextRefreshUtcTicks))
        {
            return;
        }

        lock (_refreshLock)
        {
            now = DateTime.UtcNow;
            if (!force && now.Ticks < _nextRefreshUtcTicks)
            {
                return;
            }

            Volatile.Write(ref _nextRefreshUtcTicks, now.Add(_refreshInterval).Ticks);

            try
            {
                if (!File.Exists(_path))
                {
                    if (_snapshot.Count > 0 || _loadedLength >= 0)
                    {
                        Volatile.Write(ref _snapshot, new HashSet<string>(StringComparer.Ordinal));
                        _logger.LogWarning("Book blocklist {BlocklistPath} was removed; no books are currently blocked.", _path);
                    }

                    _loadedLastWriteUtc = DateTime.MinValue;
                    _loadedLength = -1;
                    return;
                }

                var file = new FileInfo(_path);
                if (!force
                    && file.LastWriteTimeUtc == _loadedLastWriteUtc
                    && file.Length == _loadedLength)
                {
                    return;
                }

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(_path))
                {
                    var value = line.Split('#', 2)[0].Trim();
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (value.All(char.IsAsciiDigit))
                    {
                        ids.Add(value);
                    }
                    else
                    {
                        _logger.LogWarning("Ignoring invalid book ID in {BlocklistPath}: {Value}", _path, value);
                    }
                }

                Volatile.Write(ref _snapshot, ids);
                _loadedLastWriteUtc = file.LastWriteTimeUtc;
                _loadedLength = file.Length;
                _logger.LogInformation("Loaded {BlockedBookCount} blocked book IDs from {BlocklistPath}.", ids.Count, _path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not refresh book blocklist {BlocklistPath}; keeping the previous snapshot.", _path);
            }
        }
    }
}
