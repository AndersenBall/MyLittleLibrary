using System.Text.RegularExpressions;

public sealed class LibraryBook
{
    public string EditionId { get; set; } = "";
    public string? WorkId { get; set; }
    public string Title { get; set; } = "";
    public string[] Authors { get; set; } = Array.Empty<string>();
    public string[] Publishers { get; set; } = Array.Empty<string>();
    public string? PublicationDate { get; set; }
    public string[] Isbn10 { get; set; } = Array.Empty<string>();
    public string[] Isbn13 { get; set; } = Array.Empty<string>();
    public int? PublicationYear { get; set; }
    public string[] Series { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? CoverUrl { get; set; }
    public string EditionUrl { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }

    public static string DocumentId(string editionId) => "LibraryBooks/" + editionId;
    public static bool IsValidEditionId(string editionId) => Regex.IsMatch(editionId, "\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant);
    public GalleryBook ToGallery(LibraryEntry entry) => new(EditionId, Title, Authors,
        entry.HasLocalCover ? $"/api/library/books/{EditionId}/cover" : CoverUrl, entry.AddedAt);

    public static LibraryBook From(EditionSnapshot snapshot, DateTimeOffset addedAt) => new()
    {
        EditionId = snapshot.Edition.EditionId, WorkId = snapshot.WorkId, Title = snapshot.Edition.Title,
        Authors = snapshot.Edition.Authors ?? Array.Empty<string>(), Publishers = snapshot.Edition.Publishers ?? Array.Empty<string>(),
        PublicationDate = snapshot.Edition.PublicationDate, Isbn10 = snapshot.Edition.Isbn10 ?? Array.Empty<string>(),
        Isbn13 = snapshot.Edition.Isbn13 ?? Array.Empty<string>(), CoverUrl = snapshot.Edition.CoverUrl,
        PublicationYear = snapshot.PublicationYear, Series = snapshot.Series, Tags = snapshot.Tags,
        EditionUrl = snapshot.Edition.EditionUrl, AddedAt = addedAt
    };
}

public sealed record GalleryBook(string EditionId, string Title, string[] Authors, string? CoverUrl, DateTimeOffset AddedAt);

public sealed record CatalogEdition(string EditionId, string Title, string[] Authors,
    string[] Publishers, string? PublicationDate, string[] Isbn10, string[] Isbn13,
    string? CoverUrl, string EditionUrl, bool AlreadyAdded);
