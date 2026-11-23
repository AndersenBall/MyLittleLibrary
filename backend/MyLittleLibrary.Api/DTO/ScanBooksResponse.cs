public sealed record ScannedBook(string? Title, string? Author);

public sealed record ScanBooksResponse(IReadOnlyList<ScannedBook> Books);
