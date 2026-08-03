import { useEffect, useState } from "react";
import { formatBytes } from "./formatting";
import { getTransferUsage } from "./serverApi/files";
import { asByteCount } from "./serverApi/types";
import type { TransferUsage } from "./serverApi/types";

interface TransferAllowanceProps {
    onFailure: (error: unknown) => string;
}

const relayOnlyNote =
    "Counts transfers relayed through the cloud (downloads, uploads, and machine-to-machine copies). " +
    "Direct machine-to-machine (P2P) connections, once available, will not count.";

/** Today's relay allowance as a progress bar; renders nothing where no limit is configured. */
export function TransferAllowance({ onFailure }: TransferAllowanceProps) {
    const [usage, setUsage] = useState<TransferUsage | null>(null);
    const [message, setMessage] = useState<string | null>(null);

    useEffect(() => {
        getTransferUsage()
            .then(setUsage)
            .catch((error: unknown) => setMessage(onFailure(error)));
    }, [onFailure]);

    if (message !== null) {
        return <p className="notice">{message}</p>;
    }

    const limit = asByteCount(usage?.dailyLimitBytes);
    if (usage === null || limit <= 0) {
        return null;
    }

    const used = asByteCount(usage.bytesUsedToday);
    return (
        <section className="card">
            <h2>
                Daily transfer allowance{" "}
                <span title={relayOnlyNote} aria-label={relayOnlyNote} className="hint">
                    ⓘ
                </span>
            </h2>
            <progress value={used} max={limit} />
            <p className="muted">
                {formatBytes(used)} of {formatBytes(limit)} used today — resets at midnight UTC.
            </p>
        </section>
    );
}
