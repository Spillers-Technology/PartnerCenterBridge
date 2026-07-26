namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// One user's access to one tenant. This is the collaboration/sharing primitive for local
/// accounts: an <see cref="TenantRole.Owner"/> on a tenant can grant or revoke another registered
/// user's access to it (self-service, like sharing a document) instead of every grant needing a
/// central admin. <see cref="AppUser.IsSystemAdmin"/> users bypass this table entirely.
/// </summary>
public class TenantAccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    public TenantRole Role { get; set; } = TenantRole.Viewer;

    public Guid GrantedByUserId { get; set; }

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional time-boxed access (e.g. a temp/contractor engagement). Null = indefinite.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
