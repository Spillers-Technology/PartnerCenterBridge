// One-time launch tickets for the Local Workbench. PartnerCenterBridge.exe opens
// http://localhost:<port>/#ticket=<ticket> on first run (setup) and, for a workbench used without an
// account, on every launch (sign-in). The ticket rides in the fragment, which the browser never sends
// to a server (logs, proxies, Referer); the SPA takes it out of the address bar immediately and keeps
// it only in memory. It works once and expires after a few minutes, so a bookmark or a copied link
// is useless afterwards.

/**
 * Returns the ticket in the current URL's fragment, if any, and removes it from the address bar
 * (history.replaceState keeps the path and query, so the originally requested page stays).
 */
export function takeLaunchTicketFromUrl(): string | null {
  const hash = window.location.hash;
  if (!hash || hash.length < 2) return null;
  const params = new URLSearchParams(hash.slice(1));
  const ticket = params.get("ticket");
  if (ticket === null) return null;
  params.delete("ticket");
  const rest = params.toString();
  const url = `${window.location.pathname}${window.location.search}${rest ? `#${rest}` : ""}`;
  window.history.replaceState(window.history.state, "", url);
  return ticket.trim() || null;
}
