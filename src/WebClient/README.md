# WebClient

The dual-pane UI in the browser (React + TypeScript + Vite). See the repo root's
[README](../../README.md) for setup and [ARCHITECTURE.md](../../ARCHITECTURE.md) for the
full picture.

## Commands

| Command                | Does                                              |
| ---------------------- | ------------------------------------------------- |
| `npm run dev`          | Dev server (talks to the Server from compose)     |
| `npm test`             | Vitest + React Testing Library                    |
| `npm run lint`         | oxlint, type-aware, warnings are errors           |
| `npm run format`       | oxfmt (Prettier-conformant)                       |
| `npm run format:check` | The CI formatting gate                            |
| `npm run build`        | Type-checks (`tsc -b`) and builds the prod bundle |

Configuration: `VITE_SERVER_URL` selects the Server (defaults to `http://localhost:5080`,
see `.env.development`) — any build can target any environment.
