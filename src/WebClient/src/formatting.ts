import { sizeInBytes } from "./serverApi/types";
import type { DirectoryEntry } from "./serverApi/types";

export function formatSize(entry: DirectoryEntry): string {
    const bytes = sizeInBytes(entry);
    if (bytes === null) {
        return "";
    }
    if (bytes < 1024) {
        return `${bytes} B`;
    }
    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }
    if (bytes < 1024 * 1024 * 1024) {
        return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    }

    return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

export function formatTimestamp(value: string): string {
    return new Date(value).toLocaleString();
}

/** `C:\...` or `\\server\share` — a Daemon on Windows reports paths this way. */
function isWindowsPath(path: string): boolean {
    return /^[a-z]:/i.test(path) || path.startsWith("\\\\");
}

/** `C:` on its own is not a path; the drive's root is `C:\`. */
function isDriveLetter(path: string): boolean {
    return /^[a-z]:$/i.test(path);
}

function trimTrailingSeparators(path: string): string {
    return path.replace(/[\\/]+$/, "");
}

/** Joins a directory and a child name, in the separator that directory is written with. */
export function joinPath(directory: string, name: string): string {
    const separator = isWindowsPath(directory) ? "\\" : "/";
    return `${trimTrailingSeparators(directory)}${separator}${name}`;
}

/** The containing directory, or the same path when already at a root. */
export function parentPath(path: string): string {
    const trimmed = trimTrailingSeparators(path);
    if (trimmed === "") {
        return "/";
    }
    if (isDriveLetter(trimmed)) {
        return `${trimmed}\\`;
    }

    const lastSeparator = Math.max(trimmed.lastIndexOf("/"), trimmed.lastIndexOf("\\"));
    if (lastSeparator < 0) {
        return trimmed;
    }
    if (lastSeparator === 0) {
        return "/";
    }

    const parent = trimmed.slice(0, lastSeparator);
    return isDriveLetter(parent) ? `${parent}\\` : parent;
}
