using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

public sealed record EditionSnapshot(CatalogEdition Edition, string? WorkId, int? PublicationYear, string[] Series, string[] Tags);

public sealed class CatalogUnavailableException(Exception? inner = null)
    : Exception("Open Library is temporarily unavailable. Try again shortly.", inner);

public sealed partial class OpenLibraryClient(HttpClient http, IMemoryCache cache, CatalogRateLimit rateLimit)
{
    private static readonly object SnapshotGate = new();
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    private const string Fields = "key,title,author_name,subject,series_name,editions,editions.key,editions.title,editions.author_name,editions.isbn,editions.publisher,editions.publish_date,editions.cover_i";

    public async Task<CatalogEdition[]> SearchAsync(string query, CancellationToken ct)
    {
        var isbn = ParseIsbn(query);
        var providerQuery = isbn is null ? query : $"isbn:{isbn}";
        using var response = await GetAsync($"search.json?q={Uri.EscapeDataString(providerQuery)}&limit=12&fields={Fields}", ct);
        if (response is null || Property(response.RootElement, "docs") is not { ValueKind: JsonValueKind.Array } docs)
            throw new CatalogUnavailableException();

        var results = new List<CatalogEdition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var work in docs.EnumerateArray())
        {
            foreach (var doc in Array(work, "editions", "docs"))
            {
                var id = ReadKey(doc, "/books/", EditionIdPattern());
                var title = Text(doc, "title") ?? Text(work, "title");
                var isbns = Texts(doc, "isbn").Select(ParseIsbn).OfType<string>().Distinct().ToArray();
                if (id is null || title is null || !seen.Add(id))
                    continue;

                var authors = Texts(doc, "author_name");
                if (authors.Length == 0) authors = Texts(work, "author_name");
                var edition = CreateEdition(id, title, authors, Texts(doc, "publisher"),
                    Texts(doc, "publish_date").FirstOrDefault(), isbns, PositiveInt(Property(doc, "cover_i")));
                var workId = ReadKey(work, "/works/", WorkIdPattern());
                lock (SnapshotGate)
                {
                    if (cache.TryGetValue<EditionSnapshot>($"edition:{id}", out var previous) && previous is not null)
                    {
                        // Both snapshots identify this edition; keep ISBNs verified by either search.
                        edition = edition with
                        {
                            Isbn10 = edition.Isbn10.Union(previous.Edition.Isbn10).ToArray(),
                            Isbn13 = edition.Isbn13.Union(previous.Edition.Isbn13).ToArray()
                        };
                        workId ??= previous.WorkId;
                    }
                    cache.Set($"edition:{id}", new EditionSnapshot(edition, workId,
                        ParseYear(edition.PublicationDate), Texts(work, "series_name"), Texts(work, "subject")), SnapshotLifetime);
                }
                results.Add(edition);
                if (results.Count == 12) return [.. results];
            }
        }
        return [.. results];
    }

    public EditionSnapshot? FindSearchedEdition(string editionId) =>
        cache.Get<EditionSnapshot>($"edition:{editionId}");

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await rateLimit.AcquireAsync(deadline.Token);
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter;
                rateLimit.Throttle(retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5));
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new CatalogUnavailableException(ex);
        }
    }

    private static CatalogEdition CreateEdition(string id, string title, string[] authors, string[] publishers,
        string? date, string[] isbns, int? cover) => new(id, title, authors, publishers, date,
            isbns.Where(i => i.Length == 10).ToArray(), isbns.Where(i => i.Length == 13).ToArray(),
            cover is null ? null : $"https://covers.openlibrary.org/b/id/{cover}-M.jpg?default=false",
            $"https://openlibrary.org/books/{id}", false);

    private static string? ParseIsbn(string value)
    {
        var isbn = string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
        if (isbn.Length is not (10 or 13)) return null;
        if (isbn.Length == 13 && !(isbn.StartsWith("978", StringComparison.Ordinal) || isbn.StartsWith("979", StringComparison.Ordinal))) return null;
        var sum = 0;
        for (var i = 0; i < isbn.Length; i++)
        {
            var digit = isbn[i] is >= '0' and <= '9' ? isbn[i] - '0' : isbn.Length == 10 && i == 9 && isbn[i] == 'X' ? 10 : -1;
            if (digit < 0) return null;
            sum += digit * (isbn.Length == 10 ? 10 - i : i % 2 == 0 ? 1 : 3);
        }
        return sum % (isbn.Length == 10 ? 11 : 10) == 0 ? isbn : null;
    }

    private static JsonElement Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;
    private static string? AsText(JsonElement e) => e.ValueKind == JsonValueKind.String && e.GetString()?.Trim() is { Length: > 0 } text ? text : null;
    private static string? Text(JsonElement obj, string name) => AsText(Property(obj, name));
    private static int? PositiveInt(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) && i > 0 ? i : null;
    private static int? ParseYear(string? value) => value is null ? null :
        int.TryParse(YearPattern().Match(value).Value, out var year) && year > 0 ? year : null;
    private static IEnumerable<JsonElement> Array(JsonElement obj, params string[] path)
    {
        foreach (var name in path) obj = Property(obj, name);
        return obj.ValueKind == JsonValueKind.Array ? obj.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
    private static string[] Texts(JsonElement obj, string name) => Text(obj, name) is { } single
        ? [single] : Array(obj, name).Select(AsText).OfType<string>().Distinct().ToArray();
    private static string? ReadKey(JsonElement obj, string prefix, Regex pattern) =>
        Text(obj, "key") is { } key && key.StartsWith(prefix, StringComparison.Ordinal) && pattern.IsMatch(key[prefix.Length..])
            ? key[prefix.Length..] : null;
    [GeneratedRegex("\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EditionIdPattern();
    [GeneratedRegex("\\AOL[0-9]{1,15}W\\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkIdPattern();
    [GeneratedRegex("\\b[0-9]{4}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}

public sealed class CatalogRateLimit(bool hasContact)
{
    private readonly object gate = new();
    private readonly TimeSpan spacing = hasContact ? TimeSpan.FromMilliseconds(350) : TimeSpan.FromSeconds(1);
    private DateTimeOffset next;
    private DateTimeOffset throttledUntil;

    public async Task AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < throttledUntil) throw new CatalogUnavailableException();
                delay = next - now;
                if (delay <= TimeSpan.Zero)
                {
                    next = now + spacing;
                    return;
                }
            }
            await Task.Delay(delay, ct);
        }
    }

    public void Throttle(TimeSpan delay)
    {
        lock (gate)
        {
            var until = DateTimeOffset.UtcNow + (delay > spacing ? delay : spacing);
            if (until > throttledUntil) throttledUntil = until;
        }
    }
}
