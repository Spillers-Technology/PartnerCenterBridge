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
  desiredAppIds: string[];
}

export interface AppTemplate {
  id: string;
  displayName: string;
  description?: string;
  publisher?: string;
  installCommandLine: string;
  uninstallCommandLine: string;
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
  /** 0.9.0+: native evidence from a planned operation run through remediate; null for classic workflows. */
  evidence?: OperationEvidence | null;
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

export type WorkflowRunKind = "Diagnose" | "Remediate" | "Plan" | "Apply";
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
  healthy?: boolean | null;
  error?: string | null;
  startedAt: string;
  durationMs: number;
  /** 0.9.0+: structured outcome from the evidence model; absent on older servers. */
  outcome?: Outcome;
  /** 0.9.0+: the user the run was about (normalized object id or UPN), when known. */
  targetId?: string | null;
  targetDisplayName?: string | null;
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
export type InstanceRole = "Administrator" | "CatalogManager" | "CredentialManager" | "AutomationPolicyManager";
export type InstancePermission =
  | "instance.roles.manage"
  | "instance.catalog.manage"
  | "instance.sam.manage"
  | "instance.mcp-policy.manage"
  | "instance.tenant-registry.manage";
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
  instanceRoles?: InstanceRole[];
  instancePermissions?: InstancePermission[];
  authorizationVersion?: number;
}

export interface InstanceUser {
  id: string;
  email: string;
  displayName: string;
  isActive: boolean;
  roles: InstanceRole[];
  authorizationVersion: number;
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

// --- Ops workbench (0.9.0): system status + diagnostics (spec section A) ------------------------
export type HostingProfile = "Server" | "Local";
/** GET /api/system/status -- anonymous, nothing sensitive. */
export interface SystemStatus {
  profile: HostingProfile;
  version: string;
  authMode: AuthMode;
  needsFirstUser: boolean;
}

export type DiagnosticStatus = "Ok" | "Warning" | "Error" | "NotConfigured";
/** A concrete fix for a failing check: a command to run and/or an SPA route to open. */
export interface DiagnosticFix { label: string; command?: string | null; route?: string | null }
export interface DiagnosticCheck {
  id: string;
  label: string;
  status: DiagnosticStatus;
  detail?: string | null;
  fix?: DiagnosticFix | null;
}
export interface SystemCapabilities { graph: boolean; exchange: boolean; partnerCenter: boolean }
/** GET /api/system/diagnostics -- details only for instance Administrators; others get capabilities. */
export interface SystemDiagnostics {
  checks?: DiagnosticCheck[] | null;
  capabilities: SystemCapabilities;
}

/** GET /api/admin/sam/status */
export interface SamStatus { bootstrapped: boolean }

// --- Ops workbench (0.9.0): operation and evidence model (spec section B) -----------------------
export type Outcome =
  | "Succeeded" | "PartiallySucceeded" | "Failed" | "NoChangeNeeded"
  | "VerificationFailed" | "Planned"
  /** Applied and acknowledged by Microsoft, but PCB could not independently re-read and confirm it. */
  | "CompletedUnverified";

export interface OperationTarget { kind: string; id: string; displayName: string }

export interface PlanItem {
  id: string;
  action: string;
  objectType: string;
  objectId: string;
  objectName: string;
  destructive: boolean;
  eligible: boolean;
  category: string;
  /** Why the item is ineligible / why it was skipped. */
  reason?: string | null;
}

export interface OperationPlan {
  operationId: string;
  operationName: string;
  tenantId: string;
  target: OperationTarget;
  preflight: Finding[];
  items: PlanItem[];
  warnings: string[];
  limitations: string[];
}

export interface ChangeResult {
  planItemId: string;
  action: string;
  objectName: string;
  attempted: boolean;
  succeeded: boolean;
  detail?: string | null;
}

export interface VerificationCheck { name: string; passed: boolean; detail?: string | null }

export interface OperationEvidence {
  runId: string;
  operationId: string;
  operationName: string;
  tenant: { id: string; displayName: string; tenantId: string };
  target: OperationTarget | null;
  operator: string;
  startedAt: string;
  completedAt: string;
  outcome: Outcome;
  preflight: Finding[];
  plan: PlanItem[];
  changes: ChangeResult[];
  verification: VerificationCheck[];
  warnings: string[];
  limitations: string[];
  failures: string[];
  ticketNotes: string;
}

/** Access Parity group categories; only Security and Microsoft365 cloud groups are eligible. */
export type GroupCategory =
  | "Security" | "Microsoft365" | "MailEnabledSecurity" | "Distribution" | "Dynamic"
  | "RoleAssignable" | "OnPremSynced" | "DirectoryRole" | "AlreadyMember" | "Other";

// --- Ops workbench (0.9.0): person workspace ----------------------------------------------------
export type SectionStatus = "Ok" | "Unavailable" | "Error";
/** Each workspace section loads independently; Unavailable/Error carry the reason. */
export interface WorkspaceSection<T> { status: SectionStatus; reason?: string | null; data?: T | null }

export interface PersonProfile {
  id: string;
  displayName: string;
  upn?: string | null;
  mail?: string | null;
  /** Null when Graph did not return it. */
  accountEnabled?: boolean | null;
  jobTitle?: string | null;
  department?: string | null;
  onPremisesSyncEnabled?: boolean | null;
  createdDateTime?: string | null;
  lastSignIn?: string | null;
}
export interface PersonLicense { skuPartNumber: string; skuId: string }
export interface PersonGroup { id: string; displayName: string; category: GroupCategory | string }
export interface PersonDevice {
  id: string;
  deviceName?: string | null;
  operatingSystem?: string | null;
  osVersion?: string | null;
  complianceState?: string | null;
  lastSyncDateTime?: string | null;
  managementAgent?: string | null;
}
/** Exchange Online mailbox summary (MailboxInfo on the server). */
export interface PersonMailbox {
  userPrincipalName: string;
  displayName: string;
  recipientTypeDetails: string;
  forwardingSmtpAddress?: string | null;
  deliverToMailboxAndForward: boolean;
}

/** GET /api/tenants/{tenantId}/people/{userId} */
export interface PersonWorkspace {
  /** PCB tenant id (registry GUID). */
  tenantId: string;
  /** The user's resolved object id (the request may have used a UPN). */
  userId: string;
  profile: WorkspaceSection<PersonProfile>;
  licenses: WorkspaceSection<PersonLicense[]>;
  groups: WorkspaceSection<PersonGroup[]>;
  authMethods: WorkspaceSection<string[]>;
  /** Ok with null data (plus a reason) when Exchange returned no mailbox. */
  mailbox: WorkspaceSection<PersonMailbox>;
  devices: WorkspaceSection<PersonDevice[]>;
  recentRuns: WorkspaceSection<WorkflowRunRecord[]>;
}

// --- Ops workbench (0.9.0): offboarding policy v2 -----------------------------------------------
export interface OffboardingPolicy {
  blockSignIn: boolean;
  revokeSessions: boolean;
  groupCleanup: "None" | "RemoveAssignable" | "RemoveAll";
  convertMailboxToShared: boolean;
  removeLicenses: boolean;
  hideFromGal: boolean;
  forwardTo?: string | null;
  managerAccess: "None" | "FullAccess";
  wipeDevices: "None" | "Retire";
  /** Days after offboarding to revisit/delete the account; 0 = no follow-up. */
  followUpDays: number;
}

/** POST /api/provisioning/terminate: the original fields plus evidence and the policy applied (0.9.0+). */
export interface TerminateResult extends ProvisioningResult {
  evidence?: OperationEvidence | null;
  policy?: OffboardingPolicy | null;
}
