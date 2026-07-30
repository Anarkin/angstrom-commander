# Agent instructions

Angstrom Commander is a cloud-based dual-pane file manager; the full picture is in [ARCHITECTURE.md](ARCHITECTURE.md) — read it before design or implementation work. [README.md](README.md) is the landing page and will carry the setup guide.

## Conventions

- Component names: **Daemon**, **Server**, **WebClient**, **MobileClient**, **Agent** — always these exact capitalized names, no synonyms ("backend", "cloud service", "clients" are all retired). Lowercase "client"/"daemons"/"agent" may appear only as generic protocol-role words (e.g. "an MCP client", "a user's agent"), never as product references.
- No invented codenames beyond **Angstrom Commander** itself — components get plain descriptive names.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `type: subject` or `type(scope): subject` — e.g. `feat(daemon): stream file downloads`. Common types: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`, `build`
- These docs (README.md, ARCHITECTURE.md, this file) and their diagrams are living documents — update them in the same change that alters a decision, architecture, or the security model.
- ARCHITECTURE.md § Operating costs carries dated price estimates: revisit them whenever hosting choices, SKUs, or replica counts change, and refresh the rates when they age. Count only permanent free allowances — never new-customer trials, which expire and turn the estimate into a lie.
- The API contract is generated, never hand-written: C# DTOs → `openapi/AngstromCommander.Server.json` (a Server build output) → `src/WebClient/src/serverApi/schema.d.ts` (`npm run gen:api`). Both generated files are committed and CI fails if either drifts. Endpoints therefore need named response records and typed results (`Results<Ok<T>, …>`) — never anonymous objects — and errors are `ProblemDetails` throughout.
- Manual testing: `tools/<Component>.http` files (VS Code REST Client format) are the manual/exploratory test surface and double as executable API documentation. They are living artifacts under the same rule as the docs: a change that adds, removes, or reshapes an endpoint updates the matching `.http` file in the same change. They complement automated tests, never replace them.
- Mermaid diagrams: use `flowchart` and `sequenceDiagram` syntaxes only; the experimental `C4Context`/`C4Container` syntax is banned (unreadable label layout). No semicolons inside sequence-diagram message text (mermaid parses them as statement separators and the diagram breaks).
