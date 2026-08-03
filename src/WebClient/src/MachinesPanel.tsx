import { useState } from "react";
import { claimPairingCode } from "./serverApi/auth";
import type { Machine } from "./serverApi/types";

interface MachinesPanelProps {
    machines: Machine[];
    onRefresh: () => void;
    onFailure: (error: unknown) => string;
}

export function MachinesPanel({ machines, onRefresh, onFailure }: MachinesPanelProps) {
    const [code, setCode] = useState("");
    const [displayName, setDisplayName] = useState("");
    const [message, setMessage] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    function claim() {
        setBusy(true);
        setMessage(null);
        claimPairingCode(code.trim().toUpperCase(), displayName.trim())
            .then((claimed) => {
                setMessage(`Paired ${claimed.displayName}. It comes online within a few seconds.`);
                setCode("");
                setDisplayName("");
                onRefresh();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    return (
        <section className="card">
            <div className="row spread">
                <h2>My machines</h2>
                <button type="button" onClick={onRefresh}>
                    Refresh
                </button>
            </div>

            {machines.length === 0 ? (
                <p className="muted">No machines paired yet.</p>
            ) : (
                <ul className="machines">
                    {machines.map((machine) => (
                        <li key={machine.registrationId}>
                            <span className={machine.online ? "dot online" : "dot"} aria-hidden="true" />
                            <strong>{machine.displayName}</strong>
                            <span className="muted">{machine.platform}</span>
                            <span className="muted">{machine.online ? "online" : "offline"}</span>
                        </li>
                    ))}
                </ul>
            )}

            <details>
                <summary>Pair a machine</summary>
                <p className="muted">The Daemon prints a pairing code on startup until it is claimed.</p>
                <div className="row">
                    <input
                        value={code}
                        placeholder="PAIRING CODE"
                        className="code"
                        onChange={(event) => setCode(event.target.value)}
                    />
                    <input
                        value={displayName}
                        placeholder="Name this machine"
                        onChange={(event) => setDisplayName(event.target.value)}
                    />
                    <button
                        type="button"
                        disabled={busy || code.trim() === "" || displayName.trim() === ""}
                        onClick={claim}
                    >
                        Pair
                    </button>
                </div>
            </details>

            {message !== null && <p className="notice">{message}</p>}
        </section>
    );
}
