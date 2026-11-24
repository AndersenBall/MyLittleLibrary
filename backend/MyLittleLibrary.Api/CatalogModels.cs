public sealed record CatalogWork(string WorkId, string Title, string[] Authors, int? FirstPublicationYear,
    CatalogEdition[] Editions);

public sealed record CatalogEdition(string EditionId, string Title,
    string[] Publishers, string? PublicationDate, int? PageCount, string[] Isbn10, string[] Isbn13,
    string? CoverUrl, string EditionUrl, bool AlreadyAdded, bool AlreadyRead = false);

public sealed record CatalogEditionPage(CatalogEdition[] Editions, int? NextOffset);
