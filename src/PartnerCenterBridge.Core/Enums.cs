namespace PartnerCenterBridge.Core;

/// <summary>Lifecycle status of a customer tenant in the local registry.</summary>
public enum TenantStatus
{
    Active,
    Suspended,
    /// <summary>GDAP relationship is missing or expired; the bridge cannot act in this tenant.</summary>
    NoDelegation,
    Removed
}

/// <summary>Intune install intent for a Win32 app assignment target.</summary>
public enum InstallIntent
{
    Available,
    Required,
    Uninstall,
    AvailableWithoutEnrollment
}

/// <summary>Well-known assignment target groups plus explicit group targeting.</summary>
public enum AssignmentTargetType
{
    AllDevices,
    AllLicensedUsers,
    Group
}

/// <summary>Kind of Win32 detection rule declared on an <see cref="Entities.AppTemplate"/>.</summary>
public enum DetectionRuleType
{
    MsiProductCode,
    File,
    Registry,
    PowerShellScript
}

/// <summary>Which half of a workflow a <see cref="Entities.WorkflowRun"/> recorded.</summary>
public enum WorkflowRunKind
{
    Diagnose,
    Remediate
}

/// <summary>Per-(template, tenant) deployment state, tracked so updates can fan out.</summary>
public enum DeploymentStatus
{
    Pending,
    Uploading,
    Committing,
    Assigning,
    Succeeded,
    Failed,
    /// <summary>Local desired state is newer than what is committed in the tenant.</summary>
    UpdateAvailable
}

/// <summary>
/// A registered operator's level of access to one <see cref="Entities.Tenant"/>, granted via a
/// <see cref="Entities.TenantAccessGrant"/>. Ordered low to high; callers compare with &gt;=.
/// </summary>
public enum TenantRole
{
    /// <summary>Read dashboard, history, and diagnosis output only. Cannot remediate or deploy.</summary>
    Viewer = 0,
    /// <summary>Can run known-fix workflows and Win32 deployments against the tenant.</summary>
    Operator = 1,
    /// <summary>Operator plus can grant/revoke other users' access to the tenant.</summary>
    Owner = 2
}

/// <summary>Kind of <see cref="Entities.AuditEvent"/>. Kept as a closed set so a SIEM/log sink can
/// alert on specific categories (e.g. every <see cref="TenantAccessGranted"/>) without string matching.</summary>
public enum AuditEventType
{
    UserRegistered,
    LoginSucceeded,
    LoginFailed,
    LogoutSucceeded,
    TenantAccessGranted,
    TenantAccessRevoked,
    EntityCreated,
    EntityModified,
    EntityDeleted,
    TotpEnabled,
    TotpDisabled,
    TotpChallengeFailed,
    RecoveryCodeUsed,
    PasskeyRegistered,
    PasskeyRemoved,
    PasskeyLoginSucceeded
}
