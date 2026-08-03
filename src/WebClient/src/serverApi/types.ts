import type { components } from "./schema";

// Friendly aliases over the generated contract. `schema.d.ts` is the single source of truth:
// it is generated from openapi/AngstromCommander.Server.json (itself a build output of the
// Server) via `npm run gen:api`, and must never be edited by hand.
type Schemas = components["schemas"];

export type Machine = Schemas["MachineResponse"];
export type DirectoryEntry = Schemas["DirectoryEntry"];
export type SharedRoot = Schemas["SharedRoot"];
export type LoginResponse = Schemas["LoginResponse"];
export type RegisterResponse = Schemas["RegisterResponse"];
export type ClaimResponse = Schemas["ClaimResponse"];
export type Transfer = Schemas["TransferResponse"];
export type TransferUsage = Schemas["TransferUsageResponse"];
export type PatCreated = Schemas["PatCreatedResponse"];
export type Pat = Schemas["PatResponse"];
export type ProblemDetails = Schemas["ProblemDetails"];
export type ValidationProblemDetails = Schemas["HttpValidationProblemDetails"];

/**
 * int64 fields are typed `number | string | null`, because a long can exceed JavaScript's safe
 * integer range and the contract allows either form. File sizes we display are far below that,
 * so normalize once here instead of at every call site.
 */
export function sizeInBytes(entry: DirectoryEntry): number | null {
    if (entry.sizeBytes === null || entry.sizeBytes === undefined) {
        return null;
    }

    return typeof entry.sizeBytes === "string" ? Number(entry.sizeBytes) : entry.sizeBytes;
}

/** The same int64 normalization for any long-typed contract field. */
export function asByteCount(value: number | string | null | undefined): number {
    if (value === null || value === undefined) {
        return 0;
    }

    return typeof value === "string" ? Number(value) : value;
}
