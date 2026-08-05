import { useCallback, useEffect, useState } from "react";
import { FilePane } from "./FilePane";
import type { PaneState } from "./FilePane";
import { AccessTokens } from "./AccessTokens";
import { MachinesPanel } from "./MachinesPanel";
import { SignInScreen } from "./SignInScreen";
import { TransferAllowance } from "./TransferAllowance";
import { copyBetweenMachines, listMachines } from "./serverApi/files";
import type { DirectoryEntry, Machine } from "./serverApi/types";
import { joinPath } from "./formatting";
import { useSession } from "./useSession";

// Paths start empty: picking a machine fills them with its first shared root.
const emptyPane: PaneState = { registrationId: null, path: "" };

function App() {
    const { signedIn, signIn, signOut, handleFailure, endedMessage } = useSession();
    const [machines, setMachines] = useState<Machine[]>([]);
    const [left, setLeft] = useState<PaneState>(emptyPane);
    const [right, setRight] = useState<PaneState>(emptyPane);
    const [leftSelection, setLeftSelection] = useState<DirectoryEntry | null>(null);
    const [rightSelection, setRightSelection] = useState<DirectoryEntry | null>(null);
    const [leftEntries, setLeftEntries] = useState<DirectoryEntry[]>([]);
    const [rightEntries, setRightEntries] = useState<DirectoryEntry[]>([]);
    const [leftReload, setLeftReload] = useState(0);
    const [rightReload, setRightReload] = useState(0);
    const [transferMessage, setTransferMessage] = useState<string | null>(null);
    const [copying, setCopying] = useState(false);
    const [view, setView] = useState<"commander" | "settings">("commander");

    const refreshMachines = useCallback(() => {
        listMachines()
            .then(setMachines)
            .catch((error: unknown) => setTransferMessage(handleFailure(error)));
    }, [handleFailure]);

    useEffect(() => {
        if (signedIn) {
            refreshMachines();
        }
    }, [signedIn, refreshMachines]);

    /**
     * Copies the selected file to the other pane's directory. The bytes never come through the
     * browser — the Server streams them straight from one machine to the other.
     */
    function copy(direction: "left-to-right" | "right-to-left") {
        const [source, target, entry, targetEntries] =
            direction === "left-to-right"
                ? [left, right, leftSelection, rightEntries]
                : [right, left, rightSelection, leftEntries];
        if (entry === null || source.registrationId === null || target.registrationId === null) {
            return;
        }

        // A pane that has not settled on a path yet would send the file to `/name`: the root of
        // whatever the target machine shares, which is nowhere the user pointed at.
        if (source.path === "" || target.path === "") {
            return;
        }

        // Overwriting is the user's call, not a default: only ask the Server to replace a file
        // when there is one to replace and they have said so.
        const replacing = targetEntries.some((existing) => existing.name === entry.name && !existing.isDirectory);
        if (replacing && !window.confirm(`${entry.name} already exists in ${target.path}. Replace it?`)) {
            return;
        }

        setCopying(true);
        setTransferMessage(null);
        copyBetweenMachines(
            source.registrationId,
            target.registrationId,
            joinPath(source.path, entry.name),
            joinPath(target.path, entry.name),
            replacing,
        )
            .then((transfer) => {
                setTransferMessage(`Copied ${entry.name} (${transfer.bytesTransferred.toString()} bytes).`);
                if (direction === "left-to-right") {
                    setRightReload((token) => token + 1);
                } else {
                    setLeftReload((token) => token + 1);
                }
            })
            .catch((error: unknown) => setTransferMessage(handleFailure(error)))
            .finally(() => setCopying(false));
    }

    if (!signedIn) {
        return (
            <main>
                <h1>Angstrom Commander</h1>
                <p className="muted">All your machines, an ångström apart.</p>
                <SignInScreen onSignedIn={signIn} onFailure={handleFailure} notice={endedMessage} />
            </main>
        );
    }

    // Both panes must be somewhere real: a machine alone is not enough, because a pane whose
    // roots have not resolved still has an empty path and no listing to compare names against.
    const panesReady =
        left.registrationId !== null && left.path !== "" && right.registrationId !== null && right.path !== "";
    const canCopyRight = panesReady && leftSelection !== null;
    const canCopyLeft = panesReady && rightSelection !== null;

    return (
        <main className="wide">
            <div className="row spread">
                <h1>Angstrom Commander</h1>
                <div className="row">
                    <button type="button" onClick={() => setView(view === "settings" ? "commander" : "settings")}>
                        {view === "settings" ? "Hide settings" : "Settings"}
                    </button>
                    <button type="button" onClick={signOut}>
                        Sign out
                    </button>
                </div>
            </div>

            {view === "settings" && (
                <>
                    <MachinesPanel machines={machines} onRefresh={refreshMachines} onFailure={handleFailure} />
                    <AccessTokens onFailure={handleFailure} />
                    <TransferAllowance onFailure={handleFailure} />
                </>
            )}

            {/* Rendered only in the commander view: `hidden` cannot hide these, because the
                author styles for .panes and .row set `display` and outrank the UA stylesheet's
                `[hidden] { display: none }` — the file manager stayed live under Settings. */}
            {view === "commander" && (
                <>
                    <div className="panes">
                        <FilePane
                            title="Left"
                            machines={machines}
                            state={left}
                            onStateChange={setLeft}
                            selectedEntry={leftSelection}
                            onSelect={setLeftSelection}
                            onFailure={handleFailure}
                            reloadToken={leftReload}
                            onChanged={() => setRightReload((token) => token + 1)}
                            onEntriesLoaded={setLeftEntries}
                        />
                        <FilePane
                            title="Right"
                            machines={machines}
                            state={right}
                            onStateChange={setRight}
                            selectedEntry={rightSelection}
                            onSelect={setRightSelection}
                            onFailure={handleFailure}
                            reloadToken={rightReload}
                            onChanged={() => setLeftReload((token) => token + 1)}
                            onEntriesLoaded={setRightEntries}
                        />
                    </div>

                    <div className="row center">
                        <button type="button" disabled={!canCopyRight || copying} onClick={() => copy("left-to-right")}>
                            Copy →
                        </button>
                        <button type="button" disabled={!canCopyLeft || copying} onClick={() => copy("right-to-left")}>
                            ← Copy
                        </button>
                    </div>

                    {transferMessage !== null && <p className="notice center">{transferMessage}</p>}
                </>
            )}
        </main>
    );
}

export default App;
