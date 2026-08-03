import { send, sendForJson } from "./http";
import type { DirectoryEntry, Machine, SharedRoot, Transfer, TransferUsage } from "./types";

export async function listMachines(): Promise<Machine[]> {
    return await sendForJson<Machine[]>("/api/daemons");
}

/** The folders a machine shares — the pane's valid starting points. */
export async function listRoots(registrationId: string): Promise<SharedRoot[]> {
    return await sendForJson<SharedRoot[]>(`/api/daemons/${registrationId}/roots`);
}

export async function createDirectory(registrationId: string, path: string): Promise<void> {
    const query = new URLSearchParams({ path });
    await send(`/api/daemons/${registrationId}/mkdir?${query.toString()}`, { method: "POST" });
}

/** Rename is a move with the same parent — one call covers both. */
export async function moveEntry(
    registrationId: string,
    sourcePath: string,
    targetPath: string,
    overwrite = false,
): Promise<void> {
    const query = new URLSearchParams({ sourcePath, targetPath, overwrite: String(overwrite) });
    await send(`/api/daemons/${registrationId}/move?${query.toString()}`, { method: "POST" });
}

/** Deletes a file, or a directory with everything in it. */
export async function deleteEntry(registrationId: string, path: string): Promise<void> {
    const query = new URLSearchParams({ path });
    await send(`/api/daemons/${registrationId}/entries?${query.toString()}`, { method: "DELETE" });
}

/** How much of the daily relay allowance is spent; limit 0 means none is configured. */
export async function getTransferUsage(): Promise<TransferUsage> {
    return await sendForJson<TransferUsage>("/api/transfers/usage");
}

export async function listDirectory(registrationId: string, path: string): Promise<DirectoryEntry[]> {
    const query = new URLSearchParams({ path });
    return await sendForJson<DirectoryEntry[]>(`/api/daemons/${registrationId}/list?${query.toString()}`);
}

export interface DownloadedFile {
    fileName: string;
    blob: Blob;
}

/**
 * Downloads a file through the relay. The bytes stream Daemon -> Server, but a browser cannot
 * put an Authorization header on a plain link, so the response is buffered into a Blob here —
 * fine for documents, not for very large files (a short-lived download ticket in the URL is the
 * eventual fix, letting the browser stream to disk natively).
 */
export async function downloadFile(registrationId: string, path: string): Promise<DownloadedFile> {
    const query = new URLSearchParams({ path });
    const response = await send(`/api/daemons/${registrationId}/download?${query.toString()}`);

    return {
        fileName: fileNameFrom(response.headers.get("Content-Disposition")) ?? baseNameOf(path),
        blob: await response.blob(),
    };
}

/** Streams a file to a machine, into one of its writable roots. */
export async function uploadFile(
    registrationId: string,
    path: string,
    content: Blob,
    overwrite = false,
): Promise<Transfer> {
    const query = new URLSearchParams({ path, overwrite: String(overwrite) });
    return await sendForJson<Transfer>(`/api/daemons/${registrationId}/upload?${query.toString()}`, {
        method: "POST",
        rawBody: content,
    });
}

/**
 * Copies a file directly between two machines. The bytes go source -> Server -> target without
 * a round trip through the browser, so this is what the dual pane's copy action uses rather
 * than a download followed by an upload.
 */
export async function copyBetweenMachines(
    sourceRegistrationId: string,
    targetRegistrationId: string,
    sourcePath: string,
    targetPath: string,
    overwrite = false,
): Promise<Transfer> {
    const query = new URLSearchParams({
        sourcePath,
        targetPath,
        overwrite: String(overwrite),
    });
    return await sendForJson<Transfer>(
        `/api/daemons/${sourceRegistrationId}/copy-to/${targetRegistrationId}?${query.toString()}`,
        { method: "POST" },
    );
}

function fileNameFrom(contentDisposition: string | null): string | null {
    if (contentDisposition === null) {
        return null;
    }

    // filename*=UTF-8''... (RFC 5987, preferred) or a plain quoted filename=...
    const encoded = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition);
    if (encoded?.[1] !== undefined) {
        return decodeURIComponent(encoded[1]);
    }

    return /filename="?([^";]+)"?/i.exec(contentDisposition)?.[1] ?? null;
}

function baseNameOf(path: string): string {
    return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path;
}
