namespace booksBot.Infrastructure.Configuration;

public sealed class AppSettings
{
    public const string SectionName = "AppSettings";

    public string BotToken { get; init; } = string.Empty;
    public string InpxCollectionPath { get; init; } = string.Empty;
    public string ArchivesPath { get; init; } = string.Empty;
    public string LiteDbPath { get; init; } = string.Empty;
    public string? StateDbPath { get; init; }
    public string? TempPath { get; init; }
}
