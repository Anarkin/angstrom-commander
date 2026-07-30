import { useEffect, useRef, useState } from "react";
import { formatSize, formatTimestamp, joinPath, parentPath } from "./formatting";
import { downloadFile, listDirectory, uploadFile } from "./serverApi/files";
import type { DirectoryEntry, Machine } from "./serverApi/types";

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
}: FilePaneProps) {
  const [entries, setEntries] = useState<DirectoryEntry[] | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [localReload, setLocalReload] = useState(0);
  const fileInput = useRef<HTMLInputElement>(null);

  const { registrationId, path } = state;

  // State is only touched in the async continuations: an effect that sets state synchronously
  // costs an extra render pass (and the hooks lint rule rightly objects).
  useEffect(() => {
    if (registrationId === null) {
      return;
    }

    let active = true;
    listDirectory(registrationId, path)
      .then((listed) => {
        if (!active) {
          return;
        }

        setMessage(null);
        setEntries(
          [...listed].sort(
            (left, right) =>
              Number(right.isDirectory) - Number(left.isDirectory) || left.name.localeCompare(right.name),
          ),
        );
      })
      .catch((error: unknown) => {
        if (active) {
          setEntries(null);
          setMessage(onFailure(error));
        }
      });

    return () => {
      active = false;
    };
  }, [registrationId, path, reloadToken, localReload, onFailure]);

  function reload() {
    setLocalReload((token) => token + 1);
  }

  function open(entry: DirectoryEntry) {
    if (entry.isDirectory) {
      onSelect(null);
      onStateChange({ registrationId, path: joinPath(path, entry.name) });
    } else {
      onSelect(entry);
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
        URL.revokeObjectURL(url);
      })
      .catch((error: unknown) => setMessage(onFailure(error)))
      .finally(() => setBusy(false));
  }

  function upload(file: File) {
    if (registrationId === null) {
      return;
    }

    setBusy(true);
    setMessage(null);
    uploadFile(registrationId, joinPath(path, file.name), file, true)
      .then((transfer) => {
        setMessage(`Uploaded ${file.name} (${transfer.bytesTransferred.toString()} bytes).`);
        onChanged();
        reload();
      })
      .catch((error: unknown) => setMessage(onFailure(error)))
      .finally(() => setBusy(false));
  }

  const loading = registrationId !== null && entries === null && message === null;

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
            setMessage(null);
            onStateChange({ registrationId: event.target.value === "" ? null : event.target.value, path });
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
          value={path}
          aria-label={`${title}: path`}
          onChange={(event) => onStateChange({ registrationId, path: event.target.value })}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              reload();
            }
          }}
        />
        <button type="button" disabled={registrationId === null} onClick={reload}>
          Go
        </button>
      </div>

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
                onClick={() => open(entry)}
              >
                <td>
                  {entry.isDirectory ? "📁" : "📄"} {entry.name}
                </td>
                <td className="muted">{formatSize(entry)}</td>
                <td className="muted">{formatTimestamp(entry.modifiedAt)}</td>
                <td>
                  {!entry.isDirectory && (
                    <button
                      type="button"
                      onClick={(event) => {
                        event.stopPropagation();
                        download(entry);
                      }}
                    >
                      Download
                    </button>
                  )}
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
          disabled={registrationId === null}
          onChange={(event) => {
            const file = event.target.files?.[0];
            if (file !== undefined) {
              upload(file);
            }
            event.target.value = "";
          }}
        />
      </div>

      {message !== null && <p className="notice">{message}</p>}
    </section>
  );
}
