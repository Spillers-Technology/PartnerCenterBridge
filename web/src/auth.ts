import { UserManager, User, WebStorageStateStore } from "oidc-client-ts";

const authority = import.meta.env.VITE_OIDC_AUTHORITY as string | undefined;
const clientId = import.meta.env.VITE_OIDC_CLIENT_ID as string | undefined;

/** OIDC is optional: when no authority is configured the app runs unauthenticated (local dev). */
export const authEnabled = Boolean(authority && clientId);

const manager: UserManager | null = authEnabled
  ? new UserManager({
      authority: authority!,
      client_id: clientId!,
      redirect_uri: window.location.origin,
      response_type: "code",
      scope: "openid profile email",
      userStore: new WebStorageStateStore({ store: window.localStorage })
    })
  : null;

export async function initAuth(): Promise<User | null> {
  if (!manager) return null;
  // Complete a redirect callback if we are returning from the IdP.
  if (window.location.search.includes("code=")) {
    const user = await manager.signinRedirectCallback();
    window.history.replaceState({}, document.title, window.location.pathname);
    return user;
  }
  return manager.getUser();
}

export async function login() {
  await manager?.signinRedirect();
}

export async function logout() {
  await manager?.signoutRedirect();
}

export async function getAccessToken(): Promise<string | null> {
  const user = await manager?.getUser();
  return user?.access_token ?? null;
}
