// Shared mocked-/api/* fixtures and Playwright helpers used by both capture-product-media.mjs
// (desktop hero shots) and capture-mobile-media.mjs (the mobile/desktop verification matrix).
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const repoRoot = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));

export function loadPlaywright() {
  const candidates = [
    process.env.PLAYWRIGHT_NODE_MODULES
      ? path.join(process.env.PLAYWRIGHT_NODE_MODULES, "playwright")
      : null,
    path.join(repoRoot, "web", "node_modules", "playwright"),
    "playwright",
  ].filter(Boolean);

  for (const candidate of candidates) {
    try {
      return require(candidate);
    } catch {
      // try the next location
    }
  }

  throw new Error(
    [
      "Playwright is required to capture product media.",
      "Install it in a temp directory, then point PLAYWRIGHT_NODE_MODULES at that node_modules folder:",
      "  npm install --prefix %TEMP%\\pcbridge-playwright playwright",
      "  $env:PLAYWRIGHT_NODE_MODULES=\"$env:TEMP\\pcbridge-playwright\\node_modules\"",
      "Start the SPA first: cd web; npm run dev",
      "  node docs/scripts/capture-product-media.mjs",
    ].join("\n")
  );
}

export async function waitForServer(baseUrl) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(baseUrl, { signal: AbortSignal.timeout(3000) });
      if (res.ok) return;
    } catch {
      // keep waiting
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`Timed out waiting for ${baseUrl}`);
}

export async function freezeAnimations(page) {
  await page.addStyleTag({
    content: `*, *::before, *::after {
      transition-duration: 0s !important;
      animation-duration: 0s !important;
      caret-color: transparent !important;
    }`,
  });
}

function minutesAgo(mins) {
  return new Date(Date.now() - mins * 60_000).toISOString();
}

// ---------------------------------------------------------------------------
// Mocked data - a small but coherent MSP world.
// ---------------------------------------------------------------------------

const tenants = [
  { id: "11111111-1111-1111-1111-111111111111", tenantId: "aa11...", displayName: "Contoso Ltd", defaultDomain: "contoso.onmicrosoft.com", status: "Active", contractId: "c1" },
  { id: "22222222-2222-2222-2222-222222222222", tenantId: "bb22...", displayName: "Fabrikam Inc", defaultDomain: "fabrikam.onmicrosoft.com", status: "Active", contractId: "c1" },
  { id: "33333333-3333-3333-3333-333333333333", tenantId: "cc33...", displayName: "Tailspin Toys", defaultDomain: "tailspintoys.onmicrosoft.com", status: "Active", contractId: "c2" },
  { id: "44444444-4444-4444-4444-444444444444", tenantId: "dd44...", displayName: "Adventure Works", defaultDomain: "adventure-works.onmicrosoft.com", status: "Active", contractId: "c2" },
  { id: "55555555-5555-5555-5555-555555555555", tenantId: "ee55...", displayName: "Wingtip Partners", defaultDomain: "wingtip.onmicrosoft.com", status: "NoDelegation" },
];

const contracts = [
  { id: "c1", name: "Managed Workstations", notes: "Full Win32 baseline + provisioning", tenantCount: 2, desiredAppCount: 3 },
  { id: "c2", name: "Standard Care", notes: "Core apps only", tenantCount: 2, desiredAppCount: 1 },
];

const templates = [
  { id: "t1", displayName: "7-Zip 24.08", publisher: "Igor Pavlov", contentVersion: 3, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t2", displayName: "Google Chrome Enterprise 126", publisher: "Google LLC", contentVersion: 5, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t3", displayName: "Company Portal branding", publisher: "Contoso IT", contentVersion: 2, hasPackage: true, contractId: "c1", detectionRules: [], assignments: [] },
  { id: "t4", displayName: "FortiClient VPN", publisher: "Fortinet", contentVersion: 1, hasPackage: false, detectionRules: [], assignments: [] },
];

const deployments = [
  { id: "d1", appTemplateId: "t1", tenantId: tenants[0].id, intuneAppId: "9b1c...a1", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(95) },
  { id: "d2", appTemplateId: "t1", tenantId: tenants[1].id, intuneAppId: "7f2d...c4", deployedTemplateVersion: 2, status: "UpdateAvailable", lastSyncedAt: minutesAgo(120) },
  { id: "d3", appTemplateId: "t2", tenantId: tenants[0].id, intuneAppId: "42aa...9e", deployedTemplateVersion: 5, status: "Succeeded", lastSyncedAt: minutesAgo(140) },
  { id: "d4", appTemplateId: "t2", tenantId: tenants[1].id, intuneAppId: "42bb...1f", deployedTemplateVersion: 4, status: "UpdateAvailable", lastSyncedAt: minutesAgo(150) },
  { id: "d5", appTemplateId: "t3", tenantId: tenants[0].id, intuneAppId: "c103...77", deployedTemplateVersion: 2, status: "Succeeded", lastSyncedAt: minutesAgo(200) },
  { id: "d6", appTemplateId: "t3", tenantId: tenants[1].id, deployedTemplateVersion: 2, status: "Failed", lastError: "content commit rejected: 409 conflict on committedContentVersion", lastSyncedAt: minutesAgo(35) },
  { id: "d7", appTemplateId: "t1", tenantId: tenants[2].id, intuneAppId: "5510...ab", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(300) },
  { id: "d8", appTemplateId: "t1", tenantId: tenants[3].id, intuneAppId: "5511...cd", deployedTemplateVersion: 3, status: "Succeeded", lastSyncedAt: minutesAgo(320) },
];

const workflows = [
  {
    id: "compromised-lockdown", name: "Compromised account lockdown", category: "Identity",
    description: "Block sign-in, revoke all sessions, and disable inbox rules that forward, redirect, or delete mail.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "license-repair", name: "License assignment repair", category: "Identity",
    description: "Fix a user whose license won't apply - sets usage location and reprocesses stuck SKUs.",
    inputs: [
      { key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" },
      { key: "usageLocation", label: "Usage location (2-letter)", placeholder: "US", required: false, default: "US", type: "text" },
    ],
  },
  {
    id: "mfa-reset", name: "MFA / auth method reset", category: "Identity",
    description: "Revoke sessions and clear registered authentication methods so the user re-registers MFA.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "password-reset", name: "Password reset + session revoke", category: "Identity",
    description: "Set a temporary must-change password and revoke all sessions so the old credential stops working everywhere.",
    inputs: [{ key: "userUpn", label: "User UPN or id", placeholder: "user@contoso.com", required: true, type: "text" }],
  },
  {
    id: "mailbox-archive", name: "Mailbox archive repair", category: "Mailbox",
    description: "Fix a full mailbox that is not archiving: enable archive + auto-expand, ensure a retention policy, clear processing blockers, and kick the Managed Folder Assistant. Re-run to nudge the asynchronous move.",
    inputs: [
      { key: "identity", label: "Mailbox UPN or alias", placeholder: "user@contoso.com", required: true, type: "text" },
      { key: "retentionPolicyName", label: "Retention policy to assign if none", placeholder: "Default MRM Policy", required: false, default: "Default MRM Policy", type: "text" },
      { key: "enableAutoExpandingArchive", label: "Enable auto-expanding archive", required: false, default: "true", type: "bool" },
      { key: "clearProcessingBlocks", label: "Clear retention hold / ELC blocks", required: false, default: "true", type: "bool" },
      { key: "triggerProcessing", label: "Trigger the Managed Folder Assistant", required: false, default: "true", type: "bool" },
    ],
  },
];

const mailboxDiagnosis = {
  healthy: false,
  findings: [
    { name: "Primary mailbox size", status: "Warning", detail: "48.9 GB of 50 GB (98%)" },
    { name: "Archive mailbox", status: "Blocker", detail: "Archive not enabled" },
    { name: "Auto-expanding archive", status: "Warning", detail: "Disabled" },
    { name: "Retention policy", status: "Blocker", detail: "No retention policy assigned - the assistant has nothing to act on" },
    { name: "Retention hold", status: "Warning", detail: "RetentionHoldEnabled = true - blocks the Managed Folder Assistant" },
    { name: "ELC processing", status: "Warning", detail: "ElcProcessingDisabled = true" },
    { name: "Managed Folder Assistant", status: "Info", detail: "Last processed 6 days ago" },
  ],
};

const runs = [
  { id: "r6", workflowId: "access-parity", workflowName: "Access parity", tenantId: tenants[0].id, tenantName: "Contoso Ltd", kind: "Apply", operator: "jspillers", inputs: { sourceUserId: "priya.shah@contoso.com", targetUserId: "u1" }, findings: [], steps: [], succeeded: false, startedAt: minutesAgo(12), durationMs: 6120, outcome: "PartiallySucceeded", targetId: "u1", targetDisplayName: "Maya Chen" },
  { id: "r1", workflowId: "mailbox-archive", workflowName: "Mailbox archive repair", tenantId: tenants[0].id, tenantName: "Contoso Ltd", kind: "Remediate", operator: "jspillers", inputs: { identity: "maya.chen@contoso.com" }, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(40), durationMs: 5210, outcome: "Succeeded", targetId: "u1", targetDisplayName: "Maya Chen" },
  { id: "r2", workflowId: "mfa-reset", workflowName: "MFA / auth method reset", tenantId: tenants[2].id, tenantName: "Tailspin Toys", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(70), durationMs: 1890, outcome: "Succeeded", targetId: "u2", targetDisplayName: "Marco Chen" },
  { id: "r3", workflowId: "license-repair", workflowName: "License assignment repair", tenantId: tenants[2].id, tenantName: "Tailspin Toys", kind: "Remediate", operator: "amorgan", inputs: {}, findings: [], steps: [], succeeded: false, healthy: false, error: "usage location set, but SKU still in error state after reprocess", startedAt: minutesAgo(110), durationMs: 4400, outcome: "VerificationFailed", targetId: "u2", targetDisplayName: "Marco Chen" },
  { id: "r4", workflowId: "compromised-lockdown", workflowName: "Compromised account lockdown", tenantId: tenants[1].id, tenantName: "Fabrikam Inc", kind: "Remediate", operator: "jspillers", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(180), durationMs: 3120, outcome: "Succeeded" },
  { id: "r5", workflowId: "password-reset", workflowName: "Password reset + session revoke", tenantId: tenants[0].id, tenantName: "Contoso Ltd", kind: "Diagnose", operator: "amorgan", inputs: {}, findings: [], steps: [], succeeded: true, healthy: true, startedAt: minutesAgo(220), durationMs: 640, outcome: "Planned" },
];

const dashboard = {
  stats: {
    tenants: 5, tenantsNoDelegation: 1,
    deployments: 8, deploymentsFailed: 1, deploymentsUpdateAvailable: 2,
    runsLast24h: 6, runsFailedLast7d: 1,
  },
  needsAttention: [
    { kind: "Deployment failed", tenantId: tenants[1].id, tenantName: "Fabrikam Inc", subject: "Company Portal branding", detail: "content commit rejected: 409 conflict on committedContentVersion", when: minutesAgo(35) },
    { kind: "Workflow failed", tenantId: tenants[2].id, tenantName: "Tailspin Toys", subject: "License assignment repair", detail: "usage location set, but SKU still in error state after reprocess", when: minutesAgo(110) },
    { kind: "No delegation", tenantId: tenants[4].id, tenantName: "Wingtip Partners", subject: "wingtip.onmicrosoft.com", detail: "GDAP relationship missing or expired - the bridge cannot act here.", when: minutesAgo(600) },
  ],
  recentRuns: runs,
};

const pendingActions = [
  {
    id: "pa1", tenantId: tenants[0].id, tenantName: "Contoso Ltd", actionType: "workflow.remediate",
    previewSummary: "Remediate 'MFA / auth method reset' on Contoso Ltd. Current diagnosis: Registered methods=Blocker; Active sessions=Warning. Inputs: userUpn=maya.chen@contoso.com.",
    status: "Pending", createdAt: minutesAgo(6), expiresAt: minutesAgo(-54), executionError: null,
  },
  {
    id: "pa2", tenantId: tenants[2].id, tenantName: "Tailspin Toys", actionType: "workflow.remediate",
    previewSummary: "Remediate 'License assignment repair' on Tailspin Toys. Current diagnosis: Usage location=Blocker. Inputs: userUpn=marco.chen@tailspintoys.com.",
    status: "Approved", createdAt: minutesAgo(40), expiresAt: minutesAgo(-20),
    executionError: "usage location set, but SKU still in error state after reprocess",
  },
];

const searchResult = {
  tenantsSearched: 4,
  hits: [
    { tenantId: tenants[0].id, tenantName: "Contoso Ltd", id: "u1", displayName: "Maya Chen", userPrincipalName: "maya.chen@contoso.com" },
    { tenantId: tenants[2].id, tenantName: "Tailspin Toys", id: "u2", displayName: "Marco Chen", userPrincipalName: "marco.chen@tailspintoys.com" },
  ],
  errors: [
    { tenantId: tenants[4].id, tenantName: "Wingtip Partners", message: "GDAP relationship missing or expired" },
  ],
};

const skus = [
  { skuId: "sku-e3", skuPartNumber: "SPE_E3", enabled: 25, consumed: 21 },
  { skuId: "sku-bp", skuPartNumber: "O365_BUSINESS_PREMIUM", enabled: 40, consumed: 33 },
  { skuId: "sku-eop", skuPartNumber: "EXCHANGE_S_STANDARD", enabled: 10, consumed: 4 },
];

const groups = [
  { id: "g1", displayName: "All Staff" },
  { id: "g2", displayName: "VPN Users" },
  { id: "g3", displayName: "Managed Workstations" },
];

const directoryUsers = [
  { id: "u1", displayName: "Maya Chen", userPrincipalName: "maya.chen@contoso.com" },
  { id: "u10", displayName: "Priya Shah", userPrincipalName: "priya.shah@contoso.com" },
  { id: "u11", displayName: "Sam Rivera", userPrincipalName: "sam.rivera@contoso.com" },
];

const provisioningTemplate = {
  contractId: "c1", usageLocation: "US", upnDomain: "contoso.com",
  defaultJobTitle: "Associate", defaultDepartment: "Operations",
  licenseSkuIds: ["sku-e3"], groupIds: ["g1", "g3"],
};

// Auth:Mode=Local + Config Snapshots mock data. authModeOverride lets the same router serve the
// original (auth-disabled "Dev") screens unchanged while a later page in this same run asks for
// "Local" mode to exercise Login/Register/Security.

const meProfile = {
  id: "u1", email: "jspillers@example.com", displayName: "jspillers",
  isSystemAdmin: true, totpEnabled: true,
  tenantAccess: [{ tenantId: tenants[0].id, tenantName: "Contoso Ltd", role: "Owner" }],
  instanceRoles: ["Administrator"],
  instancePermissions: [
    "instance.roles.manage", "instance.catalog.manage", "instance.sam.manage",
    "instance.mcp-policy.manage", "instance.tenant-registry.manage",
  ],
  authorizationVersion: 3,
};

// Delegated instance roles: Administrator, CatalogManager, CredentialManager,
// AutomationPolicyManager -- separate from per-tenant Viewer/Operator/Owner access above.
const instanceUsers = [
  { id: "u1", email: "jspillers@example.com", displayName: "jspillers", isActive: true, roles: ["Administrator"], authorizationVersion: 3 },
  { id: "u2", email: "maya.chen@example.com", displayName: "Maya Chen", isActive: true, roles: ["CatalogManager", "AutomationPolicyManager"], authorizationVersion: 1 },
  { id: "u3", email: "devon.reyes@example.com", displayName: "Devon Reyes", isActive: true, roles: ["CredentialManager"], authorizationVersion: 1 },
  { id: "u4", email: "former.contractor@example.com", displayName: "Former Contractor", isActive: false, roles: [], authorizationVersion: 2 },
];

const passkeys = [
  { id: "pk1", nickname: "YubiKey 5C", createdAt: minutesAgo(43200), lastUsedAt: minutesAgo(62) },
];

const mcpTokens = [
  { id: "mt1", name: "Claude Desktop", createdAt: minutesAgo(20160), expiresAt: null, lastUsedAt: minutesAgo(12) },
];

const configSections = [
  { id: "conditional-access-policies", name: "Conditional Access Policies", category: "Identity" },
  { id: "named-locations", name: "Named Locations", category: "Identity" },
  { id: "device-compliance-policies", name: "Device Compliance Policies", category: "Devices" },
];

const snapshotRuns = [
  {
    id: "run1", tenantId: tenants[0].id, operator: "jspillers",
    startedAt: minutesAgo(2880), completedAt: minutesAgo(2880), succeeded: true, imported: false,
    gitCommitSha: "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0",
    sections: [
      { sectionId: "conditional-access-policies", sectionName: "Conditional Access Policies", itemCount: 6, failed: false },
      { sectionId: "named-locations", sectionName: "Named Locations", itemCount: 2, failed: false },
      { sectionId: "device-compliance-policies", sectionName: "Device Compliance Policies", itemCount: 4, failed: false },
    ],
  },
  {
    id: "run2", tenantId: tenants[0].id, operator: "jspillers",
    startedAt: minutesAgo(30), completedAt: minutesAgo(30), succeeded: true, imported: false,
    gitCommitSha: "f9e8d7c6b5a4938271605f4e3d2c1b0a9988776a",
    sections: [
      { sectionId: "conditional-access-policies", sectionName: "Conditional Access Policies", itemCount: 7, failed: false },
      { sectionId: "named-locations", sectionName: "Named Locations", itemCount: 1, failed: false },
      { sectionId: "device-compliance-policies", sectionName: "Device Compliance Policies", itemCount: 4, failed: false },
    ],
  },
];

const snapshotDiff = [
  {
    sectionId: "conditional-access-policies", sectionName: "Conditional Access Policies",
    changes: [
      { kind: "Modified", itemId: "1", label: "Require MFA for admins", fieldChanges: [{ field: "state", before: '"enabledForReportingButNotEnforced"', after: '"enabled"' }] },
      { kind: "Added", itemId: "7", label: "Block legacy authentication", fieldChanges: [] },
    ],
  },
  {
    sectionId: "named-locations", sectionName: "Named Locations",
    changes: [{ kind: "Removed", itemId: "loc2", label: "Old branch office", fieldChanges: [] }],
  },
  { sectionId: "device-compliance-policies", sectionName: "Device Compliance Policies", changes: [] },
];

// Workbench status/diagnostics (0.9.0+). A realistic mix: most checks pass, Exchange isn't set up
// yet -- so Home shows its setup checklist and Settings > Workbench shows quest-chip fixes.
const diagnostics = {
  checks: [
    { id: "hosting", label: "Hosting", status: "Ok", detail: "Server profile, listening on http://localhost:5080", fix: null },
    { id: "database", label: "Database", status: "Ok", detail: "PostgreSQL 16 at postgres.databases.svc, 42 migrations applied", fix: null },
    { id: "data-protection", label: "Data protection keys", status: "Ok", detail: "Persisted to the database", fix: null },
    { id: "auth", label: "Sign-in", status: "Ok", detail: "Local accounts, 4 users", fix: null },
    { id: "sam", label: "Microsoft connection (SAM)", status: "Ok", detail: "Refresh token stored, last used 12 minutes ago", fix: null },
    { id: "tenants", label: "Customer tenants", status: "Warning", detail: "1 of 5 tenants has no GDAP delegation", fix: { label: "Review tenants", command: null, route: "/tenants" } },
    { id: "pwsh", label: "PowerShell 7", status: "Ok", detail: "pwsh 7.4.5", fix: null },
    {
      id: "exchange-module", label: "Exchange Online module", status: "NotConfigured",
      detail: "pwsh found, ExchangeOnlineManagement not installed",
      fix: { label: "Install module", command: "pwsh -c \"Install-Module ExchangeOnlineManagement -Scope CurrentUser\"", route: null, installId: "exchange-module" },
    },
  ],
  capabilities: { graph: true, exchange: false, partnerCenter: true },
};

const personWorkspace = {
  tenantId: tenants[0].id,
  userId: "u1",
  profile: {
    status: "Ok",
    data: {
      id: "u1", displayName: "Maya Chen", upn: "maya.chen@contoso.com", mail: "maya.chen@contoso.com", accountEnabled: true,
      jobTitle: "Operations Analyst", department: "Operations", onPremisesSyncEnabled: false,
      createdDateTime: minutesAgo(525600), lastSignIn: minutesAgo(42),
    },
  },
  licenses: {
    status: "Ok",
    data: [
      { skuPartNumber: "SPE_E3", skuId: "05e9a617-0261-4cee-bb44-138d3ef5d965" },
      { skuPartNumber: "VISIOCLIENT", skuId: "c5928f49-12ba-48f7-ada3-0d743a3601d5" },
    ],
  },
  groups: {
    status: "Ok",
    data: [
      { id: "g1", displayName: "All Staff", category: "Microsoft365" },
      { id: "g3", displayName: "Managed Workstations", category: "Security" },
      { id: "g7", displayName: "Operations - All (dynamic)", category: "Dynamic" },
      { id: "g8", displayName: "ops-announcements@contoso.com", category: "Distribution" },
    ],
  },
  authMethods: { status: "Ok", data: ["password", "microsoftAuthenticator", "fido2"] },
  mailbox: { status: "Unavailable", reason: "Exchange Online module (ExchangeOnlineManagement) is not installed on this workbench." },
  devices: {
    status: "Ok",
    data: [
      { id: "d1", deviceName: "CONTOSO-LT-0142", operatingSystem: "Windows", osVersion: "10.0.26100.2033", complianceState: "compliant", lastSyncDateTime: minutesAgo(95), managementAgent: "mdm" },
      { id: "d2", deviceName: "Maya's iPhone", operatingSystem: "iOS", osVersion: "18.6", complianceState: "noncompliant", lastSyncDateTime: minutesAgo(2900), managementAgent: "mdm" },
    ],
  },
  recentRuns: { status: "Ok", data: [runs[0], runs[1]] },
};

// --- Operations and evidence (0.9.0) -----------------------------------------------------------

const parityLimitations = [
  "SharePoint direct permissions, app role assignments, Exchange mailbox/calendar permissions and Teams-only private channels are not compared.",
];

function parityItem(id, name, category, eligible, reason = null) {
  return { id: `group:${id}`, action: "AddMember", objectType: "group", objectId: id, objectName: name, destructive: false, eligible, category, reason };
}

const parityPlan = {
  operationId: "access-parity",
  operationName: "Access parity",
  tenantId: tenants[0].id,
  target: { kind: "user", id: "u1", displayName: "Maya Chen" },
  preflight: [
    { name: "Source user", status: "Ok", detail: "Priya Shah (priya.shah@contoso.com)" },
    { name: "Target user", status: "Ok", detail: "Maya Chen (maya.chen@contoso.com)" },
    { name: "Source memberships", status: "Info", detail: "9 direct memberships read" },
  ],
  items: [
    parityItem("g11", "Finance - Reporting", "Security", true),
    parityItem("g12", "Project Northwind", "Microsoft365", true),
    parityItem("g13", "VPN Users", "Security", true),
    parityItem("g14", "Power BI Pro Users", "Security", true),
    parityItem("g15", "Finance - All (dynamic)", "Dynamic", false, "Dynamic membership rule; membership follows the user's attributes, not PCB."),
    parityItem("g16", "Helpdesk Tier 2 Admins", "RoleAssignable", false, "Role-assignable group; PCB never grants privileged access by copying it."),
    parityItem("g17", "finance-team@contoso.com", "Distribution", false, "Distribution list; manage its members in Exchange Online."),
    parityItem("g1", "All Staff", "AlreadyMember", false, "Target is already a member."),
    { id: "role:r1", action: "AssignRole", objectType: "directoryRole", objectId: "r1", objectName: "Reports Reader", destructive: false, eligible: false, category: "DirectoryRole", reason: "Directory roles are never copied; assign them deliberately." },
  ],
  warnings: ["Priya Shah holds 1 directory role; it is listed but not copied."],
  limitations: parityLimitations,
};

function evidenceFor(run) {
  const base = {
    runId: run.id, operationId: run.workflowId, operationName: run.workflowName,
    tenant: { id: run.tenantId, displayName: run.tenantName, tenantId: "aa11..." },
    target: run.targetId ? { kind: "user", id: run.targetId, displayName: run.targetDisplayName ?? run.targetId } : null,
    operator: run.operator, startedAt: run.startedAt,
    completedAt: new Date(new Date(run.startedAt).getTime() + run.durationMs).toISOString(),
    outcome: run.outcome ?? (run.succeeded ? "Succeeded" : "Failed"),
    preflight: [], plan: [], changes: [], verification: [], warnings: [], limitations: [], failures: [],
    ticketNotes: `${run.workflowName} for ${run.targetDisplayName ?? run.tenantName} - ${run.outcome ?? "recorded"}.`,
  };
  if (run.id === "r6") {
    const eligible = parityPlan.items.filter((i) => i.eligible);
    return {
      ...base,
      preflight: parityPlan.preflight,
      plan: parityPlan.items,
      changes: [
        ...eligible.slice(0, 3).map((i) => ({ planItemId: i.id, action: i.action, objectName: i.objectName, attempted: true, succeeded: true, detail: "Added" })),
        { planItemId: eligible[3].id, action: "AddMember", objectName: eligible[3].objectName, attempted: true, succeeded: false, detail: "Graph 403: Insufficient privileges to complete the operation." },
        { planItemId: "group:g1", action: "AddMember", objectName: "All Staff", attempted: false, succeeded: true, detail: "Already a member" },
      ],
      verification: [
        ...eligible.slice(0, 3).map((i) => ({ name: `Maya Chen is a member of ${i.objectName}`, passed: true, detail: "Re-read from Microsoft Graph" })),
        { name: "Maya Chen is a member of Power BI Pro Users", passed: false, detail: "Not a member after apply" },
      ],
      warnings: parityPlan.warnings,
      limitations: parityLimitations,
      failures: ["Power BI Pro Users: Graph 403: Insufficient privileges to complete the operation."],
      ticketNotes: [
        "Mirrored access for Maya Chen from Priya Shah (Contoso Ltd).",
        "Outcome: partially succeeded (3 of 4 groups added and verified).",
        "Added and verified: Finance - Reporting, Project Northwind, VPN Users.",
        "Not added: Power BI Pro Users (Graph 403, insufficient privileges).",
        "Not copied by design: Finance - All (dynamic), Helpdesk Tier 2 Admins (role-assignable), finance-team@contoso.com (distribution list), Reports Reader (directory role).",
        "Not compared: SharePoint direct permissions, app role assignments, mailbox/calendar permissions, Teams private channels.",
        "Nothing was removed.",
      ].join("\n"),
    };
  }
  if (run.error) {
    return {
      ...base,
      changes: [{ planItemId: "usage-location", action: "SetUsageLocation", objectName: "Usage location US", attempted: true, succeeded: true, detail: "Set" }],
      verification: [{ name: "License SPE_E3 applied", passed: false, detail: run.error }],
      failures: [run.error],
    };
  }
  return base;
}

const offboardPlan = {
  operationId: "offboarding",
  operationName: "Offboarding",
  tenantId: tenants[0].id,
  target: { kind: "user", id: "u10", displayName: "Priya Shah" },
  preflight: [
    { name: "User", status: "Ok", detail: "Priya Shah (priya.shah@contoso.com)" },
    { name: "Sign-in", status: "Info", detail: "enabled" },
    { name: "Directory sync", status: "Ok", detail: "Cloud-only account." },
    { name: "Exchange Online", status: "Warning", detail: "Exchange Online module (ExchangeOnlineManagement) is not installed on this workbench." },
  ],
  items: [
    { id: "block-sign-in", action: "BlockSignIn", objectType: "user", objectId: "u10", objectName: "Priya Shah", destructive: false, eligible: true, category: "SignIn" },
    { id: "revoke-sessions", action: "RevokeSessions", objectType: "user", objectId: "u10", objectName: "Priya Shah", destructive: false, eligible: true, category: "SignIn" },
    { id: "convert-mailbox", action: "ConvertToShared", objectType: "mailbox", objectId: "priya.shah@contoso.com", objectName: "Priya Shah", destructive: false, eligible: false, category: "Mailbox", reason: "Exchange Online module (ExchangeOnlineManagement) is not installed on this workbench." },
    { id: "hide-from-gal", action: "HideFromGal", objectType: "mailbox", objectId: "priya.shah@contoso.com", objectName: "Priya Shah", destructive: false, eligible: false, category: "Mailbox", reason: "Not executable yet: PCB has no Exchange operation for hiding from the address list. Do it in the Exchange admin center." },
    { id: "group:g11", action: "RemoveMember", objectType: "group", objectId: "g11", objectName: "Finance - Reporting", destructive: true, eligible: true, category: "Group" },
    { id: "group:g13", action: "RemoveMember", objectType: "group", objectId: "g13", objectName: "VPN Users", destructive: true, eligible: true, category: "Group" },
    { id: "role:r1", action: "RemoveMember", objectType: "directoryRole", objectId: "r1", objectName: "Reports Reader", destructive: true, eligible: false, category: "DirectoryRole", reason: "Directory role; not removed by offboarding. Review the user's role assignments." },
    { id: "license:sku-e3", action: "RemoveLicense", objectType: "license", objectId: "sku-e3", objectName: "SPE_E3", destructive: true, eligible: true, category: "License" },
    { id: "follow-up", action: "FollowUpReminder", objectType: "user", objectId: "u10", objectName: "Priya Shah", destructive: false, eligible: false, category: "FollowUp", reason: "Recorded in the evidence; PCB does not schedule it." },
  ],
  warnings: ["Follow-up: review and delete Priya Shah on or after " + new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10) + " (30 days). Not scheduled by PCB."],
  limitations: ["Mailbox conversion, forwarding and address-list changes need Exchange Online on this workbench."],
};

const offboardEvidence = {
  runId: "r7", operationId: "offboarding", operationName: "Offboarding",
  tenant: { id: tenants[0].id, displayName: "Contoso Ltd", tenantId: "aa11..." },
  target: { kind: "user", id: "u10", displayName: "Priya Shah" },
  operator: "jspillers", startedAt: minutesAgo(1), completedAt: minutesAgo(0),
  outcome: "Succeeded",
  preflight: offboardPlan.preflight, plan: offboardPlan.items,
  changes: offboardPlan.items.map((i) => ({ planItemId: i.id, action: i.action, objectName: i.objectName, attempted: i.eligible, succeeded: i.eligible, detail: i.eligible ? "Done" : i.reason })),
  verification: [
    { name: "Sign-in blocked", passed: true, detail: "accountEnabled = false" },
    { name: "Sessions revoked", passed: true, detail: "signInSessionsValidFromDateTime updated" },
    { name: "Removed from 2 groups", passed: true },
    { name: "License SPE_E3 removed", passed: true },
  ],
  warnings: offboardPlan.warnings, limitations: offboardPlan.limitations, failures: [],
  ticketNotes: "Offboarded Priya Shah (Contoso Ltd).\nSign-in blocked and sessions revoked (verified).\nRemoved from Finance - Reporting, VPN Users; SPE_E3 license removed (verified).\nLeft for a human: mailbox conversion (Exchange not configured), hide from address list, Reports Reader role.",
};

const offboardingPolicies = {
  c1: { blockSignIn: true, revokeSessions: true, groupCleanup: "RemoveAssignable", convertMailboxToShared: true, removeLicenses: true, hideFromGal: true, forwardTo: "it-leavers@contoso.com", managerAccess: "None", wipeDevices: "Retire", followUpDays: 30 },
};
const defaultPolicy = { blockSignIn: true, revokeSessions: true, groupCleanup: "RemoveAll", convertMailboxToShared: false, removeLicenses: true, hideFromGal: false, forwardTo: null, managerAccess: "None", wipeDevices: "None", followUpDays: 0 };

function json(route, body, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}

const debugCapture = process.env.PCBRIDGE_CAPTURE_DEBUG === "1";

let unmatchedRouteCount = 0;
export function getUnmatchedRouteCount() {
  return unmatchedRouteCount;
}

// Tenant audits: a realistic finished run with a wide subject table, an unavailable check and a
// pass, so the matrix exercises the screen's widest content at phone widths.
const auditCatalog = {
  schemaVersion: 1,
  categories: [
    { id: "Identity", label: "Identity hygiene" }, { id: "Licensing", label: "Licensing" },
    { id: "Exchange", label: "Exchange / Mail" }, { id: "Devices", label: "Endpoint / Intune" }, { id: "Security", label: "Tenant security" },
  ],
  presets: [{ id: "full", name: "Full tenant health check", description: "Every available check in every category.", categories: ["Identity", "Licensing", "Exchange", "Devices", "Security"] }],
  parameters: [
    { key: "deviceStaleDays", label: "Device not synced for (days)", default: 30, min: 7, max: 365, suggested: [14, 30, 60, 90], description: "" },
    { key: "inactiveDays", label: "Inactive for (days)", default: 90, min: 7, max: 730, suggested: [30, 60, 90, 180], description: "" },
  ],
  checks: [
    ["dormant-licensed-users", "Dormant licensed users", "Licensing", ["inactiveDays"]],
    ["stale-enabled-accounts", "Stale enabled accounts", "Identity", ["inactiveDays"]],
    ["mailbox-forwarding", "External mailbox forwarding", "Exchange", []],
    ["stale-devices", "Stale managed devices", "Devices", ["deviceStaleDays"]],
    ["conditional-access-baseline", "Sign-in protection baseline", "Security", []],
  ].map(([id, name, category, parameterKeys]) => ({
    id, name, category, parameterKeys, description: "", businessImpact: "", recommendation: "", severityRules: [], requirements: [], limitations: [], version: 1,
  })),
};

const auditSummary = { checksRequested: 5, checksCompleted: 4, checksUnavailable: 1, checksErrored: 0, pass: 1, info: 0, unknown: 1, warn: 2, fail: 1, affectedSubjects: 6, health: "HighRisk" };

const auditReport = {
  schemaVersion: 1, runId: "aud-1", auditName: "Full tenant health check",
  tenant: { id: "11111111-1111-1111-1111-111111111111", displayName: "Contoso Ltd", tenantId: "aaaaaaaa-1111-2222-3333-444444444444" },
  operator: "jspillers", startedAt: minutesAgo(30), completedAt: minutesAgo(29), engineVersion: "0.10.0",
  parameters: { deviceStaleDays: 30, inactiveDays: 90 },
  requestedCheckIds: auditCatalog.checks.map((c) => c.id),
  summary: auditSummary,
  checks: [
    {
      checkId: "dormant-licensed-users", checkName: "Dormant licensed users", category: "Licensing", checkVersion: 1, status: "Completed",
      missingRequirements: [], durationMs: 820,
      notes: ["Evaluated 48 users holding at least one license that may carry cost (of 61 users in the directory); threshold 90 days."],
      findings: [
        {
          id: "dormant-licensed-users:dormant", checkId: "dormant-licensed-users", severity: "Warn", title: "Dormant licensed users",
          summary: "3 licensed users have no successful sign-in recorded in the last 90 days.",
          businessImpact: "Potential unnecessary recurring Microsoft 365 spend and possible incomplete offboarding.",
          recommendation: "Review these users for license removal, downgrade, offboarding, or documented retention.",
          columns: [
            { key: "enabled", label: "Enabled" }, { key: "licenses", label: "License / SKU(s)" },
            { key: "lastSuccessfulSignIn", label: "Last successful sign-in" }, { key: "lastSignInAttempt", label: "Last sign-in attempt" },
            { key: "daysInactive", label: "Days inactive" }, { key: "basis", label: "Basis" }, { key: "created", label: "Created" },
            { key: "adminRoles", label: "Admin roles" }, { key: "mailboxType", label: "Mailbox type" },
          ],
          subjects: [
            ["u7", "Bartholomew Featherstonehaugh-Wainwright", "bartholomew.featherstonehaugh-wainwright@contoso.onmicrosoft.com", "214", "SPE_E3; EMSPREMIUM; POWER_BI_PRO"],
            ["u8", "Priya Raman", "priya.raman@contoso.com", "131", "O365_BUSINESS_PREMIUM"],
            ["u9", "Sam Okafor", "sam.okafor@contoso.com", "98", "SPE_E3"],
          ].map(([id, name, upn, days, licenses]) => ({
            type: "user", id, name, upn, evidence: `Last successful sign-in ${days} days ago.`,
            properties: { enabled: "Yes", licenses, lastSuccessfulSignIn: "2026-03-02", lastSignInAttempt: "2026-03-04", daysInactive: days,
              basis: "last successful sign-in", created: "2021-09-14", adminRoles: id === "u7" ? "Global Administrator; Exchange Administrator" : null, mailboxType: "UserMailbox" },
            remediation: { kind: "offboarding", target: id, label: "Plan offboarding", identity: upn },
          })),
          subjectCount: 3,
        },
        {
          id: "dormant-licensed-users:unconfirmed", checkId: "dormant-licensed-users", severity: "Unknown", title: "Sign-in success not confirmed",
          summary: "1 licensed user had recent sign-in attempts, but no successful sign-in is recorded, so PCB cannot tell whether the account is in use.",
          businessImpact: "Potential unnecessary recurring Microsoft 365 spend and possible incomplete offboarding.",
          recommendation: "Check these accounts' sign-in logs: repeated failures can mean a forgotten account, or someone trying to get into it.",
          columns: [{ key: "lastSignInAttempt", label: "Last sign-in attempt" }],
          subjects: [{ type: "user", id: "u10", name: "Reception Desk", upn: "reception@contoso.com", properties: { lastSignInAttempt: "2026-09-30" } }],
          subjectCount: 1,
        },
      ],
    },
    {
      checkId: "mailbox-forwarding", checkName: "External mailbox forwarding", category: "Exchange", checkVersion: 1, status: "Completed",
      missingRequirements: [], durationMs: 14200, notes: ["Default outbound policy AutoForwardingMode: Automatic."],
      findings: [{
        id: "mailbox-forwarding:external", checkId: "mailbox-forwarding", severity: "Fail", title: "Mailboxes forwarding externally",
        summary: "1 mailbox forward sends mail to an address outside the accepted domains.",
        businessImpact: "Forwarding to an outside address quietly copies company mail out of the tenant; it is a common sign of a compromised account.",
        recommendation: "Confirm each external forward with the mailbox owner; remove any that are not documented and check the account for compromise.",
        columns: [{ key: "forwardTo", label: "Forwards to" }, { key: "forwardingType", label: "Forwarding setting" }, { key: "keepsCopy", label: "Keeps a copy" }],
        subjects: [{ type: "mailbox", id: "m1", name: "Accounts Payable", upn: "ap@contoso.com",
          properties: { forwardTo: "contoso.ap.invoices.archive.backup@some-very-long-external-domain.example", forwardingType: "ForwardingSmtpAddress", keepsCopy: "No" } }],
        subjectCount: 1,
      }],
    },
    {
      checkId: "stale-devices", checkName: "Stale managed devices", category: "Devices", checkVersion: 1, status: "Unavailable",
      statusReason: "Microsoft Intune is not available in this tenant, so Intune managed devices cannot be read (Request not applicable to target tenant.).",
      missingRequirements: ["Product: Microsoft Intune"], durationMs: 300, notes: [], findings: [],
    },
    {
      checkId: "conditional-access-baseline", checkName: "Sign-in protection baseline", category: "Security", checkVersion: 1, status: "Completed",
      missingRequirements: [], durationMs: 400, notes: [],
      findings: [{ id: "conditional-access-baseline:pass", checkId: "conditional-access-baseline", severity: "Pass", title: "Sign-in protection baseline",
        summary: "Security defaults are enabled.", columns: [], subjects: [], subjectCount: 0 }],
    },
  ],
};

const auditRuns = [{
  id: "aud-1", tenantId: auditReport.tenant.id, tenantName: "Contoso Ltd", auditName: "Full tenant health check", operator: "jspillers",
  startedAt: auditReport.startedAt, completedAt: auditReport.completedAt, schemaVersion: 1, engineVersion: "0.10.0", summary: auditSummary,
}];

export function installApiMock(page, { authenticated = true, authModeOverride = null, needsFirstUser = false } = {}) {
  async function handleApi(route) {
  const request = route.request();
  const url = new URL(request.url());
  const apiPath = url.pathname.replace(/^\/api/, "");
  const method = request.method();
  if (debugCapture) console.log(`API ${method} ${apiPath}`);

  if (method === "GET" && apiPath === "/dashboard") return json(route, dashboard);
  if (method === "GET" && apiPath === "/system/status") {
    return json(route, { profile: "Server", version: "0.9.2", authMode: authModeOverride || "Dev", needsFirstUser });
  }
  if (method === "GET" && apiPath === "/system/diagnostics") return json(route, diagnostics);
  if (method === "GET" && apiPath === "/admin/sam/status") return json(route, { bootstrapped: true });
  if (method === "GET" && apiPath === "/tenants") return json(route, tenants);
  if (method === "GET" && apiPath === "/microsoft-connections") return json(route, {
    available: true, configured: true,
    connections: [{ id: tenants[0].id, tenantId: tenants[0].tenantId, displayName: tenants[0].displayName,
      username: "admin@contoso.com", reconnectRequired: false }]
  });
  if (method === "POST" && apiPath === "/tenants/sync") return json(route, tenants);
  if (method === "GET" && apiPath === "/contracts") return json(route, contracts);
  if (method === "GET" && apiPath === "/apptemplates") return json(route, templates);
  if (method === "GET" && apiPath === "/deployments") return json(route, deployments);

  if (method === "POST" && apiPath === "/deployments") {
    const body = request.postDataJSON?.() ?? {};
    const targetIds = body.tenantIds ?? tenants.slice(0, 2).map((t) => t.id);
    const template = templates.find((t) => t.id === body.templateId) ?? templates[0];
    const results = targetIds.map((tid, i) => ({
      id: `new-${i}`, appTemplateId: template.id, tenantId: tid,
      intuneAppId: `${(9000 + i).toString(16)}...f${i}`, deployedTemplateVersion: template.contentVersion,
      status: "Succeeded",
    }));
    // Like the real API, a deploy updates the stored records, so a follow-up GET /deployments
    // (the Deploy wizard's per-tenant hints) reflects it instead of showing stale state.
    for (const r of results) {
      const existing = deployments.find((d) => d.appTemplateId === r.appTemplateId && d.tenantId === r.tenantId);
      if (existing) Object.assign(existing, { ...r, id: existing.id, lastError: undefined, lastSyncedAt: new Date().toISOString() });
      else deployments.push({ ...r, lastSyncedAt: new Date().toISOString() });
    }
    return json(route, results);
  }

  if (method === "GET" && apiPath === "/search/users") return json(route, searchResult);

  if (method === "GET" && apiPath === "/pending-actions") return json(route, pendingActions);

  if (method === "GET" && apiPath === "/workflows") return json(route, workflows);
  if (method === "GET" && apiPath === "/workflows/runs") {
    const take = Number(url.searchParams.get("take")) || runs.length;
    const targetId = url.searchParams.get("targetId");
    return json(route, runs.filter((r) => !targetId || r.targetId === targetId).slice(0, take));
  }
  let evidenceMatch = apiPath.match(/^\/workflows\/runs\/([^/]+)\/evidence$/);
  if (method === "GET" && evidenceMatch) {
    const run = runs.find((r) => r.id === evidenceMatch[1]);
    return run ? json(route, evidenceFor(run)) : json(route, "Run not found.", 404);
  }

  if (method === "POST" && /^\/tenants\/[^/]+\/operations\/access-parity\/plan$/.test(apiPath)) return json(route, parityPlan);
  if (method === "POST" && /^\/tenants\/[^/]+\/operations\/access-parity\/apply$/.test(apiPath)) return json(route, evidenceFor(runs[0]));
  if (method === "POST" && apiPath === "/provisioning/terminate/plan") return json(route, offboardPlan);
  if (method === "POST" && apiPath === "/provisioning/terminate") {
    return json(route, {
      userId: "u10", userPrincipalName: "priya.shah@contoso.com", succeeded: true,
      steps: offboardEvidence.changes.map((c) => ({ name: c.action, success: c.succeeded, detail: c.detail })),
      evidence: offboardEvidence, policy: offboardingPolicies.c1,
    });
  }
  evidenceMatch = apiPath.match(/^\/contracts\/([^/]+)\/offboarding-policy$/);
  if (evidenceMatch && method === "GET") return json(route, offboardingPolicies[evidenceMatch[1]] ?? defaultPolicy);
  if (evidenceMatch && method === "PUT") {
    offboardingPolicies[evidenceMatch[1]] = request.postDataJSON?.() ?? defaultPolicy;
    return json(route, offboardingPolicies[evidenceMatch[1]]);
  }

  let match = apiPath.match(/^\/workflows\/([^/]+)\/diagnose$/);
  if (method === "POST" && match) return json(route, mailboxDiagnosis);

  match = apiPath.match(/^\/workflows\/([^/]+)\/remediate$/);
  if (method === "POST" && match) {
    return json(route, {
      succeeded: true,
      steps: [
        { name: "Enable archive mailbox", success: true, detail: "Enable-Mailbox -Archive" },
        { name: "Enable auto-expanding archive", success: true, detail: "Set-Mailbox -AutoExpandingArchive" },
        { name: "Assign retention policy", success: true, detail: "Default MRM Policy" },
        { name: "Clear retention hold + ELC block", success: true, detail: "RetentionHoldEnabled = false" },
        { name: "Trigger Managed Folder Assistant", success: true, detail: "Start-ManagedFolderAssistant" },
      ],
      postState: { ...mailboxDiagnosis, healthy: false },
      ephemeral: {},
    });
  }

  match = apiPath.match(/^\/directory\/([^/]+)\/skus$/);
  if (method === "GET" && match) return json(route, skus);
  match = apiPath.match(/^\/directory\/([^/]+)\/groups$/);
  if (method === "GET" && match) return json(route, groups);
  match = apiPath.match(/^\/directory\/([^/]+)\/users$/);
  if (method === "GET" && match) {
    const q = (url.searchParams.get("search") ?? "").toLowerCase();
    return json(route, directoryUsers.filter((u) => !q || u.displayName.toLowerCase().includes(q) || u.userPrincipalName.toLowerCase().includes(q)));
  }

  match = apiPath.match(/^\/contracts\/([^/]+)\/provisioning-template$/);
  if (method === "GET" && match) return json(route, provisioningTemplate);

  match = apiPath.match(/^\/contracts\/([^/]+)\/plan$/);
  if (method === "GET" && match) {
    return json(route, [
      { tenantId: tenants[0].id, tenantName: "Contoso Ltd", templateId: "t1", templateName: "7-Zip 24.08", action: "UpToDate" },
      { tenantId: tenants[1].id, tenantName: "Fabrikam Inc", templateId: "t1", templateName: "7-Zip 24.08", action: "Update" },
    ]);
  }

  if (method === "GET" && apiPath === "/admin/users") return json(route, instanceUsers);
  match = apiPath.match(/^\/admin\/users\/([^/]+)\/roles$/);
  if (method === "PUT" && match) {
    const targetUser = instanceUsers.find((u) => u.id === match[1]);
    if (!targetUser) return json(route, { message: "Not found." }, 404);
    const body = request.postDataJSON?.() ?? {};
    targetUser.roles = body.roles ?? [];
    targetUser.authorizationVersion += 1;
    return json(route, targetUser);
  }

  if (method === "GET" && apiPath === "/auth/mode") return json(route, { mode: authModeOverride || "Dev" });
  if (method === "POST" && apiPath === "/auth/login") return json(route, { accessToken: "fake.jwt.token", user: meProfile });
  if (method === "POST" && apiPath === "/auth/register") return json(route, { accessToken: "fake.jwt.token", user: meProfile });
  if (method === "GET" && apiPath === "/auth/me") return authenticated ? json(route, meProfile) : json(route, {}, 401);
  if (method === "POST" && apiPath === "/auth/logout") return json(route, {}, 204);
  if (method === "GET" && apiPath === "/auth/passkey") return json(route, passkeys);
  // Login's conditional-passkey (WebAuthn autofill) effect fires this on mount, unprompted --
  // every capture that renders Login/Security needs it mocked even though nothing clicks a button.
  if (method === "POST" && apiPath === "/auth/passkey/login/options")
    return json(route, { challengeKey: "mock-challenge-key", options: {} });
  if (method === "GET" && apiPath === "/mcp-tokens") return json(route, mcpTokens);
  if (method === "GET" && apiPath === "/config-sections") return json(route, configSections);

  match = apiPath.match(/^\/tenants\/([^/]+)\/people\/([^/]+)$/);
  if (method === "GET" && match) return json(route, personWorkspace);
  match = apiPath.match(/^\/tenants\/([^/]+)\/access$/);
  if (method === "GET" && match) {
    return json(route, [
      { userId: "u1", email: "jspillers@example.com", role: "Owner", grantedAt: minutesAgo(43200) },
      { userId: "u2", email: "maya.chen@example.com", role: "Operator", grantedAt: minutesAgo(20160) },
    ]);
  }

  if (method === "GET" && apiPath === "/tenant-audits/catalog") return json(route, auditCatalog);
  match = apiPath.match(/^\/tenants\/([^/]+)\/audits$/);
  if (method === "GET" && match) return json(route, auditRuns);
  if (method === "POST" && match) return json(route, auditReport);
  match = apiPath.match(/^\/tenants\/([^/]+)\/audits\/([^/]+)$/);
  if (method === "GET" && match) return json(route, auditReport);

  match = apiPath.match(/^\/tenants\/([^/]+)\/config-snapshots$/);
  if (method === "GET" && match) return json(route, snapshotRuns);
  match = apiPath.match(/^\/tenants\/([^/]+)\/config-snapshots\/diff$/);
  if (method === "GET" && match) return json(route, snapshotDiff);

  // An unmocked/mistyped route silently succeeding with an empty 200 can make the app render a
  // broken or empty state that happens not to overflow -- a false-clean result for exactly the
  // reason this whole matrix exists. Track it and warn immediately rather than staying silent;
  // callers can check getUnmatchedRouteCount() to fail the run explicitly.
  unmatchedRouteCount++;
  console.warn(`  MOCK MISS: ${method} ${apiPath} -- not mocked, returning empty 200`);
  return json(route, {});
}

  return page.route("**/*", (route) => {
    const pathname = new URL(route.request().url()).pathname;
    return pathname.startsWith("/api/") ? handleApi(route) : route.continue();
  });
}
