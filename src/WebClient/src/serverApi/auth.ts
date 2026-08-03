import { send, sendForJson, setAccessToken } from "./http";
import type { ClaimResponse, LoginResponse, RegisterResponse } from "./types";

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

export { send };
