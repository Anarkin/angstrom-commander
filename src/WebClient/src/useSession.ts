import { useCallback, useState } from "react";
import { logout as clearSession } from "./serverApi/auth";
import { ApiError, loadStoredAccessToken } from "./serverApi/http";

/**
 * Whether we hold a token. Any 401 from the Server means it is no longer usable (expired, or its
 * account is gone), so every screen funnels failures through `handleFailure` to end the session
 * instead of leaving the user staring at errors.
 */
export function useSession() {
    const [signedIn, setSignedIn] = useState(() => loadStoredAccessToken() !== null);
    // Why the session ended, for the sign-in screen to explain: an expiry unmounts whatever was
    // about to show the message, so without this the app would just vanish mid-action.
    const [endedMessage, setEndedMessage] = useState<string | null>(null);

    const signIn = useCallback(() => {
        setEndedMessage(null);
        setSignedIn(true);
    }, []);

    const endSession = useCallback((reason: string | null) => {
        clearSession();
        setEndedMessage(reason);
        setSignedIn(false);
    }, []);

    // Deliberately argument-free: it is wired straight to the Sign out button, and a click
    // handler would otherwise hand a MouseEvent to `endSession`.
    const signOut = useCallback(() => {
        endSession(null);
    }, [endSession]);

    /** Returns a message to show; ends the session first when the token is no longer good. */
    const handleFailure = useCallback(
        (error: unknown): string => {
            if (error instanceof ApiError) {
                if (error.isUnauthorized) {
                    endSession(error.message);
                }

                return error.message;
            }

            return error instanceof Error ? error.message : "Something went wrong.";
        },
        [endSession],
    );

    return { signedIn, signIn, signOut, handleFailure, endedMessage };
}
