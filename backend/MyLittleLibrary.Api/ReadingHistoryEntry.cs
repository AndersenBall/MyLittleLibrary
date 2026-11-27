public sealed class ReadingHistoryEntry
{
    public required string OwnerKey { get; init; }
    public required string WorkId { get; init; }
    public required string EditionId { get; init; }
    public decimal? Rating { get; init; }
    public string? ReviewComment { get; init; }

    public static string DocumentId(string ownerKey, string editionId) => $"ReadingHistoryEntries/{ownerKey}/{editionId}";

    public ReadGalleryBook ToGallery(LibraryEdition edition, LibraryBook work) =>
        new(EditionId, edition.Title, work.Authors, edition.CoverUrl, Rating);
}

public sealed class MarkBookAsReadRequest
{
    public required string WorkId { get; init; }
    public required string EditionId { get; init; }
    public required decimal Rating { get; init; }
    public string? ReviewComment { get; init; }
}

public sealed record ReadGalleryBook(string EditionId, string Title, string[] Authors, string? CoverUrl, decimal? Rating);