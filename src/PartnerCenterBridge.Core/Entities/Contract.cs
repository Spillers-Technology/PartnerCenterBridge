namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// An MSP contract / client profile. Owns the desired state that every tenant on the
/// contract should be reconciled to. This is the "small state machine": the variance in
/// deployments across contracts is expressed as data here, not code.
/// </summary>
public class Contract
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Notes { get; set; }

    /// <summary>Tenants served under this contract.</summary>
    public ICollection<Tenant> Tenants { get; set; } = new List<Tenant>();

    /// <summary>
    /// App templates that every tenant on this contract should have deployed. Additional
    /// desired-state facets (groups, license SKUs, policies) are added in later phases.
    /// </summary>
    public ICollection<AppTemplate> DesiredApps { get; set; } = new List<AppTemplate>();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
