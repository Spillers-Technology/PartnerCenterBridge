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

export interface ArchiveState {
  userPrincipalName: string;
  primarySize: string;
  primaryItemCount: number;
  prohibitSendReceiveQuota: string;
  archiveEnabled: boolean;
  archiveStatus: string;
  autoExpandingArchiveEnabled: boolean;
  archiveQuota?: string;
  archiveWarningQuota?: string;
  archiveSize?: string;
  archiveItemCount: number;
  retentionPolicy?: string;
  retentionHoldEnabled: boolean;
  elcProcessingDisabled: boolean;
}

export interface ArchiveRemediationResult {
  steps: ProvisioningStep[];
  state?: ArchiveState;
  succeeded: boolean;
}

export interface ArchiveRemediationOptions {
  enableAutoExpandingArchive: boolean;
  retentionPolicyName?: string;
  clearProcessingBlocks: boolean;
  triggerProcessing: boolean;
}
