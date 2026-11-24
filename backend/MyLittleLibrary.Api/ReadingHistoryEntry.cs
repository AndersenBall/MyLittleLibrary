public sealed class ReadingHistoryEntry
{
    public required string OwnerKey { get; init; }
    public required string WorkId { get; init; }
    public required string EditionId { get; init; }

    public static string DocumentId(string ownerKey, string editionId) => $"ReadingHistoryEntries/{ownerKey}/{editionId}";
}

public sealed class MarkBookAsReadRequest
{
    public required string WorkId { get; init; }
    public required string EditionId { get; init; }
}