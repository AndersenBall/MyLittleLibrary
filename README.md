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

Open the React app at <http://localhost:18081>. The Vite dev server reloads frontend edits and proxies `/health` to the API over the Compose network. The API is also available directly at <http://localhost:18080>, with Swagger at <http://localhost:18080/swagger>. Both ports bind to loopback. Stop Compose with `Ctrl+C`.

On `realtornow-jeb`, private Tailscale Serve maps [the React app](https://realtornow-jeb.tail69be98.ts.net:8445/) to `127.0.0.1:18081` and [Swagger](https://realtornow-jeb.tail69be98.ts.net:8444/swagger) to `127.0.0.1:18080`.

## Frontend checks and API client

With Compose running, execute these from the repository root:

```sh
docker compose exec frontend npm run typecheck
docker compose exec frontend npm run build
docker compose exec frontend npm run api:generate
```

`api:generate` regenerates the checked-in Fetch client from `frontend/openapi.json`. When the API contract changes, run `docker compose exec frontend npm run api:refresh` to fetch the running API's Swagger document and regenerate the client. Commit the updated snapshot and generated files together.
