// Token storage for Auth:Mode=Local. Kept separate from auth.ts (OIDC) since the two auth planes
// are mutually exclusive per deployment but api.ts shouldn't need to know which one is active.
const LOCAL_TOKEN_KEY = "pcb.local.accessToken";

export function setLocalToken(token: string) {
  localStorage.setItem(LOCAL_TOKEN_KEY, token);
}

export function clearLocalToken() {
  localStorage.removeItem(LOCAL_TOKEN_KEY);
}

export function getLocalToken(): string | null {
  return localStorage.getItem(LOCAL_TOKEN_KEY);
}
