// Launch-link sign-in for a Local Workbench used without an account. PartnerCenterBridge.exe opens
// http://localhost:<port>/#launch=<secret>: the secret rides in the fragment, which the browser never
// sends to a server (logs, proxies, Referer). The SPA takes it out of the address bar immediately and
// exchanges it for a normal session token.

/**
 * Returns the launch secret in the current URL's fragment, if any, and removes it from the address
 * bar (history.replaceState keeps the path and query, so the originally requested page stays).
 */
export function takeLaunchSecretFromUrl(): string | null {
  const hash = window.location.hash;
  if (!hash || hash.length < 2) return null;
  const params = new URLSearchParams(hash.slice(1));
  const secret = params.get("launch");
  if (secret === null) return null;
  params.delete("launch");
  const rest = params.toString();
  const url = `${window.location.pathname}${window.location.search}${rest ? `#${rest}` : ""}`;
  window.history.replaceState(window.history.state, "", url);
  return secret.trim() || null;
}
