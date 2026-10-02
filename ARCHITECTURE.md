# Angstrom Commander — architecture

The what and why of the system: domain, components, tech stack, guardrails, security model, observability, scaling, hosting, CI/CD, and diagrams. Working conventions live in [AGENTS.md](AGENTS.md); setup in [README.md](README.md).

## Domain

- Dual-pane file manager (Total Commander clone)
- Parsec-like model: a Daemon runs on each of the user's machines; one user account manages multiple machines from WebClient, MobileClient, or any MCP client the user connects
- Cloud-based from day one — no VPN/LAN-only setup, no port forwarding expected from users
- AI-operable from day one: natural-language file tasks via the built-in assistant (Agent), or via any MCP client the user brings (Claude Code etc.)

## System components

The five products we build and ship (infrastructure like the DB is listed with its owning component):

1. **Daemon** — installed per machine; exposes that machine's file system over a secure channel. Runs on Windows, macOS, Linux, and inside Linux containers. User-installed, never cloud-hosted.
2. **Server** — user accounts, Daemon registry, connection brokering/relay, and the MCP endpoint (the file-op tool surface for AI clients). Backed by PostgreSQL (its only persistent store).
3. **WebClient** — dual-pane UI in the browser, incl. the AI chat pane.
4. **MobileClient** — iPhone + Android.
5. **Agent** — the built-in AI assistant's agent loop. Cloud-hosted, never on the user's machines.

Besides the products, the repo carries supporting codebases — authored and reviewed like code, but not shipped to anyone: `.github/workflows/` (GitHub Actions CI/CD), and `infra/` (Terraform + azd definitions of the Azure environments) once it is written.

## Tech stack

- **Daemon + Server + Agent**: .NET, latest LTS (currently .NET 10); xUnit v3 for tests (its analyzers are part of the guardrail set — notably the one that requires a real `CancellationToken` on every awaited call, so a hung integration test can actually be cancelled; and `Assert.Skip` in the box, so a test whose environment cannot run it skips visibly instead of returning early and passing vacuously)
  - Server additionally: **managed** PostgreSQL + EF Core (migrations; envs self-initialize schema when `Database:MigrateOnStartup` is set). Managed for anything holding real data — the schema is small but relational, transactional and security-sensitive (Identity, pairing claims, sessions), so a key-value or blob store would trade a few dollars for hand-rolled Identity stores and no multi-row atomicity. Throwaway stamps run Postgres in a container, since their data is disposable; ASP.NET Core Identity for user accounts (self-managed email+password; social logins deferred — offering any would trigger the mandatory Apple sign-in rule on iOS); OpenIddict for the OAuth 2.1 authorization server
- **WebClient**: latest React, TypeScript (strict); Vitest + React Testing Library for tests
- **MobileClient**: React Native (iOS + Android), TypeScript (strict); Jest + React Native Testing Library for tests (RN's supported runner — Vitest doesn't fit RN)

## Guardrails

Heavy emphasis on guardrails across the whole repo: every cheap, automatable quality gate is turned on from day one, and everything is enforced in CI on every PR — a guardrail that isn't enforced doesn't exist. Tests everywhere: every component has a test setup from its first commit, and new code is expected to come with tests.

**Daemon + Server + Agent (.NET)**
- `TreatWarningsAsErrors` + `AnalysisLevel: latest-all` (.NET analyzers) + `EnforceCodeStyleInBuild`, centralized in a root `Directory.Build.props`
- Nullable reference types enabled everywhere
- `.editorconfig` at repo root as the single source of style truth (full C# naming rules, EF Core `Migrations/` exempted as generated code); `dotnet format` verified in CI
- Code coverage collected on every test run

**WebClient + MobileClient**
- TypeScript strict mode (no `any` escapes; `@typescript-eslint` recommended-type-checked rules)
- ESLint + Prettier, enforced in CI (no warnings allowed, formatting is a build failure)

**Containers**
- Server and Daemon images run as a non-root user; the mount points a Daemon writes to are created with that ownership so its volumes stay writable

**infra/** (once it exists)
- `terraform fmt` + `terraform validate` on every PR

**Later (noted so they're not forgotten)**
- E2E: Playwright (WebClient), Maestro or Detox (MobileClient)

## Architecture

Diagrams: see the Diagrams section at the bottom.

**Daemon**
- Has no inbound API beyond a health probe, and never will: nothing may connect *to* a Daemon, which is what removes NAT, firewall and port-forwarding setup from the user's life. It is an ASP.NET Core host for its own lifecycle and health endpoint, and a SignalR **client** for everything else
- Dials out to the Server, holds that one connection open, and answers the file operations pushed down it: list, stat, copy/move/delete/rename, mkdir, streamed up/download
- Real-time travels the same way — directory change notifications and progress/cancel for long operations over the same connection, not a second channel
- Every client-supplied path is sandboxed to the allowed roots before anything touches the file system (see § Security model)

**Server**
- Relays traffic between clients (WebClient, MobileClient, MCP clients incl. the Agent) and Daemons over the Daemons' outbound connections:
  - Each Daemon holds one persistent outbound WebSocket (SignalR, port 443) open to the Server; the Server routes client requests down it and matches responses back. Reconnection never gives up — a Server deploy outlasts any bounded retry, and a Daemon that stopped trying would stay unreachable until someone restarted it — and backs off with jitter so a restart does not bring the whole fleet back at once
  - Correlation IDs multiplex many concurrent operations over the one socket
  - Works through NAT/firewalls because an established TCP connection is bidirectional regardless of who initiated it
  - Socket ownership is the Server's only per-replica state: with multiple replicas, a connection registry (Daemon → owning replica, in Redis) lets any replica find the one holding that Daemon's socket and forward the whole request there. Requests must follow the socket rather than messages crossing a bus, because a transfer's bounded channel lives in the owning replica's memory — Redis stays a small directory and never carries file bytes. A SignalR Redis backplane is not the answer here: it addresses fan-out (groups, users, all clients), still requires sticky sessions, and does nothing about in-process transfer channels. Azure SignalR Service is the alternative worth pricing when that time comes, since it would terminate the Daemon sockets itself. v1 runs one replica with an in-process map, but socket lookup sits behind an interface from the first commit so the registry is a swap, not surgery
  - File transfers stream through in chunks (never buffered whole); bulk transfers may get a second dedicated connection later
  - Every transfer is one bounded channel with a single writer and reader, which is what makes the dual pane's copy cheap: source Daemon → Server → target Daemon with no client round trip. A download is that channel read by the HTTP response, an upload is it written by the HTTP request. Backpressure is inherent — a slow reader slows the sender. (Note SignalR cannot push a stream to a client, so a receiving Daemon is told a transfer is waiting and pulls it.) Buffers make transfers a memory cost, so each user gets a ceiling on how many they may hold open — plus a daily byte quota and a bandwidth cap (relayed bytes are egress bytes, so these bound cost as well as memory) — and either end going away fails the transfer rather than leaving the other waiting
- MCP endpoint — the file-op tool surface exposed as a remote MCP server (Streamable HTTP, e.g. `api.<domain>.com/mcp`):
  - Same authorized, relayed, sandboxed ops as the Server's REST API — MCP is a protocol adapter over one tool surface, never a second implementation
  - Auth: OAuth 2.1 (authorization code + PKCE, dynamic client registration; OpenIddict) with Angstrom Commander login + consent pages; personal access tokens as the simpler first step. Both land in the sessions table (see the DB bullets below)
  - Consequence: any MCP client (Claude Code, Claude Desktop, ChatGPT, …) can operate the user's machines using the user's own AI subscription — third-party agents get no special access path
- DB stores coordination metadata only:
  - Daemon registrations: display name, Daemon public key, platform/OS/version, created / last_seen / revoked timestamps
  - Sessions — WebClient/MobileClient logins, PATs, and OAuth grants for MCP clients: hashed token, device/client info, scopes, timestamps + revocation (enables an "active sessions" page covering humans and AI alike)
  - Short-lived one-time enrollment (pairing) codes
- NOT in the DB: file data/metadata, the Daemon's FS config (allowed roots, and whether each is writable, are Daemon-side), and ephemeral state (Daemon online-status, active relay sessions live in memory/Redis)
- Reached via fixed DNS name (e.g. `api.<domain>.com`); environments = subdomains (`api.qa...`); feature-branch demos use Azure's auto-generated Container Apps URLs
- The WebClient is a different origin from the API (`app.` vs `api.`), so each environment names its WebClient origin in `Cors:AllowedOrigins` — empty by default, so an environment that has not been told refuses browsers rather than accepting any origin. Behind Container Apps' ingress the Server trusts `X-Forwarded-For`/`-Proto`, without which every caller would look like the proxy and share one rate-limit bucket

**WebClient & MobileClient**
- Configurable server URL (build config in WebClient, build flavor/hidden setting in MobileClient) so any build can target any environment

**Agent**
- Feature: user asks in natural language ("organize my Downloads by file type") in the WebClient/MobileClient chat pane and an LLM performs the file operations; a curated model picker chooses the LLM
- LLM: **Foundry Models** (Azure's model-as-a-service) — serverless pay-per-token inference, one endpoint fronting many models (Claude, GPT, …), so the model is a request parameter; resource lives in the environment's stamp
- Loop: the **Agent** (container on Foundry Agent Service) runs the tool-calling loop — model emits tool calls, the Agent executes them **as an MCP client of the Server's MCP endpoint** with a user-scoped token, feeds results back until the model finishes. The Agent is architecturally just another client: no privileged path, Daemon unchanged
- Safety: Daemon path sandboxing bounds the AI exactly like any client; the chat pane requires user confirmation before the Agent executes mutating ops, and the Agent offers the model no delete tool in v1 — the endpoint's `delete_entry` stays available to MCP clients the user drives directly, where that client's own confirmation UX gates it; built-in usage burns our Azure tokens → per-user usage limits ship with the feature, not after
- Build order inside v1 (each step ships on its own, none is rework): core file manager → MCP endpoint with PATs (third-party agents work from here) → OAuth + consent → Agent + model picker
- Chat routing: WebClient/MobileClient never talk to the Agent directly — chat goes to the Server (existing JWT auth, single public origin), which forwards the session to the Agent and streams responses/confirmations back over the client's already-open SignalR connection. The Agent's endpoint is never publicly exposed
- Fallback: the Agent is a plain container speaking MCP — if Foundry Agent Service disappoints (GA'd mid-2026), it runs on Container Apps instead with an infra-only change

## Implementation status

Where the code actually is, as of August 2026 — a living list, so update it when a row moves.

**Working end to end** (locally, via `docker compose up`; verified with tests and by hand):

- Guardrails and CI from the first commit: warnings-as-errors, analyzers, formatting and type-checked lint gates for both .NET and the WebClient, tests with coverage on every push, and a compose smoke test that builds all three images and waits for the Daemon to reach the Server
- The relay: each Daemon holds one outbound SignalR connection and reconnects indefinitely; the Server routes requests down it. Socket lookup already sits behind `IDaemonConnectionRegistry`
- Accounts (ASP.NET Core Identity, JWT), TV-style enrollment (pairing code → user claims it → registration, claimed atomically), and Daemon connection tokens earned by signing a server challenge with the machine's private key
- Sign-in defences: account lockout, per-caller rate limits on every anonymous endpoint, no enumerable difference between a wrong password and an unknown account, and no signing key with a default
- File operations: list, streamed download, streamed upload, machine-to-machine copy, and the mutations — mkdir, move/rename, recursive delete (writable roots only, acting on a link itself rather than its target, and never on a shared root itself). Transfers are chunked through one bounded channel, never buffered whole, and every deadline in the path is a *stall* deadline rather than a duration cap: the HTTP legs time out a chunk that does not move, and a copy — whose two ends are both Daemons, with no HTTP pump watching — times out only after a whole window with no bytes at all. (A duration cap was the earlier bug: it failed every transfer slower than one window, and because the cleanup path ended the channel cleanly the receiving Daemon read the truncation as a complete file.) A receiving Daemon streams to a temporary file in the destination's own directory and renames it into place only on success, so a failed transfer never leaves a half-written file under the real name. Per-user ceilings on concurrent transfers, relayed bytes per day (10 GiB default — every relayed byte is an egress byte, so this bounds what one account can cost), and bandwidth (100 Mbit/s default); all three configurable per environment, crossing them answers 429. The WebClient shows the day's allowance as a progress bar in Settings (`GET /api/transfers/usage`), noting that only relayed transfers count
- Path sandbox with per-root read-only/writable flags, symlink and junction resolution, and the Daemon's own state kept unreachable. Link resolution walks one hop at a time and re-resolves each target from the filesystem root down, under a 40-hop budget: following a chain to its end in one step and trusting the string it returns is not enough on Unix, where the returned path can still run through another link and launder an escape. A root the sandbox cannot resolve now fails startup rather than being shared in its unresolved lexical form
- Request bodies validated against the contract's own field limits, so bad input is a 400 rather than a database error
- The API contract is generated (C# → `openapi/AngstromCommander.Server.json` → the WebClient's `schema.d.ts`), with CI failing on drift
- Server and Daemon containers run as a non-root user
- WebClient: sign in / register, claim a pairing code, machine list with online status, dual-pane browser with download, upload, copy between panes (both asking before they replace a file), and the mutations — New folder, Rename and Delete. The Daemon reports what it shares (a `ListRoots` relay op), so picking a machine starts the pane in its first shared root, with a shared-folders dropdown (read-only roots marked) instead of a guessed path. The path box navigates on Enter or Go rather than per keystroke, superseded listings are aborted, and anything that can write refuses to act until the target pane has a real path — a stale listing must never decide whether a copy is about to replace a file
- Unpair — the opposite of enrollment: a machine can be revoked from the WebClient (soft revoke, `RevokedAt`); it vanishes from the machine list, relay ops and the challenge refuse it, and the Daemon reacts — by push when connected, by the challenge refusal otherwise — by discarding its registration and returning to the pairing screen, TV-style
- Azure deployment: `infra/` is real — a shared Terraform module (imported the hand-made resource group and DNS zone, added the container registry) plus the per-environment stamp driven by `azd`. The `test` stamp is live: Server on Container Apps behind `api.test.angstrom.adamlengyel.com` (managed TLS), WebClient on Static Web Apps behind `app.test.…`, managed PostgreSQL whose schema self-initialized via migrations on first boot, CORS verified across the real origins, and `terraform fmt`/`validate` gating every PR
- The whole product loop verified over the public internet (July 2026): a real Windows Daemon enrolled against the test stamp, was claimed from the deployed WebClient, reconnects on its own through Server pauses, and serves relayed directory listings of `C:\` paths
- Merges to main deploy themselves: after the CI gates pass, a workflow provisions + deploys the test stamp under a federated (secretless) identity — what is live on test is main, as of its last green push
- Registration is a switch, closed by default (`Registration:Enabled` — Development opts in, a stamp flips the env var without a deploy, closed answers 403 with a readable detail): standing environments are private until deliberately opened, existing accounts unaffected

- The MCP endpoint is live: `/mcp` (Streamable HTTP, stateless) exposes list_machines, list_roots, list_directory, create_directory, move_entry and delete_entry — thin adapters over the same `DaemonRelayOperations` the REST endpoints call, per the protocol-adapter rule. Personal access tokens are its only key: minted/listed/revoked in Settings, stored hash-only in the new sessions table, scoped, refused everywhere except `/mcp` (and user JWTs refused there). Byte-carrying tools (download/upload content) are deliberately absent — bytes do not belong base64-packed in JSON-RPC

**Next, in order:** OAuth + consent → Agent + model picker.

**Known gaps, all deliberate:**
- Opening registration to the public still needs email verification and bot protection (e.g. Turnstile); the switch exists and standing environments are closed by default, so those wait for a launch that wants strangers. Note the residual leak this leaves: `/api/auth/register` no longer repeats Identity's "that address is taken" wording, but a registration that *succeeds* still proves the address was free. Only a create-or-email flow — which needs the email verification above — answers identically either way, so an open environment is enumerable until that ships
- The daily transfer quota lives in the Server's memory, so it is a per-replica, per-process counter: restarting the Server (every deploy does) hands every user a fresh 10 GiB. It bounds runaway use within a run rather than across a day, and the § Operating costs egress figures assume nobody is deliberately farming deploys. It moves to Redis with the rest of the per-replica state below
- The stamp's PostgreSQL is reachable on its public endpoint by every Azure service in every subscription (the `0.0.0.0` firewall rule is how Azure spells that), with only the 32-character random password in front. Container Apps on the Consumption profile has no fixed outbound address to allow-list instead, so closing it means giving the stamp a VNet and private networking — which replaces the Container Apps environment rather than editing it
- Prod PostgreSQL has no `prevent_destroy` guard, though § Hosting promises one. Terraform's `lifecycle` block takes literals only, so it cannot be switched on per environment, and one stamp definition serves all of them — turning it on unconditionally would make `azd down` fail on exactly the demo stamps that exist to be discarded. Giving prod the guard means a prod-only overlay, and that is the work
- Per-PR demo stamps are not wired yet (the pull-request federated credential already exists, so it is workflow work, not identity work). And the deploy pipeline knows only test: when prod exists, it must get an approval gate, never the same auto-deploy
- Binding a stamp's `api.` managed TLS certificate is a one-time manual step after first provision (`az containerapp hostname bind`, see infra/README.md): the azurerm provider cannot create Container Apps managed certificates, so Terraform creates the unbound domain and thereafter ignores the binding
- User sign-ins are still stateless JWTs: no refresh tokens, so a sign-in is revocable only by expiry (8 h). The sessions table now exists (PATs live in it, listed and revocable in Settings); wiring sign-ins into it is what remains of the "active sessions" page
- The Server is single-replica by design for now (`maxReplicas: 1` in the stamp). The full inventory of per-replica state that moves to Redis together when that changes: the Daemon socket registry (already behind `IDaemonConnectionRegistry`), per-user transfer slots, and per-user daily usage counters — plus the piece that is real engineering rather than a swap: request forwarding between replicas ("requests follow the socket", § Architecture). Deliberately staying per-replica even then: the bandwidth token bucket (one transfer's bytes flow through one replica, and a shared bucket would put a Redis round trip in the hottest loop)
- Rate limiting buckets callers by caller address (IPv6 masked to its /64, since a subscriber owns at least that much and keying on the full /128 would let one caller step to a fresh bucket per request). The Server trusts `X-Forwarded-For` from any proxy because Container Apps' ingress address is not known ahead of time, taking exactly one hop from the right (`ForwardLimit = 1`, stated explicitly rather than inherited). Behind the ingress a caller cannot forge their bucket — their own header sits to the left of the entry the ingress appends and is ignored — but two things still bite: put the Server anywhere reachable directly and forgery is back, and if a second proxy hop ever fronts the app the rightmost entry becomes a platform address, collapsing every caller into one bucket and turning the anonymous limits into a single global one
- Downloads buffer into a browser Blob — fine for documents, wrong for very large files; the fix is a short-lived download ticket in the URL so the browser streams to disk
- No live-update channel for clients: panes refresh on navigation, and there is no transfer progress
- The WebClient bundle ships no Content-Security-Policy: it has to name the Server's origin, so it belongs with the Static Web Apps configuration rather than the compose-only nginx image
- Windows path handling is proven at the API level (a real Windows Daemon serves relayed `C:\` listings on the test stamp), but the WebClient's dual-pane browsing of Windows paths is still unit-tests-only
- The Daemon's private key sits unprotected on disk (no DPAPI/keychain/file-permission hardening) — the sandbox refuses to serve it however the roots are configured, but local file permissions must still be addressed before any real install story
- The path sandbox resolves links and then opens by path, so a link swapped in between the two would not be caught. Closing that needs opening by handle (`O_NOFOLLOW` and the Windows equivalent); it requires local write access to a shared folder to exploit, so it waits
- `MapOpenApi()` is not wired, so the contract exists only as a build artifact; there is no browsable API reference
- The Server is close to unobservable: no request log, no application logging of its own, relay failures swallowed without a trace, and no audit trail — while EF Core prints every SQL statement it runs. § Observability has the measurements and the order the rework is worth doing in
- The Server integration suite boots a factory per test, which is most of its runtime, and could share one
- That suite runs on `TestServer`, not Kestrel, so no test can see a Kestrel-level limit. That is not academic: uploads over ~28.6 MB used to die on Kestrel's default 30 MB body cap — and be reported as *the machine* falling silent, since the limit surfaces as an `IOException` — and the whole suite stayed green throughout. The upload endpoint now lifts that cap explicitly (size is the wrong guard when the quota, bandwidth and concurrency ceilings already bound the cost), but proving it still means a real upload against a stamp or `docker compose`. A single smoke test on a real Kestrel host would close the class

## Security model

The model the system is built toward, which includes parts not written yet (OAuth for MCP clients, refresh tokens, the sessions page). § Implementation status is the source of truth for what exists today.

- TLS everywhere; WebClient and MobileClient authenticate with JWT access tokens, and will carry hashed refresh tokens revocable per device once sessions are stored
- Registration is closed unless an environment deliberately opens it (`Registration:Enabled`, default false outside Development) — accounts unlock everything the relay can cost, so who gets one is a per-environment decision, not a default
- Sign-in is guessing-resistant: failed attempts lock the account (ASP.NET Core Identity's counters), and wrong password, unknown account and locked-out account all answer identically — same body, same cost — so the endpoint cannot be used to enumerate emails. The lockout case matters most and is the easiest to get wrong: it can only befall an account that exists, so an answer that admits to it makes five wrong guesses a membership test, and Identity's lockout pre-check returns without hashing, which would say the same thing to a stopwatch. Every anonymous endpoint — sign-in, registration, enrollment (including the claim), the Daemon challenge/token exchange — is rate limited per caller
- No secret has a default: the JWT signing key ships nowhere in the repo (Development reads one from `appsettings.Development.json`, every other environment sets `Auth__JwtSigningKey`), and a Server configured without one refuses to issue or accept a token instead of falling back to something known
- The three live credential types side by side. The two JWTs are HS256 with the stamp's key, verified by one middleware — after the signature check, the `token_type` claim is the only wall between their worlds, enforced by authorization policies and pinned by tests. The PAT is not a JWT at all: a random value matched by stored hash, routed to its own handler by the `acpat_` prefix:

  | | User token (JWT) | Daemon token (JWT) | Personal access token |
  | --- | --- | --- | --- |
  | Earned by | password (+ lockout, rate limit) | signing a fresh nonce | minted in Settings with a user token (a PAT can never mint or revoke PATs) |
  | Lifetime | 8 hours | 10 minutes | until revoked |
  | Claims | `sub` = user id, `token_type=user` | `registrationId`, `token_type=daemon` | none in the token — user and scopes live in its session row |
  | Carried in | `Authorization: Bearer` header | query string on the SignalR connect | `Authorization: Bearer` header |
  | Opens | user endpoints (machines, files, unpair) | only the relay hub | only `/mcp` |
  | Revocation today | expiry only (refresh tokens planned) | ≤10 min via `RevokedAt` at the challenge | instant — every request finds the live session row or fails |
- Daemon identity = keypair generated at enrollment; the Server stores only the public key (DB leak ≠ Daemon impersonation); revoking a registration kills that machine's access. The sandbox refuses to serve the Daemon's own state directory whatever the roots say, so sharing a folder that contains it cannot hand out the key that is the machine's identity
- Enrollment: Daemon shows a short-lived one-time pairing code, user enters it in a logged-in WebClient or MobileClient (TV-pairing style). Short-lived applies after the claim too: claiming pushes the expiry out just far enough for the enrolling Daemon's next poll to collect its registration id, and the code stops answering after that. A claimed code that answered indefinitely would be a permanent anonymous lookup from a short string — one screenshot, one scrollback — to a live registration id, which is enough to drive the challenge endpoint and confirm the machine is still enrolled
- The Daemon connection nonce is single-use, and single-use atomically: read-then-remove is two steps, and two requests replaying one captured signature could both pass between them. A signature is valid forever, so the nonce is the only thing that stops a recorded connect from being replayed into fresh connection tokens
- Path sandboxing in the Daemon: canonicalize all client-supplied paths and resolve every symbolic link and junction along them, then enforce allowed roots — canonicalizing alone is lexical, so a link planted inside a shared folder would otherwise read and write outside it. Resolution is a one-hop-at-a-time walk whose every result is re-resolved from the filesystem root down, bounded by a hop budget: asking the platform to follow a chain to its end and trusting the path it hands back is not enough on Unix, where that path can itself run through another link, and a two-link arrangement inside a shared folder would then pass the root check and be opened outside it. Each root carries a writable flag and read-only is the default, so sharing a folder never implies permission to change it — writes resolve only against writable roots. Mutations (delete, move, mkdir) resolve the parent fully but take the final component as-is: deleting or moving a link acts on the link, never on its target, and a shared root itself can never be deleted or renamed
- AI access: the Agent and third-party MCP clients act only through the Server's MCP endpoint with user-scoped tokens (OAuth 2.1 or PATs) — same authorization and Daemon sandbox as any client, no extra access path. Confirmation of mutating ops lives where the user is: the built-in Agent's chat pane enforces it, while a third-party client's own UX (e.g. Claude Code's permission prompts) is trusted with it — the Server cannot see another client's screen, so what it enforces are ownership checks, writable-root flags, and instant token revocation; every AI session is listed and revocable on the "active sessions" page

## Observability

Where the running system's behaviour can be seen from, and — stated plainly because the gap is
wide — how little of it currently can be. This section describes today; the rework below is on
the roadmap, not done.

**What exists.** Both Server and Daemon use the stock ASP.NET Core console logger: plain text to
stdout, no Serilog, no OpenTelemetry, no Application Insights, no JSON formatter. Container Apps
forwards stdout to a per-stamp **Log Analytics workspace** (`log-<env>`, PerGB2018, 30-day
retention, wired to the Container Apps environment in `infra/main.tf`). Two different views, worth
not confusing: `az containerapp logs show` is a live tail of the *current replica's* stdout —
ephemeral, good for "what is this container doing right now" — while the workspace is the durable,
queryable history, and `--type system` is the platform's own events (scaling, provisioning) rather
than the app's output. There are no metrics, no traces, and no alerting beyond the subscription
budget alert in § Operating costs.

**A consequence of plain text:** every line lands in the workspace as one unstructured string, so
KQL can only substring-match it. There is no querying by field, request, user or operation.

**What is actually being logged — measured on the test stamp, August 2026:** about 91,000 lines
and 4.7 MB of text a day, essentially all of it EF Core printing the SQL of every statement it
runs (`Default: Information` puts `Microsoft.EntityFrameworkCore.Database.Command` at Information,
and each query costs three or four lines). Parameter *values* are not exposed — sensitive-data
logging is off, so they render as `@__p_0` — but the schema is, column lists included. Volume sits
far inside Log Analytics' included 5 GB/month, so this costs nothing; what it costs is signal.

**What is not logged, which is the actual state of things:**

- `Microsoft.AspNetCore: Warning` turns off request logging, so there is no access log — nothing
  records that a call arrived, who made it, or what it answered
- The Server has no application logging of its own. Its single `ILogger` is a constructor
  parameter `AuthenticationHandler` requires, and it is never used. Every structured log event in
  the repo belongs to the Daemon
- The relay endpoints catch `IOException`/`OperationCanceledException`/`TimeoutException`, bind the
  exception only to the `when` filter, and discard it. A failed transfer answers the caller "the
  machine did not answer" and leaves no server-side trace whatsoever. The transfer defects fixed in
  § Implementation status were, by construction, invisible in these logs — which is the sharpest
  argument in this section
- Nothing is audited: sign-in failures, PAT use, unpair, and revocation leave no record. A PAT's
  `LastUsedAt` is deliberately a coarse "is this still in use" signal and explicitly not an audit
  log (§ Security model), so there is no other trail

### Where it is going

None of this is built yet. See the "Telemetry" diagram at the bottom for the shape.

**OpenTelemetry is the spine, not Serilog.** The code already logs through `ILogger`, and OTel
rides on top of that rather than replacing it, so instrumenting is additive instead of a rewrite.
Serilog is a logging library: it would still leave metrics and traces unsolved, and the repo would
carry two logging abstractions to no end. On Azure the whole pipeline is one package
(`Azure.Monitor.OpenTelemetry.AspNetCore`) wiring the SDK to an Application Insights exporter, and
App Insights is backed by the same Log Analytics workspace the stamp already provisions — so this
adds a dependency, not a bill. The console formatter also switches to JSON, which alone fixes the
unstructured-string problem above for the fallback path.

| Signal | Emitted by | Format | Lands in | Kept |
| --- | --- | --- | --- | --- |
| Logs | Server, through `ILogger` | structured — JSON console + OTLP | App Insights → `log-<env>` | 30 days |
| Metrics | ASP.NET Core, EF Core and SignalR built-in meters, plus ours | OTLP | App Insights | 90 days (metrics are cheap) |
| Traces | ASP.NET Core, HttpClient, EF Core instrumentation | OTLP, sampled | App Insights | 30 days |
| Daemon logs | Daemon, through `ILogger` | console + rolling file | **the user's own disk** | size-capped, never exported |

**Daemon telemetry stays on the user's machine.** This is a product decision before it is a
technical one. A Daemon's logs are made of file paths, directory listings and machine names — the
user's own data, from a product whose whole promise is that their files stay theirs. Shipping that
to our cloud as routine telemetry would be a privacy breach dressed up as diagnostics. So the
Daemon writes locally, rolling and size-capped inside its state directory, and exports nothing on
its own. When something genuinely needs investigating, support asks the user for a diagnostic
bundle and the user chooses to send it — consent at the moment it matters, rather than assumed
once at install. It keeps the Daemon free of any cloud telemetry dependency too, which suits a
component that deliberately has no inbound anything.

The corollary binds the Server: it relays those same paths, so **paths are user data there as
well**. They belong at Debug, not Information, and file contents belong nowhere. Never logged at
any level, on either side: user and Daemon tokens, PAT values, the JWT signing key, Daemon private
keys, and challenge nonces.

**Correlation** is the part that is real work rather than configuration. W3C `traceparent` carries
a trace across HTTP for free, but the relay's interesting hop is a SignalR invocation, which does
not propagate trace context on its own — it has to travel explicitly in the protocol records. The
transfer id already exists and is the natural correlation key for a transfer's whole life.

**The metrics worth having**, each chosen because it answers a question this document already
asks: how many Daemons are connected (a gauge, and the truest health signal there is — `/healthz`
answers fine while the relay is dead); relayed bytes (this *is* the egress cost argument in
§ Operating costs, currently unmeasured); relay operation duration and failure rate; transfer
failures by cause (the defects fixed above were invisible precisely because nothing counted them);
and rate-limit and quota rejections, which is how you tell a limit biting real users from a limit
doing its job.

**Levels** stop being per-author taste: Debug off outside development; Information for lifecycle
and business events (a Daemon connected, a transfer completed, a PAT minted); Warning for
refusals that are expected but worth seeing (sandbox denial, quota exceeded, a retry); Error for
the unexpected, meaning a human should look; Critical only for "this process cannot continue" —
no signing key, no database at startup.

**Alerting** stays small enough to be believed: no Daemons connected, the Server's 5xx rate, and
unhandled exceptions. The subscription budget alert in § Operating costs already covers spend.

**In the order it is worth doing:**

1. Two lines of configuration recover most of what is lost today —
   `Microsoft.EntityFrameworkCore.Database.Command` down to Warning,
   `Microsoft.AspNetCore` up to Information — trading a log that is ~85 % SQL for an actual
   request log. Deliberately *not* taken yet: on a stamp being debugged by hand, seeing every
   query is sometimes the point, so it is an environment decision rather than an obvious win
2. Structured events on the Server for what it currently stays silent about — relay operations,
   the transfer lifecycle and its failures (those discarded exceptions), and auth outcomes
3. OpenTelemetry and the exporter, giving traces and metrics rather than lines, plus the
   correlation id threaded through the relay
4. An audit trail for the security-relevant events, which the "active sessions" page
   (§ Implementation status) wants anyway

**Deliberately deferred:** browser telemetry from the WebClient, which is a second consent surface
and pointless before there are users; sampling tuning, until there is traffic worth sampling; and
SLOs, which mean nothing before anyone depends on this.

## Scaling

Sanity check against a large user base (hundreds of thousands of users → millions of enrolled Daemons, each holding an idle persistent WebSocket). The architecture's shape holds — the outbound-connection relay is the same pattern Parsec, TeamViewer, and ngrok run at larger scale — with two known pressure points, both planned for rather than redesigned around:

- **Connection routing** (the real bottleneck): no single Server instance holds a million sockets, so the Server becomes N replicas and requests must reach the replica owning the target Daemon's socket. Solved by the connection registry (see § Architecture — Server); it is designed into the relay from the first commit because retrofitting it later is surgery.
- **Relay bandwidth** (the real cost): every file transfer streams through Azure, so egress spend grows with users' copy habits — this is exactly why Parsec is P2P. The direct P2P upgrade (Parsec-style, brokered by the Server, relay as fallback) is not needed for v1 but becomes an economic requirement well before this scale.

What already scales without change: PostgreSQL stores coordination metadata only, so 500k users is a small database; the Server is stateless w.r.t. file data and streams in chunks, so replicas scale horizontally; Agent/LLM cost is pay-per-token with per-user usage limits — linear, no cliff. Smaller shifts at that scale, none structural: scale-to-zero stops mattering, and Container Apps may yield to AKS if connection density demands it.

**Going global** is the same sanity check against distance rather than load: a US or Asian user talking to a single West-Europe stamp pays the ocean twice on every relayed byte (client → Server → Daemon and back), and no amount of replicas fixes geography. The answer is an extension of the stamp model, not a redesign, in roughly this order:

1. **A stamp per region** — the Terraform already parameterizes region, and a stamp is already the deployment unit; `prod-us` is `azd env new` away. The WebClient needs nothing: Static Web Apps serves it from a global CDN today.
2. **Home each account to one region**, chosen at registration (nearest wins). The account's users, sessions, Daemons — and therefore every relayed byte — stay inside that region, which works because a person's machines and clients overwhelmingly share a geography. Region-homing also keeps each stamp's PostgreSQL and signing key independent: a user only ever talks to their home region, so nothing global has to verify tokens.
3. **Route clients to their home region** — latency-based DNS (Front Door / Traffic Manager) on one hostname plus a redirect at sign-in, or per-region hostnames the clients learn at login. The one genuinely new piece in the whole plan is the small global account → home-region directory this consults.
4. **Cross-region relay only if accounts ever span regions** (a machine in the US and one in Europe on one account): that is the connection registry's existing "requests follow the socket" answer applied one level up — forward the request to the region owning the Daemon's socket, exactly as replicas already forward to each other inside a stamp.

Nothing about the Daemon, the protocol, the sandbox, or the data model changes — which is the point of having designed the socket-lookup indirection and coordination-only database in from the start.

## Hosting & infrastructure

See the "Provisioning & deployment" diagram at the bottom for how the pieces fit together.

- Azure hosts everything hostable (Server, WebClient, Agent)
- Azure services: Azure Container Apps (Server — WebSockets; `minReplicas: 1`, because Daemons hold sockets open the Server cannot scale to zero — only Daemon-less environments can), Static Web Apps (WebClient hosting), Azure Container Registry, Microsoft Foundry (Foundry Models — serverless LLM inference, no idle cost; Foundry Agent Service — hosts the Agent container)
- Infrastructure as code: Terraform (via azd's Terraform provider) — `azd up` provisions + deploys; remote state in an Azure Storage account
- Environments are first-class: `azd env new <name>` + `azd up` spawns a full isolated env (test, qa, per-feature-branch demos, prod); one resource group per env; `azd down` tears it down. Prod is the same stamp with different variables (sizes/SKUs), never a hand-built special case
- Environment glossary — the canonical names, used everywhere an environment is meant:
  - **`local`** — the `docker compose up` stack on a dev machine. Not a stamp, not in Azure, data disposable. (Deliberately not called "dev": that would blur into .NET's `Development` mode and "dev machine", which are a framework setting and a piece of hardware)
  - **`test`** — the standing Azure stamp (live since July 2026): `api.test.` / `app.test.` under the zone
  - Planned, same pattern: **`qa`**, **`prod`** (prod alone owns the bare `api.` / `app.` names), and **`demo-<x>`** per-feature-branch stamps
  - For Azure stamps, one name appears in three places by construction: the azd environment, the resource-group suffix (`angstrom-commander-<name>`), and the DNS label. It also appears in a few hand-maintained tooling spots (REST Client environments, Daemon launch profiles) — infra/README.md § Adding an environment is the checklist
- `azd up` is idempotent: per resource Terraform no-ops, updates in place, or (only for immutable attribute changes) destroys-and-recreates — the plan marks replacements explicitly. Manual portal edits are drift and get reverted on the next apply; the `.tf` files always win
- Data safety: prod PostgreSQL gets a `prevent_destroy` lifecycle guard (blocks any destroying plan, incl. `azd down`); prod plans get human review before apply, demo envs may auto-apply
- Promotion is by commit, not by artifact: promoting a build to qa/prod means deploying the same commit with that environment's variables, and rebuilding along the way is accepted — lockfiles and pinned package versions keep rebuilds equivalent, and it is what `azd`'s per-environment build-and-deploy flow does naturally. (If that ever changes, the WebClient's build-time server URL becomes the first blocker: identical bundles would need the URL supplied at deploy time.)
- **Provisioned by hand (July 2026) — never recreate:**
  - Resource group `angstrom-commander-shared` (West Europe), the home for cross-stamp resources (DNS, the container registry, Terraform state) — now imported and managed by `infra/shared`
  - Storage account `angstromtfstate` in that group: holds every Terraform state file (blob versioning on), so it stays outside Terraform itself — `infra/README.md` records the bootstrap commands
  - Azure DNS zone `angstrom.adamlengyel.com` in that group (also imported into `infra/shared` now). The parent `adamlengyel.com` lives at an external registrar, where four NS records delegate this subdomain to Azure (`ns1-09.azure-dns.com`, `ns2-09.azure-dns.net`, `ns3-09.azure-dns.org`, `ns4-09.azure-dns.info`); delegation is live and verified. Terraform therefore creates per-environment records *inside* the zone without touching the registrar again
  - Naming: prod is `api.angstrom.adamlengyel.com`, other stamps are `api.<env>.angstrom.adamlengyel.com`; the WebClient gets `app.` equivalents. The apex `adamlengyel.com` is an unrelated personal site and must be left alone

## Operating costs

> **Estimated July 2026, West Europe. Keep this current** — re-check it whenever the hosting choices, SKUs, or replica counts change, and refresh the rates periodically even when nothing changes: Azure prices drift, and a stale table here is more misleading than no table. Same living-document rule as the rest of this file. Only permanent free allowances are counted (never new-customer trials, which expire and would make the numbers lie later).

Rates come from Azure's retail prices API for West Europe (`prices.azure.com/api/retail/prices`), which is the way to re-check them: Container Apps Consumption at **$0.000034 per vCPU-second active** ($0.000004 idle) and **$0.000004 per GiB-second** (memory has no idle discount), requests at $0.56 per million after the first two million, PostgreSQL B1ms at $0.0199/hour plus $0.1369/GiB-month of storage. Assumes active billing, one enrolled Daemon, and light personal traffic. Each environment is priced on its own, at full rates — see the note after the table for the one discount left out.

| Component | prod (always on) | test (12 h/day ≈ 365 h) | demo123 (3-day stamp, ~2 h used) |
| --- | --- | --- | --- |
| **Server** — Container Apps, 0.25 vCPU / 0.5 GiB | ~$28 (730 h) | ~$14 (365 h) | ~$0.10 (scales to zero) |
| **PostgreSQL** | Flexible Server B1ms + 32 GiB: ~$19 | its own Flexible Server B1ms, always on: ~$19 (shared test data must survive restarts, so this cannot be containerized — see the note below) | containerized in the stamp, disposable: ~$3 |
| **HTTP requests** — first 2M/month free, then $0.56/M | $0 | $0 | $0 |
| **WebClient** — Static Web Apps, free tier | $0 | $0 | $0 |
| **Container Registry** — ACR Basic | ~$5 (shared by all stamps) | shared | shared |
| **DNS zone** | ~$0.50 (shared) | shared | shared |
| **Log Analytics** — 5 GB/month included | $0–3 | $0 | $0 |
| **Egress** — first 100 GB/month free, then ~$0.087/GB | $0 at light use | $0 | $0 |
| **Agent / LLM tokens** — pay-per-token, per-user caps | $0 until the Agent ships | — | — |
| **Redis** — only once the Server runs >1 replica | $0 today, ~$16 when needed | — | — |
| **Total** | **~$53/month** | **~$33/month** | **~$3 for its whole life** |

Which PostgreSQL an environment gets is a stamp variable, not a different stamp: **managed wherever data must survive** (prod, and any shared environment people rely on), **containerized only where the data is disposable** (per-branch demo stamps, local compose). There is no third option in this hosting model — a container's filesystem is ephemeral, and Container Apps' only volume type is Azure Files over SMB, which PostgreSQL does not support as a data directory (it requires `0700` on `PGDATA`, which SMB cannot express, and lacks dependable `fsync`). "Containerized" therefore always means "disposable".

Two things drive the bill, and neither is the resource list:

- **The Server cannot sleep.** Daemons hold sockets open, so `minReplicas: 1` is required and prod pays 24/7. That is the direct price of the "no NAT, no port forwarding" promise. The idle rate is 8.5× cheaper on vCPU (memory costs the same either way) and applies to a replica that is *not processing requests*, but a live WebSocket plausibly counts as one — so budget the active rate and treat idle as upside (~$8/month instead of ~$28 if it ever applied).
- **Egress scales with what users move.** Downloads and the outbound leg of a machine-to-machine copy leave Azure; inbound is free. 100 GB/month is free, so personal use is $0 — but 1 TB/month of relayed transfers is ~$78/month. That is the § Scaling P2P argument stated in currency.

Overbilling detection is a subscription-level **budget alert** (`monthly-spend-guard`: $100/month, emails at 40/60/80/100% of actual spend). It guards the owner's whole subscription, not just this project, so it is deliberately *not* managed by this repo's Terraform — noted here because it is part of the cost-safety story: detection rather than a cap (Azure pay-as-you-go has no hard spending limit), with the pause procedure in infra/README.md as the response.

The test column's 12 h/day figure is an *assumption about usage, not something the stamp does by itself*: the standing test environment runs `minReplicas: 1` around the clock, so left alone it bills like the prod column (~$28 Server + ~$19 PostgreSQL ≈ $47/month) — the 12 h figure is what stopping it outside working hours would earn. The Terraform state storage account adds pennies (LRS, kilobytes of state).

Levers: a standing environment nobody is using right now can be *paused* — Server revision deactivated, PostgreSQL stopped (infra/README.md § Pausing an environment) — dropping it to ~$4–5/month of storage, with the caveat that Azure restarts a stopped PostgreSQL by itself after 7 days. PostgreSQL on a standing free serverless tier (e.g. Neon, which scales to zero and is ample for this schema) takes prod to ~$34/month. Demo stamps cost almost nothing while no Daemon is paired to them — pair one and its reconnect loop keeps the environment awake, which removes the saving.

One discount is deliberately left out of the figures above: Container Apps includes 180,000 vCPU-seconds and 360,000 GiB-seconds per subscription per month, worth roughly $7.50 at these rates. It is consumed once across all environments rather than per environment, so the real invoice should come out about $7.50 below the sum of any columns here.

**Scaling up is a cliff, not a ramp.** Everything above uses the serverless Consumption profile, where a replica costs what it allocates. The alternative — a Dedicated workload profile, which § Scaling names as the step before AKS — rents whole nodes continuously and adds a **$0.10/hour Dedicated plan management fee**: the smallest profile (D4, 4 vCPU / 16 GiB) is ~$236 compute + ~$78 memory + $73 management ≈ **$387/month**, roughly 14× the Server's current footprint. Growing by adding 0.25 vCPU Consumption replicas (~$28 each) stays cheaper for a long time. Note also that the management fee attaches to an environment using private endpoints or planned maintenance *even on Consumption*, so enabling either is a ~$73/month decision rather than a checkbox.

## Source control & CI/CD

- GitHub hosts the repo: <https://github.com/Anarkin/angstrom-commander> (private, personal account — free tier is ample for solo, incl. 2,000 Actions minutes/month)
- CI/CD: GitHub Actions. Deploys are wired (July 2026): merges to main run the CI gates, and on success the `azure-dev.yml` workflow deploys to the test stamp — `azd provision` (idempotent, so infra changes ride along) + `azd deploy`, authenticating via the `msi-angstrom-commander` federated identity that `azd pipeline config` created (OIDC — no cloud secrets stored in GitHub). The deploy job admits only green CI runs triggered by a push to this repo: `workflow_run`'s `branches: [main]` filter matches a branch *name*, which a fork's pull request can share, so on its own it would hand a stranger's code the Azure identity
- Every PR runs the full guardrail suite: build (warnings = errors), tests, `dotnet format`, ESLint/Prettier, contract-drift checks, and a compose smoke test that builds all three images and waits for the Daemon to reach the Server (`terraform fmt`/`validate` join it with `infra/`)
- Later: PR-open spawns a demo env (`azd up` for `demo-prN`), PR-close tears it down

## Dev environment

- Everything runs in Docker containers; dev machines likely run Rancher Desktop (Windows)
- One command is the whole setup: `docker compose up` brings up the Server (+ its PostgreSQL), the WebClient, and a Daemon that pairs itself. The Agent joins them when it exists
- The Agent runs locally as a plain container (Foundry Agent Service is prod hosting, not a dev dependency); it points at a real Foundry Models endpoint — there is no local LLM, dev inference is pay-per-token against a dev Foundry resource

## Open questions

- When the direct P2P connection upgrade ships — not whether (see § Scaling: relay egress cost makes it an economic requirement at scale, though not for v1)
- Which LLMs earn a slot in the curated model picker at launch (tool-calling quality varies widely across the Foundry catalog); per-model SDK: Claude via the official `Anthropic.Foundry` .NET SDK, others via the OpenAI-compatible surface

## Diagrams

### Containers

```mermaid
flowchart TB
  user(["👤 User"])

  subgraph devices["User's devices & AI tools"]
    web["WebClient<br/>React<br/><i>dual-pane UI + AI chat pane<br/>(curated model picker)</i>"]
    mobile["MobileClient<br/>React Native<br/><i>iPhone + Android</i>"]
    mcpclients["Third-party MCP clients<br/><i>Claude Code, Claude Desktop, ChatGPT, …<br/>user's own AI subscription</i>"]
  end

  subgraph azure["Azure — one stamp per environment (prod, qa, demo-*)"]
    server["Server<br/>.NET 10, ASP.NET Core + SignalR<br/><i>accounts, Daemon registry, relay<br/>(correlation-ID multiplexed),<br/>MCP endpoint + OAuth 2.1/PATs;<br/>stateless w.r.t. file data</i>"]
    agent["Agent<br/>.NET 10 container, Foundry Agent Service<br/><i>agent loop: chat sessions,<br/>confirmation gates, usage limits</i>"]
    models["Foundry Models<br/><i>serverless LLM inference —<br/>one endpoint, many models,<br/>pay-per-token</i>"]
    db[("PostgreSQL<br/><i>coordination metadata only:<br/>users, Daemon regs (public keys),<br/>sessions (incl. PATs/OAuth grants),<br/>pairing codes. Never file data</i>")]
    swa["Static Web Apps<br/><i>hosts + serves WebClient's<br/>static bundle</i>"]
  end

  subgraph machines["User's machines — × N: laptop, PC, home server, container"]
    daemon["Daemon<br/>.NET 10, SignalR client — no inbound API<br/><i>answers relayed file ops: list/stat/copy/<br/>move/delete/rename/mkdir, streamed<br/>up/download; path-sandboxed to<br/>allowed roots</i>"]
  end

  user --> web
  user --> mobile
  user --> mcpclients
  web -->|"REST/HTTPS (JWT): file ops, auth, AI chat<br/>WebSocket: live updates, progress"| server
  mobile -->|"REST/HTTPS (JWT): file ops, auth, AI chat<br/>WebSocket: live updates, progress"| server
  mcpclients -->|"MCP over Streamable HTTP<br/>(OAuth 2.1 consent flow or PAT)"| server
  server -->|"chat sessions<br/>(forwarded, single public origin)"| agent
  agent <-->|"Messages API + tool calling<br/>(model = request parameter)"| models
  agent -->|"MCP tools/call<br/>(user-scoped token)"| server
  daemon -->|"persistent outbound WebSocket (SignalR, 443)<br/>dials OUT and holds open;<br/>Server pushes requests back down it"| server
  server -->|"EF Core"| db
  swa -.->|"delivers JS bundle on page load"| web
```

Key point: **every connection is initiated toward the cloud** — nothing ever connects *to* a Daemon or client, which is why no NAT/firewall/port-forwarding setup is needed anywhere. The same funnel applies to AI: the Agent and third-party MCP clients reach files only through the Server's MCP endpoint — one tool surface, one authorization path, no exceptions.

### Relay flow — "list a directory"

```mermaid
sequenceDiagram
  autonumber
  participant C as WebClient / MobileClient
  participant S as Server
  participant A as Daemon #42
  Note over A,S: Daemon already holds a persistent outbound WebSocket (opened at startup, heartbeats, auto-reconnect)
  C->>S: HTTPS: list C:\Projects on Daemon #42 (JWT)
  S->>S: Authorize: Daemon #42 belongs to this user? Socket connected?
  S->>A: Push over the open socket: {id: abc123, op: list, path: C:\Projects}
  A->>A: Sandbox check (canonicalize, resolve links, allowed roots), read dir
  A->>S: {id: abc123, result: [entries]}
  S->>C: HTTPS response: [entries]
  Note over C,S: Correlation IDs multiplex many concurrent ops over one socket.<br/>Large file transfers stream through in chunks — the Server never buffers a whole file.
```

### Enrollment flow — pairing a new machine

```mermaid
sequenceDiagram
  autonumber
  participant A as New Daemon
  participant S as Server
  participant C as WebClient / MobileClient (logged in)
  A->>A: Generate keypair (private key never leaves the machine)
  A->>S: Request pairing code (sends public key)
  S-->>A: Short-lived one-time code
  A->>A: Display code to user
  loop Every few seconds, until claimed or expired
    A->>S: Has this code been claimed?
    S-->>A: Not yet
  end
  C->>S: User enters code
  S->>S: Bind registration: this Daemon → this account, in one atomic claim
  A->>S: Has this code been claimed?
  S-->>A: Yes — here is your registration id
  Note over A,S: The Daemon asks, because nothing can connect to it yet.<br/>Future connects authenticate by signing a challenge with the private key.
```

### User sign-in — password to bearer token

```mermaid
sequenceDiagram
  autonumber
  participant W as WebClient / MobileClient
  participant S as Server
  participant I as ASP.NET Identity
  W->>S: POST /api/auth/login with email and password
  S->>I: Find the account
  Note over S,I: An unknown address still pays the hash cost - no timing side channel
  I->>I: Check password with lockoutOnFailure (5 tries, then 5 minutes out)
  I-->>S: Succeeded
  S-->>W: JWT - sub is the user id, token_type user, exp now + 8 h, HS256 with the stamp's signing key
  W->>S: Every later call with Authorization Bearer token
  S->>S: Middleware verifies signature, issuer, audience, expiry - no DB lookup, any replica
  S->>S: Policy User requires claim token_type user
  S-->>W: 200
```

### Daemon connection auth — proving the keypair, every connect

```mermaid
sequenceDiagram
  autonumber
  participant A as Daemon
  participant S as Server
  A->>S: POST /api/daemon-auth/challenge with registrationId
  S->>S: Registration exists and RevokedAt is null?
  S-->>A: Fresh 32-byte nonce (2 minute lifetime, single use)
  A->>A: Sign the nonce - ECDSA P-256 over SHA-256, private key never left this machine
  A->>S: POST /api/daemon-auth/token with the signature
  S->>S: Consume the nonce (replay impossible), verify against the STORED PUBLIC key
  S-->>A: Connection JWT - registrationId claim, token_type daemon, exp now + 10 min
  A->>S: Open the SignalR socket bearing that token
  Note over A,S: A leaked DB exposes only public keys - nobody can sign the next nonce.<br/>Tokens re-earned per connect are why revocation bites within minutes despite stateless JWTs.
```

### MCP with a personal access token — how an AI client operates a machine

```mermaid
sequenceDiagram
  autonumber
  participant M as MCP client (Claude Code etc.)
  participant S as Server
  participant A as Daemon
  Note over M,S: Once - the user mints a PAT in Settings (value shown a single time)<br/>and pastes it into the client's config
  M->>S: POST /mcp tools/call list_directory (Authorization Bearer acpat_...)
  S->>S: SHA-256 the token, find the live session row - that user, those scopes
  S->>S: Same authorization as REST - does this user own that machine?
  S->>A: Relay the op over the machine's open socket (sandbox as always)
  A-->>S: Entries
  S-->>M: JSON-RPC result
  Note over M,S: One implementation under REST and MCP alike (DaemonRelayOperations) -<br/>the endpoint is a protocol adapter. Revoking the PAT closes this door instantly.
```

### MCP authorization — OAuth 2.1 consent flow (planned, per § Architecture)

```mermaid
sequenceDiagram
  autonumber
  participant M as MCP client (Claude Code etc.)
  participant B as User's browser
  participant S as Server (OpenIddict)
  M->>S: Dynamic client registration - redirect URIs
  M->>M: Invent PKCE secret, send its hash with the authorize URL
  M->>B: Open authorize URL
  B->>S: GET /authorize
  S->>B: Login if needed, then consent - "this tool wants to operate your machines"
  B->>S: User approves
  S-->>B: Redirect with one-time authorization code
  B-->>M: Code lands at the client's redirect URI
  M->>S: POST /token with code + PKCE secret revealed
  S->>S: Hash matches and code unspent?
  S-->>M: User-scoped access token + refresh token
  M->>S: MCP tools/call with Bearer token - same authorization and sandbox as any client
  Note over M,S: The consent screen is the point - the user delegates, scoped and revocable,<br/>and the tool never sees a password. PATs are the copy-paste shortcut through the same door.
```

### AI assistant flow — "organize this folder"

```mermaid
sequenceDiagram
  autonumber
  participant C as WebClient / MobileClient
  participant S as Server
  participant G as Agent (Foundry Agent Service)
  participant F as Foundry Models (LLM)
  participant A as Daemon #42
  C->>S: chat: "organize ~/Downloads by file type" on Daemon #42 (JWT, chosen model)
  S->>G: start/continue chat session (user-scoped token minted for the Agent)
  G->>F: Messages API: instruction + tool definitions (from MCP tools/list)
  F-->>G: tool_use: list_directory(~/Downloads)
  G->>S: MCP tools/call list_directory (bearer: user-scoped token)
  S->>A: relay op over the open socket (authorization + sandbox as usual)
  A-->>S: directory entries
  S-->>G: MCP result
  G->>F: tool_result: entries
  F-->>G: plan text + tool_use: create_directory / move_entry × N
  G-->>C: plan preview, wait for user confirmation (via Server/SignalR)
  C->>S: user confirms
  S-->>G: confirmation
  G->>S: MCP tools/call × N
  S->>A: execute ops via relay
  G->>F: tool_results
  F-->>G: summary text (no more tool calls)
  G-->>C: done + summary (via Server/SignalR)
  Note over G,S: The Agent is just another MCP client — same endpoint, same user-scoped auth,<br/>same Daemon sandbox as a user's Claude Code. No privileged path exists.
```

Third-party flow (Claude Code etc.) is the same picture minus the Agent and Foundry Models: the MCP client calls the Server's MCP endpoint directly with its OAuth/PAT token, and the user's own LLM fills the Foundry Models role on their side.

### Provisioning & deployment — how `infra/`, Terraform, and Azure fit together

```mermaid
flowchart TB
  dev(["👩‍💻 Developer (or CI later)<br/>runs <b>azd up</b> for a chosen environment<br/>(azd env new qa → azd up)"])

  subgraph repo["Git repo"]
    tf["infra/ — Terraform files<br/><i>declare WHAT should exist in Azure<br/>(desired state, versioned like code)</i>"]
    src["Product source code<br/><i>Server, WebClient, Agent</i>"]
  end

  subgraph up["azd up — two phases"]
    prov["1 · Provision (terraform apply)<br/><i>diff desired state vs recorded state,<br/>create/update only the difference — idempotent</i>"]
    dep["2 · Deploy<br/><i>docker build Server + Agent → push images;<br/>build WebClient bundle → upload</i>"]
  end

  state[("Terraform state<br/>Azure Storage account<br/><i>record of what Terraform<br/>already created per environment</i>")]

  subgraph azure["Azure subscription"]
    acr["Container Registry<br/><i>shared — stores Server + Agent images</i>"]
    subgraph rg["Resource group angstrom-commander-qa — ONE STAMP PER ENVIRONMENT"]
      aca["Container Apps<br/><i>pulls + runs Server image</i>"]
      pg[("PostgreSQL<br/><i>schema self-initializes<br/>via EF Core migrations</i>")]
      swa["Static Web Apps<br/><i>serves WebClient bundle</i>"]
      models["Foundry Models<br/><i>serverless LLM endpoint</i>"]
      agentsvc["Foundry Agent Service<br/><i>pulls + runs Agent container</i>"]
    end
    rg2["angstrom-commander-test, angstrom-commander-demo-featX …<br/><i>identical stamps from the same .tf files,<br/>azd down deletes a whole stamp</i>"]
  end

  dev --> up
  tf --> prov
  src --> dep
  prov <-->|"read + update"| state
  prov -->|"Azure API calls"| rg
  prov -.-> rg2
  dep -->|"push Server + Agent images"| acr
  acr -->|"pull on release"| aca
  acr -->|"pull on release"| agentsvc
  dep -->|"upload bundle"| swa
```

How to read it: `infra/` *describes* the environment; Terraform makes Azure *match* the description (using its state file to know what already exists — so re-runs are no-ops and edits apply only deltas); every environment is the same description stamped into its own resource group; `azd up` = provision + deploy (make infrastructure exist, then put code into it), `azd down` = delete the stamp.

### Telemetry — what is emitted, and where it is allowed to go (planned, per § Observability)

```mermaid
flowchart LR
  human(["👤 Whoever is debugging"])

  subgraph machines["User's machines — telemetry never leaves on its own"]
    daemon["Daemon<br/><i>ILogger. Its logs are made of<br/>file paths and listings —<br/>the user's data, not ours</i>"]
    dfile[("state/logs/<br/><i>rolling, size-capped</i>")]
  end

  subgraph stamp["Azure stamp"]
    server["Server<br/><i>ILogger + Activity + Meter</i>"]
    otel["OpenTelemetry SDK<br/><i>Azure Monitor exporter</i>"]
    ai["Application Insights"]
    law[("Log Analytics<br/><i>one workspace per stamp,<br/>30 days</i>")]
    alerts["Alert rules<br/><i>no Daemons connected,<br/>5xx rate, unhandled errors</i>"]
  end

  daemon --> dfile
  dfile -.->|"diagnostic bundle — only when<br/>support asks and the user sends it"| human
  server --> otel
  otel -->|"logs · metrics · traces"| ai
  ai --> law
  server -->|"stdout JSON — fallback and bootstrap"| law
  law --> human
  law --> alerts
  alerts --> human
```

Key point: the arrow out of the user's machines is **dashed and human-initiated**. Everything the
Daemon knows is the user's own file system, so routine telemetry export would be a privacy breach
dressed as diagnostics — consent is asked at the moment it is needed, not assumed at install.
