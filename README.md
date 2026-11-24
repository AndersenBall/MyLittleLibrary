# MyLittleLibrary

MyLittleLibrary is built with a modern web application stack:

- **React** provides the user-facing web application.
- **.NET** powers the application backend and its server-side functionality.
- **RavenDB** is the database used for application data.

Together, these technologies provide a React-based client, a .NET application layer, and RavenDB for data storage.

The API foundation is an ASP.NET Core Minimal API targeting .NET 10.

## Backend architecture

- **Keep [`Program.cs`](backend/MyLittleLibrary.Api/Program.cs) as the API map.** Define every route there, including its HTTP method, path, authorization, and OpenAPI metadata. Map it directly to a named handler. Keep middleware order visible, but put middleware implementation in its own file.
- **Put request handling and business logic in handlers.** Handlers validate inputs, enforce business rules, coordinate persistence and external calls, and return HTTP results. For example, `POST /api/library/books` maps directly to [`LibraryHandler.AddBook`](backend/MyLittleLibrary.Api/LibraryHandler.cs), which validates and saves the book.
- **Keep dependency injection wiring readable.** Put new application-specific registrations, factories, and options setup in a focused extension next to the dependency. Call the extension from `Program.cs` rather than defining the setup inline. Use [`AddRavenDb()`](backend/MyLittleLibrary.Api/RavenDbExtensions.cs) as the pattern; simple framework registrations can stay visible.
- **Share logic with a clear responsibility.** [`BookStore`](backend/MyLittleLibrary.Api/BookStore.cs) handles catalog imports for both the library and reading history. Don’t add service or repository layers just to pass calls through.
- **Keep each entity’s models and mappings together.** One file should show how an entity is stored, what the API accepts, what it returns, and how those representations map to each other. For example, [`LibraryBook.cs`](backend/MyLittleLibrary.Api/LibraryBook.cs) contains the RavenDB book document, book response DTOs, and mappings; [`LibraryEntry.cs`](backend/MyLittleLibrary.Api/LibraryEntry.cs) contains the shelf entry document and add request DTO. Follow the same pattern for other entities and their DTOs.

## Run locally with Docker

From the repository root:

```sh
docker compose -f compose.yaml -f compose.dev.yaml up --build
```

Open My Library at <http://localhost:18081> or go to [Add Books](http://localhost:18081/add) to search Open Library by title, author, or ISBN. The Vite dev server proxies `/health` and `/api` to the API. Swagger is at <http://localhost:18080/swagger>. Both host ports bind to loopback. Stop Compose with `Ctrl+C`.

Hot reload is enabled for development with the Compose command above: Vite reloads frontend source edits, and `dotnet watch` applies backend source edits or automatically restarts the API when a change requires it. The source directories are mounted into their containers, so ordinary code edits do not require rebuilding the images. Backend hot reload is provided by the opt-in `compose.dev.yaml` override.

The opt-in `compose.dev.yaml` uses one fixed server-side identity. Everyone with access to this private development instance uses the same dev shelf. Never expose this mode publicly. The API rejects `Auth__EnableLocalDevIdentity=true` in any environment other than `Local`. `/health` remains public.

The API creates the `MyLittleLibrary` RavenDB database on startup. RavenDB is reachable only on the project network and stores data in the `mylittlelibrary_ravendb-data` volume. Keep that volume when recreating containers. For a standalone API, set `RavenDb__Url` and `RavenDb__Database` to your RavenDB instance and database.

For Open Library's identified API rate limit, set `MYLITTLELIBRARY_OPEN_LIBRARY_CONTACT_EMAIL` for Compose (or `OpenLibrary__ContactEmail` for a standalone API). Without a valid address the app uses the lower anonymous limit. Cover art comes from Open Library; books without an available cover display a placeholder.

Search groups matching editions under their work. Expand a work and scroll to browse its editions one page at a time. An exact ISBN search automatically opens the matching result. Edition cards show an exact page count once the corresponding edition page loads, when Open Library provides one.

**Add to Library** and **Mark As Read** are independent actions on an edition. Both accept `workId` and `editionId` as JSON without requiring a prior search. On the first import, the API verifies the edition belongs to the work and saves `LibraryWorks/{workId}` and all `LibraryBooks/{editionId}` from Open Library in one transaction with the user's entry. `PageCount` is nullable and stored per edition. If any catalog page fails, nothing is saved. Later actions on imported editions reuse the stored books without calling Open Library or changing existing entries.

Shelf entries are stored at `LibraryEntries/{ownerKey}/{editionId}`. Read status is stored separately at `ReadingHistoryEntries/{ownerKey}/{editionId}`: the entry's presence means the user read that edition. There are no dates, ratings, or notes. Search and edition results show the signed-in user's own shelf and read status.

The full-work import model requires a fresh database. There is no migration from the previous edition-only model.

On `realtornow-jeb`, private Tailscale Serve maps [the React app](https://realtornow-jeb.tail69be98.ts.net:8445/) to `127.0.0.1:18081` and [Swagger](https://realtornow-jeb.tail69be98.ts.net:8444/swagger) to `127.0.0.1:18080`.

## Hosted authentication settings (future)

Auth0 integration is code only; no tenant is configured or exercised here. Without the Local identity, the API requires `Auth0__Domain` (hostname only) and `Auth0__Audience` at startup and expects RS256-signed access tokens. The hosted React build needs `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, and `VITE_AUTH0_AUDIENCE`. Set the same domain and API audience on both sides. The frontend uses the app origin for its callback and logout return URL and calls the API on that same origin. No client secret belongs in the browser.

For future standalone API development after hosted authentication is configured, `dotnet run --project backend/MyLittleLibrary.Api` uses `http://localhost:5127` by default; `/health` returns HTTP 200 when ready. For day-to-day development without Auth0, use the Compose command above instead.

## Frontend checks and API client

With Compose running, execute these from the repository root:

```sh
docker compose exec frontend npm run typecheck
docker compose exec frontend npm run build
docker compose exec frontend npm run api:generate
```

`api:generate` regenerates the checked-in Fetch client from `frontend/openapi.json`. When the API contract changes, run `docker compose exec frontend npm run api:refresh` to fetch the running API's Swagger document and regenerate the client. Commit the updated snapshot and generated files together.

## Backend integration tests

With the .NET 10 SDK, run from the repository root:

```sh
dotnet test backend/MyLittleLibrary.sln --configuration Release
```

This is the routine backend check locally and on pull requests targeting `main`. It runs the real API in-process, starts its own RavenDB server with a disposable database per scenario, and stubs Open Library at the HTTP handler. Ownership checks use a TestServer-only identity scheme; they do not run or mock Auth0. No Compose stack, development database, or Open Library access is required.
