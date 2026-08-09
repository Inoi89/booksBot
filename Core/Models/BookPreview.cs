namespace booksBot.Core.Models;

public sealed record BookPreview(
    string Annotation,
    string? TelegramCoverFileId,
    byte[]? CoverBytes,
    string? CoverContentType)
{
    public bool HasAnnotation => !string.IsNullOrWhiteSpace(Annotation);
    public bool HasCover => !string.IsNullOrWhiteSpace(TelegramCoverFileId) || CoverBytes is { Length: > 0 };
    public bool HasContent => HasAnnotation || HasCover;
}
