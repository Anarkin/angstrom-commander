import { describe, expect, test } from "vitest";
import { formatSize, joinPath, parentPath } from "./formatting";

describe("joinPath", () => {
  test("keeps forward slashes on POSIX paths", () => {
    expect(joinPath("/data", "notes.txt")).toBe("/data/notes.txt");
    expect(joinPath("/data/", "notes.txt")).toBe("/data/notes.txt");
    expect(joinPath("/", "data")).toBe("/data");
  });

  test("keeps backslashes on Windows paths", () => {
    // A Daemon on Windows reports paths this way, and the Up button used to send them back
    // as "/" — a path its sandbox could only refuse.
    expect(joinPath("C:\\Users\\me", "notes.txt")).toBe("C:\\Users\\me\\notes.txt");
    expect(joinPath("C:\\", "Users")).toBe("C:\\Users");
    expect(joinPath("\\\\server\\share", "notes.txt")).toBe("\\\\server\\share\\notes.txt");
  });
});

describe("parentPath", () => {
  test("walks up a POSIX path and stops at the root", () => {
    expect(parentPath("/data/projects")).toBe("/data");
    expect(parentPath("/data/projects/")).toBe("/data");
    expect(parentPath("/data")).toBe("/");
    expect(parentPath("/")).toBe("/");
  });

  test("walks up a Windows path and stops at the drive", () => {
    expect(parentPath("C:\\Users\\me\\Documents")).toBe("C:\\Users\\me");
    expect(parentPath("C:\\Users")).toBe("C:\\");
    expect(parentPath("C:\\")).toBe("C:\\");
  });
});

describe("formatSize", () => {
  const entry = (sizeBytes: number | null) => ({
    name: "f",
    isDirectory: false,
    sizeBytes,
    modifiedAt: "2026-07-30T09:00:00Z",
  });

  test("scales to the unit that reads best", () => {
    expect(formatSize(entry(512))).toBe("512 B");
    expect(formatSize(entry(2048))).toBe("2.0 KB");
    expect(formatSize(entry(5 * 1024 * 1024))).toBe("5.0 MB");
    expect(formatSize(entry(3 * 1024 * 1024 * 1024))).toBe("3.0 GB");
  });

  test("shows nothing for a directory, which has no size", () => {
    expect(formatSize(entry(null))).toBe("");
  });
});
