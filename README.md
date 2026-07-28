# Angstrom Commander

All your machines, an ångström apart — a cloud-based dual-pane file manager with a Parsec-like model: a Daemon on each of your machines, one account managing them all from web or mobile, AI-operable from day one.

- [ARCHITECTURE.md](ARCHITECTURE.md) — domain, components, tech stack, security model, diagrams
- [AGENTS.md](AGENTS.md) — repo conventions and instructions for coding agents

## Setup

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download) (exact version pinned in `global.json`).

```sh
dotnet test
```

builds the Server and Daemon and runs their test suites — that's everything runnable so far. Per the plan, setup will eventually center on a single `docker compose up` that brings up the Server (+ its PostgreSQL), the WebClient, and the Agent; see ARCHITECTURE.md § Dev environment.
