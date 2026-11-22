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

The API is available at `http://localhost:18080`; `GET /health` returns HTTP 200. The host port is 18080 to avoid conflicts with other local applications. Stop it with `Ctrl+C`.
