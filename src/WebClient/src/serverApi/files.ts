import { send, sendForJson } from "./http";
import type { DirectoryEntry, Machine } from "./types";

export async function listMachines(): Promise<Machine[]> {
  return await sendForJson<Machine[]>("/api/daemons");
}

export async function listDirectory(
  registrationId: string,
  path: string,
): Promise<DirectoryEntry[]> {
  const query = new URLSearchParams({ path });
  return await sendForJson<DirectoryEntry[]>(
    `/api/daemons/${registrationId}/list?${query.toString()}`,
  );
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
export async function downloadFile(
  registrationId: string,
  path: string,
): Promise<DownloadedFile> {
  const query = new URLSearchParams({ path });
  const response = await send(
    `/api/daemons/${registrationId}/download?${query.toString()}`,
  );

  return {
    fileName:
      fileNameFrom(response.headers.get("Content-Disposition")) ??
      baseNameOf(path),
    blob: await response.blob(),
  };
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
