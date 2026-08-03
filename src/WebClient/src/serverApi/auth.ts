import { send, sendForJson, setAccessToken } from "./http";
import type { ClaimResponse, LoginResponse, Pat, PatCreated, RegisterResponse } from "./types";

export async function register(email: string, password: string): Promise<RegisterResponse> {
    return await sendForJson<RegisterResponse>("/api/auth/register", {
        method: "POST",
        body: { email, password },
        anonymous: true,
    });
}

/** Logs in and keeps the token for subsequent calls. */
export async function login(email: string, password: string): Promise<LoginResponse> {
    const result = await sendForJson<LoginResponse>("/api/auth/login", {
        method: "POST",
        body: { email, password },
        anonymous: true,
    });
    setAccessToken(result.accessToken);
    return result;
}

export function logout(): void {
    setAccessToken(null);
}

/** Pairs the machine displaying `code` with the logged-in account. */
export async function claimPairingCode(code: string, displayName: string): Promise<ClaimResponse> {
    return await sendForJson<ClaimResponse>("/api/enrollment/claim", {
        method: "POST",
        body: { code, displayName },
    });
}

/** Unpairs a machine; it needs a fresh pairing code to ever come back. */
export async function unpairMachine(registrationId: string): Promise<void> {
    await send(`/api/daemons/${registrationId}`, { method: "DELETE" });
}

/** Mints a personal access token — the response is the ONLY time its value exists. */
export async function createPat(name: string): Promise<PatCreated> {
    return await sendForJson<PatCreated>("/api/pats", { method: "POST", body: { name } });
}

export async function listPats(): Promise<Pat[]> {
    return await sendForJson<Pat[]>("/api/pats");
}

export async function revokePat(id: string): Promise<void> {
    await send(`/api/pats/${id}`, { method: "DELETE" });
}

export { send };
