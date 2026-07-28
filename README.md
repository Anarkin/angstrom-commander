# Angstrom Commander

All your machines, an ångström apart — a cloud-based dual-pane file manager with a Parsec-like model: a Daemon on each of your machines, one account managing them all from web or mobile, AI-operable from day one.

- [ARCHITECTURE.md](ARCHITECTURE.md) — domain, components, tech stack, security model, diagrams
- [AGENTS.md](AGENTS.md) — repo conventions and instructions for coding agents

## Setup

Prerequisite: Docker (dev machines typically run Rancher Desktop).

```sh
docker compose up --build
```

brings up the Server on <http://localhost:5080> and a Daemon that dials out to it, exposing the repo directory read-only as `/data`. Exercise the relay:

```sh
curl "http://localhost:5080/api/daemons/compose-daemon/list?path=/data"
```

For working on the code, the [.NET 10 SDK](https://dotnet.microsoft.com/download) (exact version pinned in `global.json`) is enough: `dotnet test` builds everything and runs the test suites. PostgreSQL, the WebClient, and the Agent join the compose setup as they come to exist; see ARCHITECTURE.md § Dev environment.
