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

/** Joins a directory and a child name with forward slashes (the Daemon canonicalizes anyway). */
export function joinPath(directory: string, name: string): string {
  return `${directory.replace(/\/+$/, "")}/${name}`;
}

/** The containing directory, or the same path when already at a root. */
export function parentPath(path: string): string {
  const trimmed = path.replace(/\/+$/, "");
  const lastSeparator = trimmed.lastIndexOf("/");
  if (lastSeparator <= 0) {
    return "/";
  }

  return trimmed.slice(0, lastSeparator);
}
