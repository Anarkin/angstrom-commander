# Angstrom Commander

All your machines, an ångström apart — a cloud-based dual-pane file manager with a Parsec-like model: a Daemon on each of your machines, one account managing them all from web or mobile, AI-operable from day one.

- [ARCHITECTURE.md](ARCHITECTURE.md) — domain, components, tech stack, security model, diagrams
- [AGENTS.md](AGENTS.md) — repo conventions and instructions for coding agents

## Setup

Prerequisite: Docker (dev machines typically run Rancher Desktop).

```sh
docker compose up --build
```

brings up PostgreSQL, the Server on <http://localhost:5080>, and a Daemon that dials out to it, exposing the repo directory read-only as `/data`.

The easiest way to try it: open [tools/api-console.html](tools/api-console.html) in a browser — register, log in, claim the pairing code from `docker compose logs daemon`, and browse the machine's files, all point-and-click.

The same flow with curl (TV-style pairing: the Daemon shows a code, you claim it):

```sh
# 1. Create an account and log in (grab accessToken from the response)
curl -X POST http://localhost:5080/api/auth/register -H "Content-Type: application/json" -d '{"email":"you@example.com","password":"Sup3rSecret!"}'
curl -X POST http://localhost:5080/api/auth/login    -H "Content-Type: application/json" -d '{"email":"you@example.com","password":"Sup3rSecret!"}'

# 2. Find the pairing code the Daemon printed
docker compose logs daemon | grep "PAIRING CODE"

# 3. Claim it (grab registrationId from the response)
curl -X POST http://localhost:5080/api/enrollment/claim -H "Authorization: Bearer <accessToken>" -H "Content-Type: application/json" -d '{"code":"<CODE>","displayName":"Compose Daemon"}'

# 4. Your machines, and their files through the relay
curl -H "Authorization: Bearer <accessToken>" "http://localhost:5080/api/daemons"
curl -H "Authorization: Bearer <accessToken>" "http://localhost:5080/api/daemons/<registrationId>/list?path=/data"
```

For working on the code, the [.NET 10 SDK](https://dotnet.microsoft.com/download) (exact version pinned in `global.json`) is enough: `dotnet test` builds everything and runs the test suites (integration tests start their own throwaway PostgreSQL via Testcontainers, so Docker must be running). The WebClient and the Agent join the compose setup as they come to exist; see ARCHITECTURE.md § Dev environment.
