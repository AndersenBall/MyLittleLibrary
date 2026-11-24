using System.Text.RegularExpressions;

public sealed class LibraryWork
{
    public required string WorkId { get; init; }
    public required string Title { get; init; }
    public required string[] Authors { get; init; }
    public required string[] Subjects { get; init; }
    public required string[] Series { get; init; }
    public int? FirstPublicationYear { get; init; }

    public static string DocumentId(string workId) => "LibraryWorks/" + workId;
    public static bool IsValidWorkId(string workId) => Regex.IsMatch(workId, "\\AOL[0-9]{1,15}W\\z", RegexOptions.CultureInvariant);
}

public sealed record CatalogWork(string WorkId, string Title, string[] Authors, int? FirstPublicationYear,
    CatalogEdition[] Editions);

internal sealed record WorkImport(LibraryWork Work, CatalogEdition[] Editions);
