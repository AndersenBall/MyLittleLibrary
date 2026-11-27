public sealed record BookDetails(
    LibraryEdition Edition,
    LibraryBook Work,
    bool InLibrary,
    bool IsRead,
    decimal? Rating,
    string? ReviewComment);