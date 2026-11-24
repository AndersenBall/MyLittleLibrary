public sealed class LibraryEntry
{
    public required string OwnerKey { get; init; }
    public required string BookId { get; init; }
    public DateTimeOffset AddedAt { get; init; }

    public static string DocumentId(string ownerKey, string editionId) => $"LibraryEntries/{ownerKey}/{editionId}";
}

public sealed class AddLibraryBookRequest
{
    public required string WorkId { get; init; }
    public required string EditionId { get; init; }
}