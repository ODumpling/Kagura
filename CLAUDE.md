# Kagura

## Run

```bash
dotnet run --project src/Kagura.AppHost
```

The Aspire AppHost orchestrates `Kagura.Api` and the Vite frontend (`web/kagura-web`) together and injects `VITE_API` into Vite so the frontend always knows the API URL. The dashboard URL is printed on startup; API serves on `:5253`, web on `:5173`.

First time on a machine: `dotnet dev-certs https --trust`.

## Test

```bash
dotnet test
```

Runs the xUnit suite in `tests/Kagura.Tests`. There is no frontend test suite yet.
