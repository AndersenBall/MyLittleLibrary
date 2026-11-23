public static class BookScanHandler
{
    public static async Task<IResult> Scan(
        HttpRequest request,
        OpenAiBookScanner scanner,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType ||
            !request.ContentType!.StartsWith("multipart/form-data;", StringComparison.OrdinalIgnoreCase))
            return Results.Problem("Multipart form data is required.", statusCode: StatusCodes.Status415UnsupportedMediaType);

        IFormFileCollection files;
        try
        {
            files = (await request.ReadFormAsync(cancellationToken)).Files;
        }
        catch (InvalidDataException)
        {
            return Results.Problem("The multipart photo could not be read.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (files.Count != 1 || !string.Equals(files[0].Name, "photo", StringComparison.Ordinal))
            return Results.Problem("Supply exactly one image field named photo.", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var books = await scanner.ScanAsync(files[0], cancellationToken);
            return Results.Ok(new ScanBooksResponse(books));
        }
        catch (InvalidScanPhotoException error)
        {
            return Results.Problem(error.Message, statusCode: error.StatusCode, title: "Invalid photo");
        }
        catch (ScanBusyException)
        {
            request.HttpContext.Response.Headers.RetryAfter = "2";
            return Results.Problem(
                "Another scan is in progress. Please retry shortly.",
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Scan busy");
        }
        catch (ScanTimedOutException)
        {
            return Results.Problem(
                "The book scan took too long. Try a smaller or clearer photo.",
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Scan timed out");
        }
        catch (ModelUnavailableException)
        {
            return Results.Problem(
                "The book-recognition service is unavailable. Please retry later.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Model unavailable");
        }
    }
}
