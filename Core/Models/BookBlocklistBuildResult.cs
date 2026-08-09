namespace booksBot.Core.Models;

public sealed record BookBlocklistBuildResult(
    int SourceMaterialCount,
    int CandidateTitleCount,
    int ExactCatalogMatchCount,
    int BlockedBookCount,
    int ReviewMatchCount);
