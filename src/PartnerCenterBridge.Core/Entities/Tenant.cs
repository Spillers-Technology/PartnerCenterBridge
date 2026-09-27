namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A customer tenant the MSP has a GDAP relationship with. Seeded from the Partner Center
/// customer list and used as the target for every Graph operation.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Entra tenant id (the customer's directory id) used as the token exchange authority.</summary>
    public required string TenantId { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>
    /// Primary/default domain, handy for display and disambiguation. Supplied by Partner Center or
    /// by whoever registered the tenant, so it is never used as an authorization target -- in
    /// particular not as the Exchange Online organization (see <see cref="ExchangeOrganization"/>).
    /// </summary>
    public string? DefaultDomain { get; set; }

    /// <summary>
    /// The tenant's initial <c>*.onmicrosoft.com</c> domain as reported by Microsoft Graph
    /// (<c>GET /organization</c> with a token for <see cref="TenantId"/>, the verified domain
    /// flagged <c>isInitial</c>). This, not <see cref="DefaultDomain"/>, is what Exchange Online
    /// connects to.
    /// </summary>
    public string? ExchangeOrganization { get; set; }

    /// <summary>When <see cref="ExchangeOrganization"/> was last read from Microsoft Graph.</summary>
    public DateTimeOffset? ExchangeOrganizationVerifiedAt { get; set; }

    /// <summary>Active GDAP relationship id backing our delegated access, if known.</summary>
    public string? GdapRelationshipId { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>
    /// Gates mutating MCP tool calls against this tenant. Defaults to the safe Queue mode;
    /// settable only by a system admin (see AdminController.SetMcpMode) -- never by the tenant's
    /// own Owner, deliberately, since this is a platform safety policy, not tenant power.
    /// </summary>
    public McpApprovalMode McpApprovalMode { get; set; } = McpApprovalMode.Queue;

    /// <summary>Contract this tenant is served under. A contract may cover many tenants.</summary>
    public Guid? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
