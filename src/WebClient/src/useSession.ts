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

  const signIn = useCallback(() => {
    setSignedIn(true);
  }, []);

  const signOut = useCallback(() => {
    clearSession();
    setSignedIn(false);
  }, []);

  /** Returns a message to show; ends the session first when the token is no longer good. */
  const handleFailure = useCallback(
    (error: unknown): string => {
      if (error instanceof ApiError) {
        if (error.isUnauthorized) {
          signOut();
        }

        return error.message;
      }

      return error instanceof Error ? error.message : "Something went wrong.";
    },
    [signOut],
  );

  return { signedIn, signIn, signOut, handleFailure };
}
