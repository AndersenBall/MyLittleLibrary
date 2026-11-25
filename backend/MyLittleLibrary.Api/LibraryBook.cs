using System.Text.RegularExpressions;

public sealed class LibraryBook
{
    public required string WorkId { get; init; }
    public required string Title { get; init; }
    public required string[] Authors { get; init; }
    public required string[] Subjects { get; init; }
    public required string[] Series { get; init; }
    public int? FirstPublicationYear { get; init; }
    public string? Description { get; init; }

    public static string DocumentId(string workId) => "LibraryBooks/" + workId;
    public static bool IsValidWorkId(string workId) => Regex.IsMatch(workId, "\\AOL[0-9]{1,15}W\\z", RegexOptions.CultureInvariant);
}

internal sealed record WorkImport(LibraryBook Work, CatalogEdition[] Editions);
