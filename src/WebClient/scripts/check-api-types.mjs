// Drift guard for the generated API contract types.
//
// Regenerates the OpenAPI -> TypeScript output to a temp file with the same CLI that
// `npm run gen:api` uses (so formatting matches exactly) and compares it to the committed
// src/serverApi/schema.d.ts. Exits non-zero on drift.
//
// Implemented in Node rather than as a `| diff` pipe so it behaves identically on Windows
// PowerShell, Git Bash, and the Linux CI image — none of which share a common diff tool.
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const SPEC = "../../openapi/AngstromCommander.Server.json";
const COMMITTED = "src/serverApi/schema.d.ts";

const tempDirectory = mkdtempSync(join(tmpdir(), "api-types-"));
const generatedFile = join(tempDirectory, "generated.d.ts");

try {
    // `npm run` puts node_modules/.bin on PATH, so the binary resolves cross-platform;
    // shell: true lets Windows find the .cmd shim.
    execFileSync("openapi-typescript", [SPEC, "-o", generatedFile], {
        stdio: "inherit",
        shell: true,
    });

    const normalize = (path) => readFileSync(path, "utf8").replaceAll("\r\n", "\n");
    if (normalize(generatedFile) !== normalize(COMMITTED)) {
        console.error(`\n${COMMITTED} is out of date with ${SPEC}. Run: npm run gen:api`);
        process.exit(1);
    }

    console.log(`${COMMITTED} is in sync with the OpenAPI contract`);
} finally {
    rmSync(tempDirectory, { recursive: true, force: true });
}
