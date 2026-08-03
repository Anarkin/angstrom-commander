import { useCallback, useEffect, useState } from "react";
import { createPat, listPats, revokePat } from "./serverApi/auth";
import { formatTimestamp } from "./formatting";
import type { Pat } from "./serverApi/types";

interface AccessTokensProps {
    onFailure: (error: unknown) => string;
}

/**
 * Personal access tokens — what an MCP client (Claude Code, Claude Desktop, …) uses to
 * operate this account's machines. The token value is shown exactly once, at creation;
 * after that only its hash exists anywhere.
 */
export function AccessTokens({ onFailure }: AccessTokensProps) {
    const [tokens, setTokens] = useState<Pat[]>([]);
    const [freshToken, setFreshToken] = useState<string | null>(null);
    const [message, setMessage] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    const refresh = useCallback(() => {
        listPats()
            .then(setTokens)
            .catch((error: unknown) => setMessage(onFailure(error)));
    }, [onFailure]);

    useEffect(refresh, [refresh]);

    function create() {
        const name = window.prompt("Name this token after the tool that will hold it (e.g. Claude Code)")?.trim();
        if (name === undefined || name === "") {
            return;
        }

        setBusy(true);
        setMessage(null);
        createPat(name)
            .then((created) => {
                setFreshToken(created.token);
                refresh();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    function revoke(token: Pat) {
        if (!window.confirm(`Revoke "${token.name}"? The tool holding it loses access immediately.`)) {
            return;
        }

        setBusy(true);
        setMessage(null);
        revokePat(token.id)
            .then(() => {
                setMessage(`Revoked ${token.name}.`);
                refresh();
            })
            .catch((error: unknown) => setMessage(onFailure(error)))
            .finally(() => setBusy(false));
    }

    return (
        <section className="card">
            <div className="row spread">
                <h2>Access tokens</h2>
                <button type="button" disabled={busy} onClick={create}>
                    Create token
                </button>
            </div>
            <p className="muted">
                For MCP clients like Claude Code: a token lets the tool operate your machines as you, and can be revoked
                here at any time.
            </p>

            {freshToken !== null && (
                <p className="notice">
                    Copy it now — it will never be shown again: <code className="token">{freshToken}</code>
                </p>
            )}

            {tokens.length === 0 ? (
                <p className="muted">No tokens.</p>
            ) : (
                <ul className="machines">
                    {tokens.map((token) => (
                        <li key={token.id}>
                            <strong>{token.name}</strong>
                            <span className="muted">created {formatTimestamp(token.createdAt)}</span>
                            <span className="muted">
                                {token.lastUsedAt === null || token.lastUsedAt === undefined
                                    ? "never used"
                                    : `last used ${formatTimestamp(token.lastUsedAt)}`}
                            </span>
                            <button type="button" disabled={busy} onClick={() => revoke(token)}>
                                Revoke
                            </button>
                        </li>
                    ))}
                </ul>
            )}

            {message !== null && <p className="notice">{message}</p>}
        </section>
    );
}
