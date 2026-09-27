// URL builders for the routes in docs/specs/ops-workbench.md section C. Kept out of the route
// modules so a component can link to a page without pulling that page's lazy chunk in with it.

function withQuery(path: string, params: Record<string, string | undefined | null>): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v) q.set(k, v);
  const qs = q.toString();
  return qs ? `${path}?${qs}` : path;
}

export function personPath(tenantId: string, userId: string, tab?: string) {
  const base = `/people/${encodeURIComponent(tenantId)}/${encodeURIComponent(userId)}`;
  return tab && tab !== "summary" ? `${base}?tab=${encodeURIComponent(tab)}` : base;
}

/** Deep link into a known fix with the tenant and target user already filled in. */
export function workflowLink(workflowId: string, tenantId?: string, user?: string) {
  return withQuery(`/operations/workflows/${encodeURIComponent(workflowId)}`, { tenant: tenantId, user });
}

export function accessParityPath(opts: { tenant?: string; source?: string; target?: string } = {}) {
  return withQuery("/operations/access-parity", { tenant: opts.tenant, source: opts.source, target: opts.target });
}

export function offboardPath(tenantId?: string, user?: string) {
  return withQuery("/operations/offboard", { tenant: tenantId, user });
}

export function onboardPath(tenantId?: string) {
  return withQuery("/operations/onboard", { tenant: tenantId });
}

export function runPath(runId: string) {
  return `/activity/runs/${encodeURIComponent(runId)}`;
}

export function peopleSearchPath(q?: string) {
  return withQuery("/people", { q: q?.trim() });
}
