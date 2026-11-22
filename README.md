# MyLittleLibrary

MyLittleLibrary is built with a modern web application stack:

- **React** provides the user-facing web application.
- **.NET** powers the application backend and its server-side functionality.
- **RavenDB** is the database used for application data.

Together, these technologies provide a React-based client, a .NET application layer, and RavenDB for data storage.

The API foundation is an ASP.NET Core Minimal API targeting .NET 10.

## Run locally

From the repository root:

```sh
dotnet run --project backend/MyLittleLibrary.Api
```

The API listens on `http://localhost:5127` by default. Check `http://localhost:5127/health` for HTTP 200.

## Run with Docker

From the repository root:

```sh
docker compose up --build
```

Open My Library at <http://localhost:18081> or go to [Add Books](http://localhost:18081/add) to search Open Library by title, author, or ISBN. The Vite dev server reloads frontend edits and proxies `/health` and `/api` to the API. Swagger is at <http://localhost:18080/swagger>. Both host ports bind to loopback. Stop Compose with `Ctrl+C`.

The API creates the `MyLittleLibrary` RavenDB database on startup. RavenDB is reachable only on the project network and stores data in the `mylittlelibrary_ravendb-data` volume. Keep that volume when recreating containers. For a standalone API, set `RavenDb__Url` and `RavenDb__Database` to your RavenDB instance and database.

For Open Library's identified API rate limit, set `MYLITTLELIBRARY_OPEN_LIBRARY_CONTACT_EMAIL` for Compose (or `OpenLibrary__ContactEmail` for a standalone API). Without a valid address the app uses the lower anonymous limit. The shelf is shared by everyone with access to the app; there is no sign-in. Hosted cover art can be unavailable independently of the saved metadata. Upload a JPEG, PNG, or WebP up to 5 MiB only if you have permission to keep it; the app stores the original bytes in RavenDB.

On `realtornow-jeb`, private Tailscale Serve maps [the React app](https://realtornow-jeb.tail69be98.ts.net:8445/) to `127.0.0.1:18081` and [Swagger](https://realtornow-jeb.tail69be98.ts.net:8444/swagger) to `127.0.0.1:18080`.

## Frontend checks and API client

With Compose running, execute these from the repository root:

```sh
docker compose exec frontend npm run typecheck
docker compose exec frontend npm run build
docker compose exec frontend npm run api:generate
```

`api:generate` regenerates the checked-in Fetch client from `frontend/openapi.json`. When the API contract changes, run `docker compose exec frontend npm run api:refresh` to fetch the running API's Swagger document and regenerate the client. Commit the updated snapshot and generated files together.

## Shelf smoke test

Run this only in an **isolated Compose project with a fresh, disposable RavenDB volume**. Never recreate the live RavenDB for this test. Save this test-only override as `compose.smoke.local.yaml` (do not commit it):

```yaml
services:
  api:
    container_name: !reset null
    ports: !override ["127.0.0.1:18082:8080"]
  frontend:
    ports: !override ["127.0.0.1:18084:5173"]
networks:
  mylittlelibrary-network:
    name: !reset null
```

Use the same disposable project throughout; choose a new project name for a fresh run. Start RavenDB first and wait for it to be healthy before starting the API.

```sh
smoke() { docker compose -p mylittlelibrary-smoke -f compose.yaml -f compose.smoke.local.yaml "$@"; }
smoke config --quiet
smoke up -d --wait ravendb
smoke up -d --build api
# Wait for http://127.0.0.1:18082/health to return 200:
python3 tests/smoke.py seed 18082
smoke restart api
# Wait for /health again, then use the edition ID and query printed by seed:
python3 tests/smoke.py cache-miss 18082 --edition OL... --query 'The Hobbit'

# Recreate only the disposable RavenDB container; keep its volume.
smoke up -d --force-recreate --no-deps --wait ravendb
smoke run -d --no-deps -p 127.0.0.1:18083:8080 \
  -e HTTPS_PROXY=http://127.0.0.1:9 -e NO_PROXY=ravendb api
# Wait for http://127.0.0.1:18083/health to return 200:
python3 tests/smoke.py retained 18083
smoke down --volumes
```

New editions must come from a search within the last 15 minutes. If an add returns `404`, run the search again and retry. Repeat adds return the original saved item without requiring a fresh search or a working catalog.
