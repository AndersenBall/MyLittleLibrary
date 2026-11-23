# MyLittleLibrary

MyLittleLibrary is built with a modern web application stack:

- **React** provides the user-facing web application.
- **.NET** powers the application backend and its server-side functionality.
- **RavenDB** is the database used for application data.

Together, these technologies provide a React-based client, a .NET application layer, and RavenDB for data storage.

The API foundation is an ASP.NET Core Minimal API targeting .NET 10.

## Run locally with Docker

From the repository root:

```sh
docker compose -f compose.yaml -f compose.dev.yaml up --build
```

Open My Library at <http://localhost:18081> or go to [Add Books](http://localhost:18081/add) to search Open Library by title, author, or ISBN. The Vite dev server reloads frontend edits and proxies `/health` and `/api` to the API. Swagger is at <http://localhost:18080/swagger>. Both host ports bind to loopback. Stop Compose with `Ctrl+C`.

The opt-in `compose.dev.yaml` uses one fixed server-side identity. Everyone with access to this private development instance uses the same dev shelf. Never expose this mode publicly. The API rejects `Auth__EnableLocalDevIdentity=true` in any environment other than `Local`. `/health` remains public.

The API creates the `MyLittleLibrary` RavenDB database on startup. RavenDB is reachable only on the project network and stores data in the `mylittlelibrary_ravendb-data` volume. Keep that volume when recreating containers. For a standalone API, set `RavenDb__Url` and `RavenDb__Database` to your RavenDB instance and database.

For Open Library's identified API rate limit, set `MYLITTLELIBRARY_OPEN_LIBRARY_CONTACT_EMAIL` for Compose (or `OpenLibrary__ContactEmail` for a standalone API). Without a valid address the app uses the lower anonymous limit. Hosted cover art can be unavailable independently of the saved metadata. Upload a JPEG, PNG, or WebP up to 5 MiB only if you have permission to keep it; the original bytes are stored on your private shelf entry in RavenDB.

An edition missing from the shared catalog cache must come from a search within the last 15 minutes. If an add returns `404`, run the search again and retry. Each edition has one shared `LibraryBooks/{editionId}` document and each user can have one private `LibraryEntries/{ownerKey}/{editionId}` reference. Repeat adds return the caller's existing entry without changing its cover. Existing book documents and their old uploads are retained, but aren't assigned to anyone's shelf; add a book again to create a private entry. Legacy uploads aren't served.

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
