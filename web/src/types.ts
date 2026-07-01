export type TenantStatus = "Active" | "Suspended" | "NoDelegation" | "Removed";

export interface Tenant {
  id: string;
  tenantId: string;
  displayName: string;
  defaultDomain?: string;
  status: TenantStatus;
  contractId?: string;
}

export interface Contract {
  id: string;
  name: string;
  notes?: string;
  tenantCount: number;
  desiredAppCount: number;
}

export interface AppTemplate {
  id: string;
  displayName: string;
  publisher?: string;
  contentVersion: number;
  hasPackage: boolean;
  contractId?: string;
  detectionRules: unknown[];
  assignments: unknown[];
}

export type DeploymentStatus =
  | "Pending" | "Uploading" | "Committing" | "Assigning"
  | "Succeeded" | "Failed" | "UpdateAvailable";

export interface Deployment {
  id: string;
  appTemplateId: string;
  tenantId: string;
  intuneAppId?: string;
  deployedTemplateVersion: number;
  status: DeploymentStatus;
  lastError?: string;
  lastSyncedAt?: string;
}
