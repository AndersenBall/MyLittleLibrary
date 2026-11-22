using System.Text.RegularExpressions;

public sealed class LibraryBook
{
    public const string CoverAttachmentName = "cover";
    public string EditionId { get; set; } = "";
    public string? WorkId { get; set; }
    public string Title { get; set; } = "";
    public string[] Authors { get; set; } = Array.Empty<string>();
    public string[] Publishers { get; set; } = Array.Empty<string>();
    public string? PublicationDate { get; set; }
    public string[] Isbn10 { get; set; } = Array.Empty<string>();
    public string[] Isbn13 { get; set; } = Array.Empty<string>();
    public string? CoverUrl { get; set; }
    public string EditionUrl { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    public bool HasLocalCover { get; set; }

    public static string DocumentId(string editionId) => "LibraryBooks/" + editionId;
    public static bool IsValidEditionId(string editionId) => Regex.IsMatch(editionId, "\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant);
    public static LibraryBook From(CatalogEdition edition, string? workId, bool hasLocalCover) => new()
    {
        EditionId = edition.EditionId, WorkId = workId, Title = edition.Title,
        Authors = edition.Authors ?? Array.Empty<string>(), Publishers = edition.Publishers ?? Array.Empty<string>(),
        PublicationDate = edition.PublicationDate, Isbn10 = edition.Isbn10 ?? Array.Empty<string>(),
        Isbn13 = edition.Isbn13 ?? Array.Empty<string>(), CoverUrl = edition.CoverUrl,
        EditionUrl = edition.EditionUrl, AddedAt = DateTimeOffset.UtcNow, HasLocalCover = hasLocalCover
    };
    public GalleryBook ToGallery() => new(EditionId, Title, Authors, HasLocalCover ? $"/api/library/books/{Uri.EscapeDataString(EditionId)}/cover" : CoverUrl, AddedAt);
}
