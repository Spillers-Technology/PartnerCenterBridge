import { getAccessToken } from "./auth";
import type {
  AppTemplate, Contract, Dashboard, Deployment, DiagnosisResult, DirectoryObject,
  GlobalSearchResult, ProvisioningResult, ProvisioningTemplate, Sku, Tenant,
  WorkflowRunRecord, WorkflowRunResult, WorkflowSummary
} from "./types";

const base = (import.meta.env.VITE_API_BASE as string | undefined) ?? "";

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = await getAccessToken();
  const headers = new Headers(init.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (init.body && !(init.body instanceof FormData)) headers.set("Content-Type", "application/json");

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
    setContract: (id: string, contractId: string | null) =>
      request<void>(`/api/tenants/${id}/contract`, { method: "PUT", body: JSON.stringify(contractId) })
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
  }
};
