using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>A subset of mailbox properties surfaced to the UI.</summary>
public record MailboxInfo(
    string UserPrincipalName,
    string DisplayName,
    string RecipientTypeDetails,
    string? ForwardingSmtpAddress,
    bool DeliverToMailboxAndForward);

/// <summary>Outcome of one EXO operation, reusing the per-step shape of provisioning runs.</summary>
public class ExoResult
{
    public List<ProvisioningStep> Steps { get; set; } = new();
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Success);
}

/// <summary>
/// Diagnostic snapshot of a mailbox's archive posture — everything that determines whether items
/// actually flow to the archive. Surfaced verbatim to the operator for transparency.
/// </summary>
public record ArchiveState(
    string UserPrincipalName,
    string PrimarySize,
    long PrimaryItemCount,
    string ProhibitSendReceiveQuota,
    bool ArchiveEnabled,
    string ArchiveStatus,
    bool AutoExpandingArchiveEnabled,
    string? ArchiveQuota,
    string? ArchiveWarningQuota,
    string? ArchiveSize,
    long ArchiveItemCount,
    string? RetentionPolicy,
    bool RetentionHoldEnabled,
    bool ElcProcessingDisabled);

/// <summary>Which fixes to apply when remediating a stuck/full archive. All default to the safe fix.</summary>
public class ArchiveRemediationOptions
{
    /// <summary>Enable auto-expanding archive (the "TB expansion" that grows the archive automatically).</summary>
    public bool EnableAutoExpandingArchive { get; set; } = true;
    /// <summary>Retention policy to assign if the mailbox has none (its archive tag drives the move).</summary>
    public string? RetentionPolicyName { get; set; } = "Default MRM Policy";
    /// <summary>Clear a retention hold and re-enable ELC processing — both silently block the assistant.</summary>
    public bool ClearProcessingBlocks { get; set; } = true;
    /// <summary>Kick the Managed Folder Assistant so the move runs now instead of on its own schedule.</summary>
    public bool TriggerProcessing { get; set; } = true;
}

/// <summary>Result of a remediation run: the steps taken plus the resulting archive state.</summary>
public class ArchiveRemediationResult
{
    public List<ProvisioningStep> Steps { get; set; } = new();
    public ArchiveState? State { get; set; }
    public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Success);
}

/// <summary>
/// Exchange Online operations that Graph cannot do — mailbox configuration such as converting a
/// terminated user's mailbox to shared and setting forwarding. Backed by the Exchange Online
/// PowerShell V3 module (app-only certificate auth) invoked out-of-process.
/// </summary>
public interface IExchangeOnlineService
{
    Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default);

    /// <summary>Convert a mailbox to shared and optionally set SMTP forwarding — the classic offboard step.</summary>
    Task<ExoResult> ConvertToSharedAsync(
        Tenant tenant,
        string identity,
        string? forwardingSmtpAddress,
        bool deliverToMailboxAndForward,
        CancellationToken ct = default);

    Task<IReadOnlyList<MailboxInfo>> ListSharedMailboxesAsync(Tenant tenant, CancellationToken ct = default);

    /// <summary>Diagnose a mailbox's archive posture (sizes, quotas, and every processing blocker).</summary>
    Task<ArchiveState?> GetArchiveStateAsync(Tenant tenant, string identity, CancellationToken ct = default);

    /// <summary>
    /// Idempotently fix "mailbox full / not archiving": enable archive + auto-expand, assign a
    /// retention policy, clear retention hold / ELC blocks, and trigger the Managed Folder Assistant.
    /// Returns the steps taken and the resulting state.
    /// </summary>
    Task<ArchiveRemediationResult> RemediateArchiveAsync(
        Tenant tenant, string identity, ArchiveRemediationOptions options, CancellationToken ct = default);

    /// <summary>
    /// Re-trigger the Managed Folder Assistant and return the refreshed state — the "nudge" for the
    /// asynchronous move that often needs poking more than once before it visibly progresses.
    /// </summary>
    Task<ArchiveRemediationResult> NudgeArchiveAsync(Tenant tenant, string identity, CancellationToken ct = default);
}
