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

export interface GlobalUserHit {
  tenantId: string;
  tenantName: string;
  id: string;
  displayName: string;
  userPrincipalName?: string;
}

export interface TenantSearchError { tenantId: string; tenantName: string; message: string }

export interface GlobalSearchResult {
  hits: GlobalUserHit[];
  errors: TenantSearchError[];
  tenantsSearched: number;
}

export interface DashboardStats {
  tenants: number;
  tenantsNoDelegation: number;
  deployments: number;
  deploymentsFailed: number;
  deploymentsUpdateAvailable: number;
  runsLast24h: number;
  runsFailedLast7d: number;
}

export interface AttentionItem {
  kind: string;
  tenantId: string;
  tenantName: string;
  subject: string;
  detail: string;
  when?: string;
}

export interface Dashboard {
  stats: DashboardStats;
  needsAttention: AttentionItem[];
  recentRuns: WorkflowRunRecord[];
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

export type PendingActionStatus = "Pending" | "Approved" | "Rejected" | "Executed" | "Expired";

export interface PendingAction {
  id: string;
  tenantId: string;
  tenantName: string;
  actionType: string;
  previewSummary: string;
  status: PendingActionStatus;
  createdAt: string;
  expiresAt: string;
  executionError: string | null;
}

// --- Auth:Mode=Local: accounts, TOTP, passkeys, tenant sharing --------------------------------
export type AuthMode = "Oidc" | "Local" | "Dev";

export type TenantRole = "Viewer" | "Operator" | "Owner";
/** Which tenants the current user has access to (used in MeProfile). */
export interface TenantAccess { tenantId: string; tenantName: string; role: TenantRole }
/** Who has access to a given tenant (used by the share/revoke panel) -- the other direction from TenantAccess. */
export interface TenantGrant { userId: string; email: string; role: TenantRole; grantedAt: string; expiresAt?: string }

export interface MeProfile {
  id: string;
  email: string;
  displayName: string;
  isSystemAdmin: boolean;
  totpEnabled: boolean;
  tenantAccess: TenantAccess[];
}

export interface AuthResponse { accessToken: string; user: MeProfile }
export interface MfaChallengeResponse { mfaTicket: string }
/** Discriminate a login response: an MfaChallengeResponse has no accessToken. */
export function isMfaChallenge(r: AuthResponse | MfaChallengeResponse): r is MfaChallengeResponse {
  return (r as MfaChallengeResponse).mfaTicket !== undefined;
}

export interface TotpEnrollResponse { pendingKey: string; secret: string; otpAuthUri: string }
export interface TotpVerifyEnrollResponse { recoveryCodes: string[] }

export interface PasskeyInfo { id: string; nickname?: string; createdAt: string; lastUsedAt?: string }
export interface McpTokenInfo {
  id: string;
  name: string;
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
}

// --- Config snapshots: backup + diff -----------------------------------------------------------
export interface ConfigSection { id: string; name: string; category: string }

export interface ConfigSnapshotSectionSummary { sectionId: string; sectionName: string; itemCount: number; failed: boolean; error?: string }
export interface ConfigSnapshotRun {
  id: string; tenantId: string; operator: string; startedAt: string; completedAt?: string;
  succeeded: boolean; imported: boolean; gitCommitSha?: string; sections: ConfigSnapshotSectionSummary[];
}

export type ConfigChangeKind = "Added" | "Removed" | "Modified";
export interface ConfigFieldChange { field: string; before?: string; after?: string }
export interface ConfigItemChange { kind: ConfigChangeKind; itemId: string; label?: string; fieldChanges: ConfigFieldChange[] }
export interface SectionDiff { sectionId: string; sectionName: string; changes: ConfigItemChange[] }

export interface ConfigWorkbookSection { sectionId: string; sectionName: string; contentJson: string }
export interface ConfigWorkbook { tenantDisplayName: string; capturedAt: string; operator: string; sections: ConfigWorkbookSection[] }
