public sealed class LibraryEntry
{
    public const string CoverAttachmentName = "cover";
    public required string OwnerKey { get; init; }
    public required string BookId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public bool HasLocalCover { get; init; }

    public static string DocumentId(string ownerKey, string editionId) => $"LibraryEntries/{ownerKey}/{editionId}";
}