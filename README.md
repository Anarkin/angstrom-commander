# Angstrom Commander

All your machines, an ångström apart — a cloud-based dual-pane file manager with a Parsec-like model: a Daemon on each of your machines, one account managing them all from web or mobile, AI-operable from day one.

- [ARCHITECTURE.md](ARCHITECTURE.md) — domain, components, tech stack, security model, scaling, operating costs, diagrams, and [what is built so far](ARCHITECTURE.md#implementation-status)
- [AGENTS.md](AGENTS.md) — repo conventions and instructions for coding agents

## Setup

Prerequisite: Docker (dev machines typically run Rancher Desktop).

```sh
docker compose up --build
```

brings up PostgreSQL, the Server on <http://localhost:5080>, the WebClient on <http://localhost:5081>, and a Daemon that dials out to it. The Daemon shares the repo directory read-only as `/data` and a writable volume as `/uploads`; everything else on that machine, including the Daemon's own keypair, stays out of reach.

Open <http://localhost:5081>, create an account, pair the Daemon with the code from `docker compose logs daemon`, then point both panes at the machine to browse, download, upload, and copy files between paths.

The easiest way to try it: open [tools/Server.http](tools/Server.http) and run the requests top to bottom in your IDE (VS Code REST Client etc.) — the token and registration id chain between requests automatically.

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

# 5. Download a file (streamed through the relay; -OJ saves it under its real name)
curl -OJ -H "Authorization: Bearer <accessToken>" "http://localhost:5080/api/daemons/<registrationId>/download?path=/data/README.md"
```

For working on the code, the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`global.json` sets the floor and rolls forward to the newest 10.0 feature band installed) is enough: `dotnet test` builds everything and runs the test suites (integration tests start their own throwaway PostgreSQL via Testcontainers, so Docker must be running).

## Deploying to Azure

Environments are stamps: `azd env new <name>` + `azd provision` + `azd deploy` spawns a
full isolated environment (Server on Container Apps at `api.<name>.angstrom.adamlengyel.com`,
WebClient on Static Web Apps at `app.<name>.…`, its own managed PostgreSQL), and
`azd down` deletes it whole. Prerequisites, the one-time bootstrap that already
happened, and the one manual TLS step per new stamp are all in
[infra/README.md](infra/README.md). The `test` environment is live.

For the WebClient, Node.js 22+: `cd src/WebClient && npm ci && npm run dev` starts the dev server against the compose Server on <http://localhost:5080>; `npm test`, `npm run lint`, `npm run format:check`, and `npm run gen:api:check` mirror the CI gates. The Agent joins the compose setup once it exists; see ARCHITECTURE.md § Dev environment.
