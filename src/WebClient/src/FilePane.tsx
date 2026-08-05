import { useEffect, useRef, useState } from "react";
import { formatSize, formatTimestamp, joinPath, parentPath } from "./formatting";
import {
    createDirectory,
    deleteEntry,
    downloadFile,
    listDirectory,
    listRoots,
    moveEntry,
    uploadFile,
} from "./serverApi/files";
import type { DirectoryEntry, Machine, SharedRoot } from "./serverApi/types";

export interface PaneState {
    registrationId: string | null;
    path: string;
}

interface FilePaneProps {
    title: string;
    machines: Machine[];
    state: PaneState;
    onStateChange: (state: PaneState) => void;
    /** The selected file, so the other pane can copy it here. */
    selectedEntry: DirectoryEntry | null;
    onSelect: (entry: DirectoryEntry | null) => void;
    onFailure: (error: unknown) => string;
    /** Bumped by the other pane after writing here, so this pane reloads. */
    reloadToken: number;
    onChanged: () => void;
    /** Lets the other pane see what is already here before copying over it. */
    onEntriesLoaded: (entries: DirectoryEntry[]) => void;
}

export function FilePane({
    title,
    machines,
    state,
    onStateChange,
    selectedEntry,
    onSelect,
    onFailure,
    reloadToken,
    onChanged,
    onEntriesLoaded,
}: FilePaneProps) {
    const [entries, setEntries] = useState<DirectoryEntry[] | null>(null);
    const [roots, setRoots] = useState<SharedRoot[] | null>(null);
    // Action feedback ("Deleted x.") and listing failures have different lifetimes: a
    // reload after an action must not wipe the action's own message, while a listing
    // error should clear itself the moment a later navigation succeeds.
    const [message, setMessage] = useState<string | null>(null);
    const [listError, setListError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const [localReload, setLocalReload] = useState(0);
    const fileInput = useRef<HTMLInputElement>(null);

    const { registrationId, path } = state;

    // The path box is a draft until the user commits it with Enter or Go. Writing every keystroke
    // into the pane's path would list a directory per character — each half-typed path a sandbox
    // refusal that empties the table and, on a deployed stamp, walks into the rate limit.
    const [draftPath, setDraftPath] = useState(path);
    const [shownPath, setShownPath] = useState(path);
    if (shownPath !== path) {
        // Navigation from anywhere else (a root pick, Up, opening a directory) wins over a draft.
        setShownPath(path);
        setDraftPath(path);
    }

    // What the machine shares: fetched on machine pick, and the first root becomes the
    // starting path — nobody should have to guess what a sandbox would accept.
    useEffect(() => {
        if (registrationId === null) {
            return;
        }

        let active = true;
        listRoots(registrationId)
            .then((shared) => {
                if (!active) {
                    return;
                }

                setRoots(shared);
                const first = shared[0];
                // Only seed a pane that has nowhere to be: a pane remounted with a path already
                // chosen (returning from Settings) must not be sent back to the first root.
                if (first !== undefined && path === "") {
                    onStateChange({ registrationId, path: first.path });
                }
            })
            .catch((error: unknown) => {
                if (active) {
                    setMessage(onFailure(error));
                }
            });

        return () => {
            active = false;
        };
        // Only a machine change should re-ask; the path is read for the seeding decision above
        // but deliberately stays out of this list.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [registrationId, onFailure]);

    // State is only touched in the async continuations: an effect that sets state synchronously
    // costs an extra render pass (and the hooks lint rule rightly objects).
    useEffect(() => {
        if (registrationId === null || path === "") {
            return;
        }

        // `active` decides whether a late answer may touch state; the controller stops the
        // request itself, so a superseded listing does not keep a relay round trip alive.
        let active = true;
        const cancellation = new AbortController();
        listDirectory(registrationId, path, cancellation.signal)
            .then((listed) => {
                if (!active) {
                    return;
                }

                setListError(null);
                const sorted = [...listed].sort(
                    (left, right) =>
                        Number(right.isDirectory) - Number(left.isDirectory) || left.name.localeCompare(right.name),
                );
                setEntries(sorted);
                onEntriesLoaded(sorted);
            })
            .catch((error: unknown) => {
                if (active) {
                    setEntries(null);
                    setListError(onFailure(error));
                    onEntriesLoaded([]);
                }
            });

        return () => {
            active = false;
            cancellation.abort();
        };
    }, [registrationId, path, reloadToken, localReload, onFailure, onEntriesLoaded]);

    function reload() {
        setLocalReload((token) => token + 1);
    }

    function open(entry: DirectoryEntry) {
        if (entry.isDirectory) {
            onSelect(null);
            setMessage(null);
            onStateChange({ registrationId, path: joinPath(path, entry.name) });
        } else {
            onSelect(entry);
        }
    }

    /** Enter or Go: the only moments a typed path becomes the pane's location. */
    function goToDraftPath() {
        onSelect(null);
        setMessage(null);
        if (draftPath === path) {
            // Already there, so nothing would change and the listing effect would not rerun:
            // treat it as the refresh the user just asked for.
            reload();
        } else {
            onStateChange({ registrationId, path: draftPath });
        }
    }

    function download(entry: DirectoryEntry) {
        if (registrationId === null) {
            return;
        }

        setBusy(true);
        downloadFile(registrationId, joinPath(path, entry.name))
            .then((file) => {
                const url = URL.createObjectURL(file.blob);
                const link = document.createElement("a");
                link.href = url;
                link.download = file.fileName;
                link.click();
                // Revoking in the same tick cancels the download in some browsers: the click only
                // queues it, and the URL has to outlive that.
                setTimeout(() => URL.revokeObjectURL(url), 60_000);
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    function newFolder() {
        if (registrationId === null || path === "") {
            return;
        }

        const name = window.prompt("New folder name")?.trim();
        if (name === undefined || name === "") {
            return;
        }

        setBusy(true);
        setMessage(null);
        createDirectory(registrationId, joinPath(path, name))
            .then(() => {
                setMessage(`Created ${name}.`);
                onChanged();
                reload();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    function rename(entry: DirectoryEntry) {
        if (registrationId === null) {
            return;
        }

        const name = window.prompt(`Rename "${entry.name}" to`, entry.name)?.trim();
        if (name === undefined || name === "" || name === entry.name) {
            return;
        }

        setBusy(true);
        setMessage(null);
        moveEntry(registrationId, joinPath(path, entry.name), joinPath(path, name))
            .then(() => {
                setMessage(`Renamed to ${name}.`);
                onSelect(null);
                onChanged();
                reload();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    function remove(entry: DirectoryEntry) {
        if (registrationId === null) {
            return;
        }

        const warning = entry.isDirectory ? `Delete "${entry.name}" and everything in it?` : `Delete "${entry.name}"?`;
        if (!window.confirm(warning)) {
            return;
        }

        setBusy(true);
        setMessage(null);
        deleteEntry(registrationId, joinPath(path, entry.name))
            .then(() => {
                setMessage(`Deleted ${entry.name}.`);
                onSelect(null);
                onChanged();
                reload();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    function upload(file: File) {
        // Without a settled path every joinPath below lands at the filesystem root of whatever
        // this machine shares — a place the user never looked at, let alone chose.
        if (registrationId === null || path === "") {
            return;
        }

        // Replacing a file is a decision the user gets to make: overwrite is only requested for a
        // name already in this directory, and only after they say so. A directory that has not
        // finished listing counts as "not replacing" — the Server refuses rather than clobbers.
        const replacing = entries?.some((entry) => entry.name === file.name && !entry.isDirectory) ?? false;
        if (replacing && !window.confirm(`${file.name} already exists here. Replace it?`)) {
            return;
        }

        setBusy(true);
        setMessage(null);
        uploadFile(registrationId, joinPath(path, file.name), file, replacing)
            .then((transfer) => {
                setMessage(`Uploaded ${file.name} (${transfer.bytesTransferred.toString()} bytes).`);
                onChanged();
                reload();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    // Roots that came back empty are an answer, not a wait: with no shared folder the path stays
    // empty, the listing never runs, and "working…" would spin for as long as the pane is open.
    const located = registrationId !== null && path !== "";
    const loading = registrationId !== null && entries === null && listError === null && (roots === null || located);

    return (
        <section className="card pane">
            <div className="row spread">
                <h2>{title}</h2>
                {(busy || loading) && <span className="muted">working…</span>}
            </div>

            <div className="row">
                <select
                    value={registrationId ?? ""}
                    aria-label={`${title}: machine`}
                    onChange={(event) => {
                        onSelect(null);
                        setEntries(null);
                        // The other pane has to forget this listing too: entries it still held
                        // would otherwise decide whether a copy replaces a file on a machine
                        // they no longer describe.
                        onEntriesLoaded([]);
                        setRoots(null);
                        setMessage(null);
                        // The path resets with the machine: the roots fetch fills it in.
                        onStateChange({
                            registrationId: event.target.value === "" ? null : event.target.value,
                            path: "",
                        });
                    }}
                >
                    <option value="">Pick a machine…</option>
                    {machines.map((machine) => (
                        <option key={machine.registrationId} value={machine.registrationId} disabled={!machine.online}>
                            {machine.displayName}
                            {machine.online ? "" : " (offline)"}
                        </option>
                    ))}
                </select>
            </div>

            <div className="row">
                <select
                    value=""
                    aria-label={`${title}: shared folder`}
                    disabled={roots === null || roots.length === 0}
                    onChange={(event) => {
                        if (event.target.value !== "") {
                            onSelect(null);
                            onStateChange({ registrationId, path: event.target.value });
                        }
                    }}
                >
                    <option value="">Shared folders…</option>
                    {(roots ?? []).map((root) => (
                        <option key={root.path} value={root.path}>
                            {root.path}
                            {root.writable ? "" : " (read-only)"}
                        </option>
                    ))}
                </select>
                <button
                    type="button"
                    title="Up one level"
                    disabled={registrationId === null}
                    onClick={() => {
                        onSelect(null);
                        onStateChange({ registrationId, path: parentPath(path) });
                    }}
                >
                    ↑ Up
                </button>
                <input
                    className="path"
                    value={draftPath}
                    aria-label={`${title}: path`}
                    onChange={(event) => setDraftPath(event.target.value)}
                    onKeyDown={(event) => {
                        if (event.key === "Enter") {
                            goToDraftPath();
                        } else if (event.key === "Escape") {
                            setDraftPath(path);
                        }
                    }}
                />
                <button type="button" disabled={registrationId === null} onClick={goToDraftPath}>
                    Go
                </button>
                <button type="button" disabled={!located || busy} onClick={newFolder}>
                    New folder
                </button>
            </div>

            {registrationId !== null && roots !== null && roots.length === 0 && (
                <p className="muted">This machine shares no folders; its Daemon has no allowed roots configured.</p>
            )}

            {registrationId !== null && entries !== null && (
                <table>
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>Size</th>
                            <th>Modified</th>
                            <th />
                        </tr>
                    </thead>
                    <tbody>
                        {entries.map((entry) => (
                            <tr
                                key={entry.name}
                                className={selectedEntry?.name === entry.name ? "selected" : undefined}
                                tabIndex={0}
                                onClick={() => open(entry)}
                                onKeyDown={(event) => {
                                    // Only the row's own keys: Enter and Space on the buttons
                                    // inside it belong to those buttons.
                                    if (event.target !== event.currentTarget) {
                                        return;
                                    }

                                    if (event.key === "Enter" || event.key === " ") {
                                        // Space would scroll the page out from under the pane.
                                        event.preventDefault();
                                        open(entry);
                                    }
                                }}
                            >
                                <td>
                                    {entry.isDirectory ? "📁" : "📄"} {entry.name}
                                </td>
                                <td className="muted">{formatSize(entry)}</td>
                                <td className="muted">{formatTimestamp(entry.modifiedAt)}</td>
                                <td>
                                    <div className="row">
                                        {!entry.isDirectory && (
                                            <button
                                                type="button"
                                                disabled={busy}
                                                onClick={(event) => {
                                                    event.stopPropagation();
                                                    download(entry);
                                                }}
                                            >
                                                Download
                                            </button>
                                        )}
                                        <button
                                            type="button"
                                            disabled={busy}
                                            onClick={(event) => {
                                                event.stopPropagation();
                                                rename(entry);
                                            }}
                                        >
                                            Rename
                                        </button>
                                        <button
                                            type="button"
                                            disabled={busy}
                                            onClick={(event) => {
                                                event.stopPropagation();
                                                remove(entry);
                                            }}
                                        >
                                            Delete
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}

            <div className="row">
                <input
                    type="file"
                    ref={fileInput}
                    aria-label={`${title}: upload a file`}
                    disabled={!located}
                    onChange={(event) => {
                        const file = event.target.files?.[0];
                        if (file !== undefined) {
                            upload(file);
                        }
                        event.target.value = "";
                    }}
                />
            </div>

            {(message ?? listError) !== null && <p className="notice">{message ?? listError}</p>}
        </section>
    );
}
