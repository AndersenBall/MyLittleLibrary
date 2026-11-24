using System.Text.RegularExpressions;

public sealed class LibraryEdition
{
    public required string EditionId { get; init; }
    public required string WorkId { get; init; }
    public required string Title { get; init; }
    public required string[] Publishers { get; init; }
    public string? PublicationDate { get; init; }
    public int? PageCount { get; init; }
    public required string[] Isbn10 { get; init; }
    public required string[] Isbn13 { get; init; }
    public string? CoverUrl { get; init; }

    public static string DocumentId(string editionId) => "LibraryEditions/" + editionId;
    public static bool IsValidEditionId(string editionId) => Regex.IsMatch(editionId, "\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant);
    public GalleryBook ToGallery(LibraryEntry entry, LibraryBook work) => new(EditionId, Title, work.Authors, CoverUrl, entry.AddedAt);

    public static LibraryEdition From(CatalogEdition edition, string workId) => new()
    {
        EditionId = edition.EditionId, WorkId = workId, Title = edition.Title,
        Publishers = edition.Publishers, PublicationDate = edition.PublicationDate, PageCount = edition.PageCount,
        Isbn10 = edition.Isbn10, Isbn13 = edition.Isbn13, CoverUrl = edition.CoverUrl
    };
}

public sealed record GalleryBook(string EditionId, string Title, string[] Authors, string? CoverUrl, DateTimeOffset AddedAt);
