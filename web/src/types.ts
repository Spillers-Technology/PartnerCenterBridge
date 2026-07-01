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

export interface Sku {
  skuId: string;
  skuPartNumber: string;
  enabled: number;
  consumed: number;
}

export interface DirectoryObject {
  id: string;
  displayName: string;
  userPrincipalName?: string;
}

export interface ProvisioningStep {
  name: string;
  success: boolean;
  detail?: string;
}

export interface ProvisioningResult {
  userId?: string;
  userPrincipalName?: string;
  initialPassword?: string;
  steps: ProvisioningStep[];
  succeeded: boolean;
}

export interface ProvisioningTemplate {
  contractId: string;
  usageLocation: string;
  upnDomain?: string;
  defaultJobTitle?: string;
  defaultDepartment?: string;
  licenseSkuIds: string[];
  groupIds: string[];
}

export type FindingStatus = "Ok" | "Info" | "Warning" | "Blocker";
export interface Finding { name: string; status: FindingStatus; detail?: string }
export interface DiagnosisResult { findings: Finding[]; healthy: boolean }
export interface WorkflowRunResult {
  steps: ProvisioningStep[];
  postState?: DiagnosisResult;
  /** Show-once secrets (e.g. a temporary password) - never persisted to run history. */
  ephemeral?: Record<string, string>;
  succeeded: boolean;
}

export type WorkflowRunKind = "Diagnose" | "Remediate";
export interface WorkflowRunRecord {
  id: string;
  workflowId: string;
  workflowName: string;
  tenantId: string;
  tenantName: string;
  kind: WorkflowRunKind;
  operator: string;
  inputs: Record<string, string>;
  findings: Finding[];
  steps: ProvisioningStep[];
  succeeded: boolean;
  healthy?: boolean;
  error?: string;
  startedAt: string;
  durationMs: number;
}

export interface WorkflowInput { key: string; label: string; placeholder?: string; required: boolean; default?: string; type: "text" | "bool" }
export interface WorkflowSummary {
  id: string;
  name: string;
  description: string;
  category: string;
  inputs: WorkflowInput[];
}
