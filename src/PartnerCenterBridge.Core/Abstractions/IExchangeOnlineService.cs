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
}
