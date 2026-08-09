# BoxBot

BoxBot is a private Telegram catalog for a local INPX/FB2 collection. It searches by title, author, and series, then extracts the selected FB2 directly from ZIP or 7z archives.

The original working implementation is preserved as the `v1.0.0` tag. The current branch contains the redesigned native Telegram V2 interface.

## V2 highlights

- one search box for title, author, and series;
- compact HTML book cards and in-place pagination;
- callback sessions bound to both chat and user, with expiration;
- direct FB2 download buttons and backward compatibility with `/download@<id>`;
- Telegram `file_id` cache, so a previously uploaded book is sent without reopening its archive;
- one-time ZIP/7z range catalog instead of scanning the directory for every download;
- normalized Russian search (`ё`/`е`, punctuation, word order);
- atomic collection rebuild into a temporary database before replacing the active index;
- separate persistent state database for Telegram file IDs;
- cancellation, structured logging, generic user-facing errors, and non-zero startup failures;
- integration tests for indexing, search, archive extraction, cleanup, and cache behavior.

## Configuration

Copy `appsettings.example.json` to `appsettings.json`, or configure values with environment variables. The bot token should be provided as `AppSettings__BotToken` in production instead of being committed.

Required settings:

- `AppSettings__BotToken`
- `AppSettings__InpxCollectionPath`
- `AppSettings__ArchivesPath`
- `AppSettings__LiteDbPath`

Optional settings:

- `AppSettings__StateDbPath` — defaults to `boxbot-state.db` beside the main database;
- `AppSettings__TempPath` — defaults to `temp` beside the main database.

## Build and test

```powershell
dotnet restore booksBot.sln
dotnet test booksBot.sln -c Release
dotnet publish booksBot.csproj -c Release -r win-x64 --self-contained false
```

Production data can be checked without starting Telegram polling:

```powershell
dotnet booksBot.dll --probe-query "Касс Маркус" --probe-book 806581
```

The production Windows scheduled task launches the bot through
`deployment/run-boxbot.ps1`. The wrapper waits for the local SOCKS egress
listener and keeps all runtime output in `boxbot.log`.
