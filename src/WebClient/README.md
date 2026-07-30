# WebClient

The dual-pane UI in the browser (React + TypeScript + Vite). See the repo root's
[README](../../README.md) for setup and [ARCHITECTURE.md](../../ARCHITECTURE.md) for the
full picture.

## Commands

| Command                | Does                                              |
| ---------------------- | ------------------------------------------------- |
| `npm run dev`          | Dev server (talks to the Server from compose)     |
| `npm test`             | Vitest + React Testing Library                    |
| `npm run lint`         | ESLint (type-checked rules), warnings are errors  |
| `npm run format`       | Prettier                                          |
| `npm run format:check` | The CI formatting gate                            |
| `npm run build`        | Type-checks (`tsc -b`) and builds the prod bundle |
| `npm run gen:api`      | Regenerates `src/serverApi/schema.d.ts` from the contract |
| `npm run gen:api:check`| The CI drift gate for those generated types       |

## API types are generated

```
C# DTOs → dotnet build → openapi/AngstromCommander.Server.json → npm run gen:api → src/serverApi/schema.d.ts
```

`schema.d.ts` is committed but never edited by hand. After changing a Server endpoint or DTO:
build the Server (which rewrites the contract), run `npm run gen:api`, and commit both files —
CI fails if either is stale. `serverApi/types.ts` exposes friendly aliases (`Machine`,
`DirectoryEntry`, …) so components never import generated names directly.

Configuration: `VITE_SERVER_URL` selects the Server (defaults to `http://localhost:5080`,
see `.env.development`) — any build can target any environment.
