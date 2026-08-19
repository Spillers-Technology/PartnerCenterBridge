import { getAccessToken } from "./auth";
import { getLocalToken } from "./session";
import type {
  AppTemplate, AuthMode, AuthResponse, ConfigSection, ConfigSnapshotRun, Contract, Dashboard,
  Deployment, DiagnosisResult, DirectoryObject, GlobalSearchResult, MeProfile, MfaChallengeResponse,
  PasskeyInfo, PendingAction, ProvisioningResult, ProvisioningTemplate, SectionDiff, Sku, Tenant, TenantGrant,
  TenantRole, TotpEnrollResponse, TotpVerifyEnrollResponse, WorkflowRunRecord, WorkflowRunResult,
  WorkflowSummary
} from "./types";

const base = (import.meta.env.VITE_API_BASE as string | undefined) ?? "";

async function authHeaders(init: RequestInit = {}): Promise<Headers> {
  // OIDC and Auth:Mode=Local are mutually exclusive per deployment; whichever produced a token wins.
  const token = (await getAccessToken()) ?? getLocalToken();
  const headers = new Headers(init.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (init.body && !(init.body instanceof FormData)) headers.set("Content-Type", "application/json");
  return headers;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = await authHeaders(init);
  const resp = await fetch(`${base}${path}`, { ...init, headers });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.status === 204 ? (undefined as T) : ((await resp.json()) as T);
}

export const api = {
  health: () => request<{ status: string }>("/health"),

  dashboard: () => request<Dashboard>("/api/dashboard"),

  search: {
    users: (q: string) => request<GlobalSearchResult>(`/api/search/users?q=${encodeURIComponent(q)}`)
  },

  tenants: {
    list: () => request<Tenant[]>("/api/tenants"),
    sync: () => request<Tenant[]>("/api/tenants/sync", { method: "POST" }),
    create: (tenantId: string, displayName: string, defaultDomain?: string) =>
      request<Tenant>("/api/tenants", { method: "POST", body: JSON.stringify({ tenantId, displayName, defaultDomain }) }),
    setContract: (id: string, contractId: string | null) =>
      request<void>(`/api/tenants/${id}/contract`, { method: "PUT", body: JSON.stringify(contractId) })
  },

  tenantAccess: {
    list: (tenantId: string) => request<TenantGrant[]>(`/api/tenants/${tenantId}/access`),
    grant: (tenantId: string, email: string, role: TenantRole, expiresAt?: string | null) =>
      request<void>(`/api/tenants/${tenantId}/access`, {
        method: "POST", body: JSON.stringify({ email, role, expiresAt: expiresAt ?? null })
      }),
    revoke: (tenantId: string, userId: string) =>
      request<void>(`/api/tenants/${tenantId}/access/${userId}`, { method: "DELETE" })
  },

  contracts: {
    list: () => request<Contract[]>("/api/contracts"),
    create: (name: string, notes?: string) =>
      request<Contract>("/api/contracts", { method: "POST", body: JSON.stringify({ name, notes }) }),
    plan: (id: string) =>
      request<{ tenantId: string; tenantName: string; templateId: string; templateName: string; action: string }[]>(
        `/api/contracts/${id}/plan`)
  },

  templates: {
    list: () => request<AppTemplate[]>("/api/apptemplates"),
    create: (body: Record<string, unknown>) =>
      request<AppTemplate>("/api/apptemplates", { method: "POST", body: JSON.stringify(body) }),
    uploadPackage: (id: string, file: File) => {
      const fd = new FormData();
      fd.append("file", file);
      return request<AppTemplate>(`/api/apptemplates/${id}/package`, { method: "POST", body: fd });
    }
  },

  deployments: {
    list: () => request<Deployment[]>("/api/deployments"),
    deploy: (templateId: string, tenantIds: string[]) =>
      request<Deployment[]>("/api/deployments", {
        method: "POST",
        body: JSON.stringify({ templateId, tenantIds })
      })
  },

  directory: {
    skus: (tenantId: string) => request<Sku[]>(`/api/directory/${tenantId}/skus`),
    groups: (tenantId: string) => request<DirectoryObject[]>(`/api/directory/${tenantId}/groups`),
    users: (tenantId: string, search?: string) =>
      request<DirectoryObject[]>(`/api/directory/${tenantId}/users${search ? `?search=${encodeURIComponent(search)}` : ""}`)
  },

  provisioning: {
    hire: (tenantId: string, hire: Record<string, unknown>) =>
      request<ProvisioningResult>("/api/provisioning/hire", {
        method: "POST",
        body: JSON.stringify({ tenantId, hire })
      }),
    terminate: (tenantId: string, termination: Record<string, unknown>) =>
      request<ProvisioningResult>("/api/provisioning/terminate", {
        method: "POST",
        body: JSON.stringify({ tenantId, termination })
      }),
    getTemplate: (contractId: string) =>
      request<ProvisioningTemplate | undefined>(`/api/contracts/${contractId}/provisioning-template`),
    upsertTemplate: (contractId: string, body: Record<string, unknown>) =>
      request<ProvisioningTemplate>(`/api/contracts/${contractId}/provisioning-template`, {
        method: "PUT",
        body: JSON.stringify(body)
      })
  },

  workflows: {
    list: () => request<WorkflowSummary[]>("/api/workflows"),
    runs: (opts?: { tenantId?: string; workflowId?: string; take?: number }) => {
      const q = new URLSearchParams();
      if (opts?.tenantId) q.set("tenantId", opts.tenantId);
      if (opts?.workflowId) q.set("workflowId", opts.workflowId);
      if (opts?.take) q.set("take", String(opts.take));
      const qs = q.toString();
      return request<WorkflowRunRecord[]>(`/api/workflows/runs${qs ? `?${qs}` : ""}`);
    },
    diagnose: (id: string, tenantId: string, inputs: Record<string, string>) =>
      request<DiagnosisResult>(`/api/workflows/${id}/diagnose`, {
        method: "POST", body: JSON.stringify({ tenantId, inputs })
      }),
    remediate: (id: string, tenantId: string, inputs: Record<string, string>) =>
      request<WorkflowRunResult>(`/api/workflows/${id}/remediate`, {
        method: "POST", body: JSON.stringify({ tenantId, inputs })
      })
  },

  pendingActions: {
    list: () => request<PendingAction[]>("/api/pending-actions"),
    approve: (id: string) => request<void>(`/api/pending-actions/${id}/approve`, { method: "POST" }),
    reject: (id: string) => request<void>(`/api/pending-actions/${id}/reject`, { method: "POST" }),
    retry: (id: string) => request<void>(`/api/pending-actions/${id}/retry`, { method: "POST" })
  },

  auth: {
    mode: () => request<{ mode: AuthMode }>("/api/auth/mode"),
    register: (email: string, password: string, displayName: string) =>
      request<AuthResponse>("/api/auth/register", { method: "POST", body: JSON.stringify({ email, password, displayName }) }),
    login: (email: string, password: string) =>
      request<AuthResponse | MfaChallengeResponse>("/api/auth/login", { method: "POST", body: JSON.stringify({ email, password }) }),
    logout: () => request<void>("/api/auth/logout", { method: "POST" }),
    me: () => request<MeProfile>("/api/auth/me")
  },

  totp: {
    enroll: () => request<TotpEnrollResponse>("/api/auth/totp/enroll", { method: "POST" }),
    verifyEnroll: (pendingKey: string, code: string) =>
      request<TotpVerifyEnrollResponse>("/api/auth/totp/verify-enroll", { method: "POST", body: JSON.stringify({ pendingKey, code }) }),
    disable: (password: string) =>
      request<void>("/api/auth/totp/disable", { method: "POST", body: JSON.stringify({ password }) }),
    challenge: (mfaTicket: string, code: string) =>
      request<AuthResponse>("/api/auth/totp/challenge", { method: "POST", body: JSON.stringify({ mfaTicket, code }) })
  },

  passkey: {
    // Options responses carry raw WebAuthn ceremony data (base64url byte fields) -- shaped by
    // webauthn.ts, not modeled fully here.
    registerOptions: () => request<{ challengeKey: string; options: unknown }>("/api/auth/passkey/register/options", { method: "POST" }),
    registerVerify: (challengeKey: string, attestationResponse: unknown, nickname?: string) =>
      request<void>("/api/auth/passkey/register/verify", {
        method: "POST", body: JSON.stringify({ challengeKey, attestationResponse, nickname })
      }),
    loginOptions: () => request<{ challengeKey: string; options: unknown }>("/api/auth/passkey/login/options", { method: "POST" }),
    loginVerify: (challengeKey: string, assertionResponse: unknown) =>
      request<AuthResponse>("/api/auth/passkey/login/verify", {
        method: "POST", body: JSON.stringify({ challengeKey, assertionResponse })
      }),
    list: () => request<PasskeyInfo[]>("/api/auth/passkey"),
    remove: (id: string) => request<void>(`/api/auth/passkey/${id}`, { method: "DELETE" })
  },

  configSnapshots: {
    sections: () => request<ConfigSection[]>("/api/config-sections"),
    list: (tenantId: string) => request<ConfigSnapshotRun[]>(`/api/tenants/${tenantId}/config-snapshots`),
    capture: (tenantId: string) =>
      request<ConfigSnapshotRun>(`/api/tenants/${tenantId}/config-snapshots`, { method: "POST" }),
    diff: (tenantId: string, beforeRunId: string, afterRunId: string, sectionId?: string) => {
      const q = new URLSearchParams({ beforeRunId, afterRunId });
      if (sectionId) q.set("sectionId", sectionId);
      return request<SectionDiff[]>(`/api/tenants/${tenantId}/config-snapshots/diff?${q}`);
    },
    // Downloads need the bearer token attached to the request itself -- a plain <a href> can't
    // carry an Authorization header, so these fetch the file as a blob and save it client-side.
    exportDiff: (tenantId: string, beforeRunId: string, afterRunId: string, sectionId?: string) => {
      const q = new URLSearchParams({ beforeRunId, afterRunId });
      if (sectionId) q.set("sectionId", sectionId);
      return download(`/api/tenants/${tenantId}/config-snapshots/diff/export?${q}`, `config-diff-${beforeRunId}-${afterRunId}.patch`);
    },
    exportRun: (tenantId: string, runId: string) =>
      download(`/api/tenants/${tenantId}/config-snapshots/${runId}/export`, `config-snapshot-${runId}.json`),
    import: (tenantId: string, sections: { sectionId: string; sectionName: string; contentJson: string }[]) =>
      request<ConfigSnapshotRun>(`/api/tenants/${tenantId}/config-snapshots/import`, {
        method: "POST", body: JSON.stringify({ sections })
      })
  }
};

async function download(path: string, filename: string): Promise<void> {
  const headers = await authHeaders();
  const resp = await fetch(`${base}${path}`, { headers });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  const blob = await resp.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
