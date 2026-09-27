import { getAccessToken } from "./auth";
import { getLocalToken } from "./session";
import type {
  AppTemplate, AuthMode, AuthResponse, ConfigSection, ConfigSnapshotRun, Contract, Dashboard,
  Deployment, DiagnosisResult, DirectoryObject, GlobalSearchResult, MeProfile, MfaChallengeResponse,
  InstanceRole, InstanceUser, McpTokenInfo, OffboardingPolicy, OperationEvidence, OperationPlan, PasskeyInfo, PendingAction,
  PersonWorkspace, ProvisioningResult, TerminateResult, ProvisioningTemplate, SamStatus, SectionDiff, Sku, SystemDiagnostics, SystemStatus,
  Tenant, TenantGrant, TenantRole, TotpEnrollResponse, TotpVerifyEnrollResponse, WorkflowRunRecord, WorkflowRunResult,
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

/**
 * A non-2xx API response. The message keeps the historical "status statusText: body" shape every
 * screen already displays; `status` lets callers branch on it (e.g. 404 from an older server that
 * predates an endpoint) without parsing the message.
 */
export class ApiError extends Error {
  readonly status: number;
  /** The raw response body (often the server's plain-text reason). */
  readonly body: string;
  constructor(status: number, message: string, body = "") {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.body = body;
  }
}

/**
 * The server's own explanation for a failed call, for showing next to a form: a plain-text body
 * ("Source and target must be different users."), a ProblemDetails title/errors, or the generic
 * message when the body says nothing useful.
 */
export function errorText(e: unknown): string {
  if (e instanceof ApiError && e.body.trim()) {
    const body = e.body.trim();
    try {
      const parsed = JSON.parse(body) as unknown;
      if (typeof parsed === "string") return parsed;
      if (parsed && typeof parsed === "object") {
        const p = parsed as { title?: string; detail?: string; errors?: Record<string, string[]> };
        const errors = p.errors ? Object.values(p.errors).flat() : [];
        if (errors.length > 0) return errors.join(" ");
        if (p.detail) return p.detail;
        if (p.title) return p.title;
      }
    } catch {
      if (body.length <= 500 && !body.startsWith("<")) return body;
    }
  }
  return e instanceof Error ? e.message : String(e);
}

/** True when `e` is an ApiError with the given HTTP status. */
export function isApiStatus(e: unknown, status: number): boolean {
  return e instanceof ApiError && e.status === status;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = await authHeaders(init);
  const resp = await fetch(`${base}${path}`, { ...init, headers });
  if (!resp.ok) {
    const body = await resp.text();
    throw new ApiError(resp.status, `${resp.status} ${resp.statusText}: ${body}`, body);
  }
  return resp.status === 204 ? (undefined as T) : ((await resp.json()) as T);
}

export const api = {
  microsoftConnections: {
    list: () => request<{ available: boolean; configured: boolean; connections: { id: string; tenantId: string; displayName: string; username: string; reconnectRequired: boolean }[] }>("/api/microsoft-connections"),
    connect: (tenantId?: string) => request<Tenant>("/api/microsoft-connections", { method: "POST", body: JSON.stringify({ tenantId }) })
  },
  health: () => request<{ status: string }>("/health"),

  dashboard: () => request<Dashboard>("/api/dashboard"),

  /** Workbench host status + diagnostics (0.9.0+). Older servers answer 404. */
  system: {
    status: () => request<SystemStatus>("/api/system/status"),
    diagnostics: () => request<SystemDiagnostics>("/api/system/diagnostics"),
    installDependency: (id: "pwsh" | "exchange-module") =>
      request<{ installed: boolean; detail: string }>(`/api/system/dependencies/${id}/install`, { method: "POST" }),
    declineDependency: (id: "pwsh" | "exchange-module") =>
      request<void>(`/api/system/dependencies/${id}/decision`, { method: "PUT", body: JSON.stringify({ declined: true }) })
  },

  /** Instance-level Secure Application Model credential (requires instance.sam.manage). */
  sam: {
    status: () => request<SamStatus>("/api/admin/sam/status"),
    seed: (refreshToken: string) =>
      request<void>("/api/admin/sam/seed", { method: "POST", body: JSON.stringify({ refreshToken }) })
  },

  /** Person workspace (0.9.0+): each section loads independently with its own status. */
  people: {
    get: (tenantId: string, userId: string) =>
      request<PersonWorkspace>(`/api/tenants/${tenantId}/people/${encodeURIComponent(userId)}`)
  },

  /** Access Parity (0.9.0+): additive group-membership mirroring from a source to a target user. */
  accessParity: {
    plan: (tenantId: string, sourceUserId: string, targetUserId: string) =>
      request<OperationPlan>(`/api/tenants/${tenantId}/operations/access-parity/plan`, {
        method: "POST", body: JSON.stringify({ sourceUserId, targetUserId })
      }),
    apply: (tenantId: string, sourceUserId: string, targetUserId: string, itemIds: string[]) =>
      request<OperationEvidence>(`/api/tenants/${tenantId}/operations/access-parity/apply`, {
        method: "POST", body: JSON.stringify({ sourceUserId, targetUserId, itemIds })
      })
  },

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

  instanceAccess: {
    list: () => request<InstanceUser[]>("/api/admin/users"),
    replaceRoles: (userId: string, roles: InstanceRole[], expectedAuthorizationVersion: number) =>
      request<InstanceUser>(`/api/admin/users/${userId}/roles`, {
        method: "PUT",
        body: JSON.stringify({ roles, expectedAuthorizationVersion })
      })
  },

  contracts: {
    list: () => request<Contract[]>("/api/contracts"),
    create: (name: string, notes?: string) =>
      request<Contract>("/api/contracts", { method: "POST", body: JSON.stringify({ name, notes }) }),
    plan: (id: string) =>
      request<{ tenantId: string; tenantName: string; templateId: string; templateName: string; action: string }[]>(
        `/api/contracts/${id}/plan`),
    addDesiredApp: (contractId: string, templateId: string) =>
      request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "POST" }),
    removeDesiredApp: (contractId: string, templateId: string) =>
      request<Contract>(`/api/contracts/${contractId}/desired-apps/${templateId}`, { method: "DELETE" }),
    /** 0.9.0+: the contract's offboarding policy (built-in defaults when none is set). */
    getOffboardingPolicy: (contractId: string) =>
      request<OffboardingPolicy>(`/api/contracts/${contractId}/offboarding-policy`),
    /** Replaces the policy; needs instance.catalog.manage. 400 carries the validation messages. */
    putOffboardingPolicy: (contractId: string, policy: OffboardingPolicy) =>
      request<OffboardingPolicy>(`/api/contracts/${contractId}/offboarding-policy`, {
        method: "PUT", body: JSON.stringify(policy)
      })
  },

  templates: {
    list: () => request<AppTemplate[]>("/api/apptemplates"),
    create: (body: Record<string, unknown>) =>
      request<AppTemplate>("/api/apptemplates", { method: "POST", body: JSON.stringify(body) }),
    update: (id: string, body: Record<string, unknown>) =>
      request<AppTemplate>(`/api/apptemplates/${id}`, { method: "PUT", body: JSON.stringify(body) }),
    remove: (id: string) => request<void>(`/api/apptemplates/${id}`, { method: "DELETE" }),
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
      request<TerminateResult>("/api/provisioning/terminate", {
        method: "POST",
        body: JSON.stringify({ tenantId, termination })
      }),
    /** 0.9.0+: ordered, destructive-flagged offboarding plan (no changes made). */
    terminatePlan: (tenantId: string, termination: Record<string, unknown>) =>
      request<OperationPlan>("/api/provisioning/terminate/plan", {
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
    runs: (opts?: { tenantId?: string; workflowId?: string; targetId?: string; take?: number }) => {
      const q = new URLSearchParams();
      if (opts?.tenantId) q.set("tenantId", opts.tenantId);
      if (opts?.targetId) q.set("targetId", opts.targetId);
      if (opts?.workflowId) q.set("workflowId", opts.workflowId);
      if (opts?.take) q.set("take", String(opts.take));
      const qs = q.toString();
      return request<WorkflowRunRecord[]>(`/api/workflows/runs${qs ? `?${qs}` : ""}`);
    },
    /** 0.9.0+: structured evidence for a persisted run. */
    evidence: (runId: string) => request<OperationEvidence>(`/api/workflows/runs/${runId}/evidence`),
    evidenceMarkdown: (runId: string) =>
      download(`/api/workflows/runs/${runId}/evidence?format=markdown`, `pcb-run-${runId}.md`),
    evidenceJson: (runId: string) =>
      download(`/api/workflows/runs/${runId}/evidence`, `pcb-run-${runId}.json`),
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
    /** On a Local Workbench's first run the first account needs the one-time setup ticket the exe opened. */
    register: (email: string, password: string, displayName: string, ticket?: string | null) =>
      request<AuthResponse>("/api/auth/register", { method: "POST", body: JSON.stringify({ email, password, displayName, ticket: ticket ?? undefined }) }),
    login: (email: string, password: string) =>
      request<AuthResponse | MfaChallengeResponse>("/api/auth/login", { method: "POST", body: JSON.stringify({ email, password }) }),
    logout: () => request<void>("/api/auth/logout", { method: "POST" }),
    /** First run: use this Local Workbench without an account (creates the built-in owner); needs the setup ticket. */
    setupNoAccount: (ticket: string | null) =>
      request<AuthResponse>("/api/auth/setup/no-account", { method: "POST", body: JSON.stringify({ confirm: true, ticket }) }),
    /** Exchanges the one-time sign-in ticket from the URL fragment for a session. */
    launch: (ticket: string) =>
      request<AuthResponse>("/api/auth/launch", { method: "POST", body: JSON.stringify({ ticket }) }),
    /** Converts the no-account owner into a password account (launch links stop working). */
    protectOwner: (email: string, password: string, displayName: string) =>
      request<AuthResponse>("/api/auth/owner/protect", { method: "POST", body: JSON.stringify({ email, password, displayName }) }),
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

  mcpTokens: {
    list: () => request<McpTokenInfo[]>("/api/mcp-tokens"),
    create: (name: string) =>
      request<{ id: string; name: string; jwt: string }>("/api/mcp-tokens", { method: "POST", body: JSON.stringify({ name }) }),
    revoke: (id: string) => request<void>(`/api/mcp-tokens/${id}`, { method: "DELETE" }),
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
  if (!resp.ok) {
    const body = await resp.text();
    throw new ApiError(resp.status, `${resp.status} ${resp.statusText}: ${body}`, body);
  }
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
