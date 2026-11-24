using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class CatalogUnavailableException(Exception? inner = null)
    : Exception("Open Library is temporarily unavailable. Try again shortly.", inner);

public sealed partial class OpenLibraryClient(HttpClient http, CatalogRateLimit rateLimit)
{
    private const string SearchFields = "key,title,author_name,first_publish_year,editions,editions.key,editions.title,editions.isbn,editions.publisher,editions.publish_date,editions.cover_i,editions.language";
    private const string SearchLanguageFilter = "language:eng";
    private const string WorkMetadataFields = "key,author_name,subject,series_name,first_publish_year";

    public async Task<CatalogWork[]> SearchAsync(string query, CancellationToken ct)
    {
        var isbn = ParseIsbn(query);
        var providerQuery = $"{(isbn is null ? query : $"isbn:{isbn}")} {SearchLanguageFilter}";
        using var response = await SearchDocumentsAsync(providerQuery, limit: 12, SearchFields, ct);
        var docs = response.RootElement.GetProperty("docs");

        var results = new List<CatalogWork>();

        foreach (var work in docs.EnumerateArray())
        {
            if (ReadKey(work, "/works/", WorkIdPattern()) is not { } workId || Text(work, "title") is not { } title)
                continue;
            var editions = new Dictionary<string, CatalogEdition>(StringComparer.Ordinal);
            foreach (var doc in Array(work, "editions", "docs"))
            {
                if (!IsEnglishEdition(doc) || ReadKey(doc, "/books/", EditionIdPattern()) is not { } id) continue;
                editions.TryAdd(id, CreateEdition(id, Text(doc, "title") ?? title, Texts(doc, "publisher"),
                    Texts(doc, "publish_date").FirstOrDefault(), Texts(doc, "isbn"), PositiveInt(Property(doc, "cover_i"))));
            }
            if (editions.Count > 0)
                results.Add(new(workId, title, Texts(work, "author_name"),
                    PositiveInt(Property(work, "first_publish_year")), [.. editions.Values]));
        }
        return [.. results];
    }

    internal async Task<CatalogEditionPage?> BrowseEditionPageAsync(string workId, int offset, CancellationToken ct)
    {
        if (await FetchEditionPageAsync(workId, offset, limit: 25, ct) is not { } page) return null;
        var (total, editions, englishIds) = page;
        var next = offset + editions.Length;
        return new(editions.Where(e => englishIds.Contains(e.EditionId)).ToArray(), next < total ? next : null);
    }

    internal async Task<WorkImport?> FetchWorkAsync(string workId, CancellationToken ct)
    {
        var key = $"/works/{workId}";
        using var response = await GetAsync($"works/{workId}.json", ct);
        if (response is null) return null;
        if (Text(response.RootElement, "key") != key ||
            Text(response.RootElement, "title") is not { } title)
            throw new CatalogUnavailableException();
        var details = response.RootElement;
        // The work document links to authors by ID; one exact-key projection resolves display names.
        using var projection = await SearchDocumentsAsync($"key:{key}", limit: 1, WorkMetadataFields, ct);
        var metadata = projection.RootElement.GetProperty("docs").EnumerateArray()
            .FirstOrDefault(doc => Text(doc, "key") == key);
        var work = new LibraryWork
        {
            WorkId = workId,
            Title = title,
            Authors = Texts(metadata, "author_name"),
            Subjects = Texts(details, "subjects").Union(Texts(metadata, "subject")).ToArray(),
            Series = Texts(details, "series").Union(Texts(metadata, "series_name")).ToArray(),
            FirstPublicationYear = ParseYear(Text(details, "first_publish_date")) ?? PositiveInt(Property(metadata, "first_publish_year"))
        };
        return new(work, await FetchAllEditionsAsync(workId, ct));
    }

    private async Task<JsonDocument> SearchDocumentsAsync(string query, int limit, string fields, CancellationToken ct)
    {
        var response = await GetAsync($"search.json?q={Uri.EscapeDataString(query)}&limit={limit}&fields={fields}", ct);
        if (response is not null && Property(response.RootElement, "docs").ValueKind == JsonValueKind.Array)
            return response;
        response?.Dispose();
        throw new CatalogUnavailableException();
    }

    private async Task<CatalogEdition[]> FetchAllEditionsAsync(string workId, CancellationToken ct)
    {
        var editions = new Dictionary<string, CatalogEdition>(StringComparer.Ordinal);
        int? total = null;
        for (var offset = 0; total is null || offset < total;)
        {
            var (count, page, _) = await FetchEditionPageAsync(workId, offset, limit: 100, ct) ?? throw new CatalogUnavailableException();
            if (total is not null && count != total) throw new CatalogUnavailableException();
            total = count;
            foreach (var edition in page)
                if (!editions.TryAdd(edition.EditionId, edition))
                    throw new CatalogUnavailableException();
            offset += page.Length;
        }
        return [.. editions.Values];
    }

    private async Task<(int Total, CatalogEdition[] Editions, HashSet<string> EnglishIds)?> FetchEditionPageAsync(string workId, int offset, int limit, CancellationToken ct)
    {
        using var response = await GetAsync($"works/{workId}/editions.json?limit={limit}&offset={offset}", ct);
        if (response is null) return null;
        if (Property(response.RootElement, "size") is not { ValueKind: JsonValueKind.Number } size ||
            !size.TryGetInt32(out var count) || count < 0 ||
            Property(response.RootElement, "entries") is not { ValueKind: JsonValueKind.Array } page ||
            page.GetArrayLength() > Math.Min(limit, Math.Max(0, count - offset)) || (page.GetArrayLength() == 0 && offset < count))
            throw new CatalogUnavailableException();

        var editions = new Dictionary<string, CatalogEdition>(StringComparer.Ordinal);
        var englishIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in page.EnumerateArray())
        {
            if (ReadKey(doc, "/books/", EditionIdPattern()) is not { } id || editions.ContainsKey(id))
                throw new CatalogUnavailableException();
            if (Array(doc, "languages").Any(language => Text(language, "key") == "/languages/eng"))
                englishIds.Add(id);
            editions.Add(id, CreateEdition(id, Text(doc, "title") ?? "Untitled edition", Texts(doc, "publishers"),
                Text(doc, "publish_date"),
                Texts(doc, "isbn_10").Concat(Texts(doc, "isbn_13")),
                Array(doc, "covers").Select(PositiveInt).FirstOrDefault(x => x.HasValue)));
        }
        return (count, [.. editions.Values], englishIds);
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

    private static CatalogEdition CreateEdition(string id, string title, string[] publishers,
        string? date, IEnumerable<string> values, int? cover)
    {
        var isbns = values.Select(ParseIsbn).OfType<string>().Distinct().ToArray();
        return new(id, title, publishers, date,
            isbns.Where(i => i.Length == 10).ToArray(), isbns.Where(i => i.Length == 13).ToArray(),
            cover is null ? null : $"https://covers.openlibrary.org/b/id/{cover}-M.jpg?default=false",
            $"https://openlibrary.org/books/{id}", false);
    }

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
    private static bool IsEnglishEdition(JsonElement edition) => Texts(edition, "language")
        .Any(language => language is "eng" or "/languages/eng");
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
