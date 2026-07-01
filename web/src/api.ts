import { getAccessToken } from "./auth";
import type { AppTemplate, Contract, Deployment, Tenant } from "./types";

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
  }
};
