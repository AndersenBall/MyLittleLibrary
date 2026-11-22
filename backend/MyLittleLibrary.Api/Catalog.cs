using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

public sealed record CatalogEdition(string EditionId, string Title, string[] Authors,
    string[] Publishers, string? PublicationDate, string[] Isbn10, string[] Isbn13,
    string? CoverUrl, string EditionUrl, bool AlreadyAdded);

public sealed record EditionSnapshot(CatalogEdition Edition, string? WorkId);

public sealed class CatalogUnavailableException(Exception? inner = null)
    : Exception("Open Library is temporarily unavailable. Try again shortly.", inner);

public sealed partial class CatalogClient(HttpClient http, IMemoryCache cache, CatalogRateLimit rateLimit)
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    private const string Fields = "key,title,author_name,editions,editions.key,editions.title,editions.author_name,editions.isbn,editions.publisher,editions.publish_date,editions.cover_i";

    public async Task<CatalogEdition[]> SearchAsync(string query, CancellationToken ct)
    {
        var isbn = ParseIsbn(query);
        var providerQuery = isbn is null ? query : $"isbn:{isbn}";
        using var response = await GetAsync($"search.json?q={Uri.EscapeDataString(providerQuery)}&limit=12&fields={Fields}", ct);
        if (response is null || !response.RootElement.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
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
                if (id is null || title is null || (isbn is not null && !isbns.Contains(isbn)) || !seen.Add(id))
                    continue;

                var authors = Texts(doc, "author_name");
                if (authors.Length == 0) authors = Texts(work, "author_name");
                var edition = CreateEdition(id, title, authors, Texts(doc, "publisher"),
                    Texts(doc, "publish_date").FirstOrDefault(), isbns, PositiveInt(Property(doc, "cover_i")));
                var snapshot = new EditionSnapshot(edition, ReadKey(work, "/works/", WorkIdPattern()));
                // Keep an exact ISBN selection if a later title search omits edition fields.
                if (isbn is not null || !cache.TryGetValue<EditionSnapshot>($"edition:{id}", out _))
                    cache.Set($"edition:{id}", snapshot, SnapshotLifetime);
                results.Add(edition);
                if (results.Count == 12) return [.. results];
            }
        }
        return [.. results];
    }

    public async Task<EditionSnapshot?> ResolveAsync(string editionId, CancellationToken ct)
    {
        if (!EditionIdPattern().IsMatch(editionId)) return null;
        if (cache.TryGetValue<EditionSnapshot>($"edition:{editionId}", out var cached)) return cached;
        using var response = await GetAsync($"books/{editionId}.json", ct);
        if (response is null) return null;
        var doc = response.RootElement;
        if (ReadKey(doc, "/books/", EditionIdPattern()) != editionId || Text(doc, "title") is not { } title)
            throw new CatalogUnavailableException();

        var workId = Array(doc, "works").Select(w => ReadKey(w, "/works/", WorkIdPattern())).FirstOrDefault();
        var authorIds = Array(doc, "authors").Select(ReadAuthorKey).OfType<string>().Distinct().Take(8).ToArray();
        if (authorIds.Length == 0 && workId is not null)
        {
            using var work = await GetAsync($"works/{workId}.json", ct);
            if (work is not null)
                authorIds = Array(work.RootElement, "authors").Select(ReadAuthorKey).OfType<string>().Distinct().Take(8).ToArray();
        }
        var authors = new List<string>();
        foreach (var id in authorIds)
        {
            using var author = await GetAsync($"authors/{id}.json", ct);
            if (author is not null && Text(author.RootElement, "name") is { } name) authors.Add(name);
        }
        var edition = CreateEdition(editionId, title, [.. authors], Texts(doc, "publishers"), Text(doc, "publish_date"),
            Texts(doc, "isbn_10").Concat(Texts(doc, "isbn_13")).Select(ParseIsbn).OfType<string>().Distinct().ToArray(),
            Array(doc, "covers").Select(PositiveInt).FirstOrDefault(n => n is not null));
        var snapshot = new EditionSnapshot(edition, workId);
        cache.Set($"edition:{editionId}", snapshot, SnapshotLifetime);
        return snapshot;
    }

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
    private static string? ReadAuthorKey(JsonElement obj) =>
        ReadKey(Property(obj, "author"), "/authors/", AuthorIdPattern()) ?? ReadKey(obj, "/authors/", AuthorIdPattern());

    [GeneratedRegex("\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EditionIdPattern();
    [GeneratedRegex("\\AOL[0-9]{1,15}W\\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkIdPattern();
    [GeneratedRegex("\\AOL[0-9]{1,15}A\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorIdPattern();
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
