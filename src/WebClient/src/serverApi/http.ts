import { serverUrl } from "../config";
import type { ProblemDetails, ValidationProblemDetails } from "./types";

const tokenStorageKey = "angstrom.accessToken";

let accessToken: string | null = null;

/** Restores the session from a previous visit; call once at startup. */
export function loadStoredAccessToken(): string | null {
    accessToken = localStorage.getItem(tokenStorageKey);
    return accessToken;
}

export function setAccessToken(token: string | null): void {
    accessToken = token;
    if (token === null) {
        localStorage.removeItem(tokenStorageKey);
    } else {
        localStorage.setItem(tokenStorageKey, token);
    }
}

export class ApiError extends Error {
    readonly status: number;

    constructor(status: number, message: string) {
        super(message);
        this.name = "ApiError";
        this.status = status;
    }

    /** The token is missing, expired, or its account is gone — the caller should log out. */
    get isUnauthorized(): boolean {
        return this.status === 401;
    }
}

interface RequestOptions {
    method?: "GET" | "POST" | "DELETE";
    body?: unknown;
    /** Sent as-is instead of JSON — file uploads stream their bytes this way. */
    rawBody?: Blob;
    /** Send without the bearer token (registration, login, and the Daemon-facing endpoints). */
    anonymous?: boolean;
}

export async function send(path: string, options: RequestOptions = {}): Promise<Response> {
    const headers = new Headers();
    if (options.body !== undefined) {
        headers.set("Content-Type", "application/json");
    } else if (options.rawBody !== undefined) {
        headers.set("Content-Type", "application/octet-stream");
    }

    if (options.anonymous !== true && accessToken !== null) {
        headers.set("Authorization", `Bearer ${accessToken}`);
    }

    const response = await fetch(new URL(path, serverUrl), {
        method: options.method ?? "GET",
        headers,
        body: options.body === undefined ? options.rawBody : JSON.stringify(options.body),
    });

    if (!response.ok) {
        throw new ApiError(response.status, await describeFailure(response));
    }

    return response;
}

export async function sendForJson<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const response = await send(path, options);
    return (await response.json()) as T;
}

/** Reads the message out of a ProblemDetails body, falling back to something readable. */
async function describeFailure(response: Response): Promise<string> {
    try {
        const problem = (await response.json()) as ProblemDetails & ValidationProblemDetails;
        const validationMessages = Object.values(problem.errors ?? {}).flat();
        if (validationMessages.length > 0) {
            return validationMessages.join(" ");
        }

        const message = problem.detail ?? problem.title;
        if (typeof message === "string" && message.length > 0) {
            return message;
        }
    } catch {
        // Body was empty or not JSON (e.g. a bare 401 challenge); fall through.
    }

    return describeStatus(response.status);
}

function describeStatus(status: number): string {
    if (status === 401) {
        return "Your session has expired. Please log in again.";
    }
    if (status === 404) {
        return "Not found.";
    }
    if (status >= 500) {
        return "Something went wrong on the server. Please try again.";
    }

    return `The request failed (HTTP ${status}).`;
}
