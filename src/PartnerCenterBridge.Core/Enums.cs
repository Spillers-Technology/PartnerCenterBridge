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
