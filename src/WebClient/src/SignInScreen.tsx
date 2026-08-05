import { useState } from "react";
import { login, register } from "./serverApi/auth";

interface SignInScreenProps {
    onSignedIn: () => void;
    onFailure: (error: unknown) => string;
    /** Why the last session ended, so an expiry does not look like the app vanishing. */
    notice?: string | null;
}

export function SignInScreen({ onSignedIn, onFailure, notice = null }: SignInScreenProps) {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [message, setMessage] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    async function run(action: () => Promise<void>) {
        setBusy(true);
        setMessage(null);
        try {
            await action();
        } catch (error) {
            setMessage(onFailure(error));
        } finally {
            setBusy(false);
        }
    }

    return (
        <section className="card sign-in">
            <h2>Sign in</h2>
            <form
                onSubmit={(event) => {
                    event.preventDefault();
                    void run(async () => {
                        await login(email, password);
                        onSignedIn();
                    });
                }}
            >
                <label>
                    Email
                    <input
                        type="email"
                        value={email}
                        autoComplete="username"
                        required
                        onChange={(event) => setEmail(event.target.value)}
                    />
                </label>
                <label>
                    Password
                    <input
                        type="password"
                        value={password}
                        autoComplete="current-password"
                        required
                        onChange={(event) => setPassword(event.target.value)}
                    />
                </label>
                <div className="row">
                    <button type="submit" disabled={busy}>
                        Sign in
                    </button>
                    <button
                        type="button"
                        disabled={busy}
                        onClick={() => {
                            void run(async () => {
                                await register(email, password);
                                await login(email, password);
                                onSignedIn();
                            });
                        }}
                    >
                        Create account
                    </button>
                </div>
            </form>
            {/* A failed attempt speaks for itself and takes precedence over the older notice. */}
            {(message ?? notice) !== null && <p className="error">{message ?? notice}</p>}
        </section>
    );
}
