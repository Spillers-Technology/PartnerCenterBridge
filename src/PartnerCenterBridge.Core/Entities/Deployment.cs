namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// Records that an <see cref="AppTemplate"/> has been applied to a <see cref="Tenant"/>. This is
/// the join that lets an update fan out to "every tenant that has template X".
/// </summary>
public class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppTemplateId { get; set; }
    public AppTemplate? AppTemplate { get; set; }

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>The Intune mobileApp id created in the customer tenant.</summary>
    public string? IntuneAppId { get; set; }

    /// <summary>Intune contentVersion id that is currently committed in the tenant.</summary>
    public string? CommittedContentVersionId { get; set; }

    /// <summary>The template's local <see cref="AppTemplate.ContentVersion"/> that was last deployed.</summary>
    public int DeployedTemplateVersion { get; set; }

    /// <summary>Assignment ids created in the tenant, for later reconciliation/cleanup.</summary>
    public List<string> AssignmentIds { get; set; } = new();

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Pending;
    public string? LastError { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
