# Ops Dashboard (read-only)

Minimal ASP.NET Core Razor app that reads the engine `/health` and `/metrics` endpoints and displays the raw payloads for operators.

## Running locally

1. Set the engine base URL (required):

```bash
export DASHBOARD_ENGINE_BASE_URL=http://127.0.0.1:8080
```

2. Run the dashboard:

```bash
dotnet run --project tools/OpsDashboard
```

Then browse to the hosted URL (default `http://localhost:5000`).

If `DASHBOARD_ENGINE_BASE_URL` is missing, the page will show a warning and will not call the engine.

## SSH tunnel for VPS access

If the engine is only reachable on the VPS loopback:

```bash
ssh -L 8080:127.0.0.1:8080 <user>@<vps-host>
export DASHBOARD_ENGINE_BASE_URL=http://127.0.0.1:8080
dotnet run --project tools/OpsDashboard
```

This forwards the engine port to your local machine; the dashboard stays read-only.
