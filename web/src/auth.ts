import { PublicClientApplication, type AccountInfo } from "@azure/msal-browser";
import { config } from "./config";

export type Session = { token: string; name: string; mode: "dev" | "entra" };

const STORAGE_KEY = "docassistant.session";

export function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Session) : null;
  } catch {
    return null;
  }
}

export function saveSession(session: Session | null): void {
  if (session) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  else sessionStorage.removeItem(STORAGE_KEY);
}

/** Development sign-in: the API mints a token for a named test user. Only available when the API runs in dev auth mode. */
export async function devSignIn(user: string): Promise<Session> {
  const resp = await fetch(`${config.apiBase}/auth/dev-token`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ user }),
  });
  if (!resp.ok) throw new Error(`sign-in failed (${resp.status})`);
  const body = (await resp.json()) as { token: string; user: string };
  return { token: body.token, name: body.user, mode: "dev" };
}

let msal: PublicClientApplication | null = null;

async function client(): Promise<PublicClientApplication> {
  if (!msal) {
    msal = new PublicClientApplication({
      auth: {
        clientId: config.entra.clientId,
        authority: `https://login.microsoftonline.com/${config.entra.tenantId}`,
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: "sessionStorage" },
    });
    await msal.initialize();
  }
  return msal;
}

/** Microsoft Entra ID sign-in through MSAL; the access token carries the API scope and the user's oid. */
export async function entraSignIn(): Promise<Session> {
  const app = await client();
  const scopes = [config.entra.apiScope];
  const result = await app.loginPopup({ scopes });
  const account: AccountInfo | null = result.account ?? app.getAllAccounts()[0] ?? null;
  if (!account) throw new Error("no account after sign-in");
  const token = await app.acquireTokenSilent({ scopes, account });
  return { token: token.accessToken, name: account.name ?? account.username, mode: "entra" };
}

export async function signOut(session: Session | null): Promise<void> {
  saveSession(null);
  if (session?.mode === "entra" && msal) await msal.logoutPopup();
}
