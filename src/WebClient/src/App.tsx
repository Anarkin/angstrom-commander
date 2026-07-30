import { useCallback, useEffect, useState } from "react";
import { FilePane } from "./FilePane";
import type { PaneState } from "./FilePane";
import { MachinesPanel } from "./MachinesPanel";
import { SignInScreen } from "./SignInScreen";
import { copyBetweenMachines, listMachines } from "./serverApi/files";
import type { DirectoryEntry, Machine } from "./serverApi/types";
import { joinPath } from "./formatting";
import { useSession } from "./useSession";

const emptyPane: PaneState = { registrationId: null, path: "/data" };

function App() {
  const { signedIn, signIn, signOut, handleFailure } = useSession();
  const [machines, setMachines] = useState<Machine[]>([]);
  const [left, setLeft] = useState<PaneState>(emptyPane);
  const [right, setRight] = useState<PaneState>({ registrationId: null, path: "/uploads" });
  const [leftSelection, setLeftSelection] = useState<DirectoryEntry | null>(null);
  const [rightSelection, setRightSelection] = useState<DirectoryEntry | null>(null);
  const [leftReload, setLeftReload] = useState(0);
  const [rightReload, setRightReload] = useState(0);
  const [transferMessage, setTransferMessage] = useState<string | null>(null);
  const [copying, setCopying] = useState(false);

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
    const [source, target, entry] =
      direction === "left-to-right" ? [left, right, leftSelection] : [right, left, rightSelection];
    if (entry === null || source.registrationId === null || target.registrationId === null) {
      return;
    }

    setCopying(true);
    setTransferMessage(null);
    copyBetweenMachines(
      source.registrationId,
      target.registrationId,
      joinPath(source.path, entry.name),
      joinPath(target.path, entry.name),
      true,
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
        <SignInScreen onSignedIn={signIn} onFailure={handleFailure} />
      </main>
    );
  }

  const canCopyRight = leftSelection !== null && left.registrationId !== null && right.registrationId !== null;
  const canCopyLeft = rightSelection !== null && left.registrationId !== null && right.registrationId !== null;

  return (
    <main className="wide">
      <div className="row spread">
        <h1>Angstrom Commander</h1>
        <button type="button" onClick={signOut}>
          Sign out
        </button>
      </div>

      <MachinesPanel machines={machines} onRefresh={refreshMachines} onFailure={handleFailure} />

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
    </main>
  );
}

export default App;
