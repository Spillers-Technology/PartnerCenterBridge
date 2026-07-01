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

    /// <summary>Primary/default domain, handy for display and disambiguation.</summary>
    public string? DefaultDomain { get; set; }

    /// <summary>Active GDAP relationship id backing our delegated access, if known.</summary>
    public string? GdapRelationshipId { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>Contract this tenant is served under. A contract may cover many tenants.</summary>
    public Guid? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
