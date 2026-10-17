import { useCallback, useEffect, useState } from "react";
import { api } from "./api";
import { devSignIn, entraSignIn, loadSession, saveSession, signOut, type Session } from "./auth";
import { config } from "./config";
import { Chat } from "./components/Chat";
import { Costs } from "./components/Costs";
import { Documents } from "./components/Documents";

export function App() {
  const [session, setSession] = useState<Session | null>(() => loadSession());
  const [user, setUser] = useState("demo");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    if (!session) return;
    api.me(session.token).catch(() => {
      saveSession(null);
      setSession(null);
    });
  }, [session]);

  const signIn = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const s = config.authMode === "entra" ? await entraSignIn() : await devSignIn(user.trim());
      saveSession(s);
      setSession(s);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [user]);

  const out = useCallback(async () => {
    await signOut(session);
    setSession(null);
  }, [session]);

  return (
    <div className="app">
      <header>
        <h1>Document assistant</h1>
        <p className="tagline">Upload invoices, contracts and manuals; ask questions with citations; extract fields.</p>
        <div className="session">
          {session ? (
            <>
              <span data-testid="signed-in">
                Signed in as <strong>{session.name}</strong>
              </span>
              <button onClick={out}>Sign out</button>
            </>
          ) : config.authMode === "entra" ? (
            <button onClick={signIn} disabled={busy}>
              Sign in with Microsoft
            </button>
          ) : (
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void signIn();
              }}
            >
              <label>
                Test user
                <input value={user} onChange={(e) => setUser(e.target.value)} pattern="[A-Za-z0-9._-]{1,64}" required aria-label="test user" />
              </label>
              <button type="submit" disabled={busy}>
                Sign in (development)
              </button>
            </form>
          )}
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </header>
      {session && (
        <main>
          <Documents token={session.token} onChanged={() => setRefreshKey((k) => k + 1)} />
          <Chat token={session.token} refreshKey={refreshKey} />
          <Costs token={session.token} refreshKey={refreshKey} />
        </main>
      )}
      <footer>
        <span>API: {config.apiBase || window.location.origin}</span>
        <span>Auth: {config.authMode}</span>
      </footer>
    </div>
  );
}
