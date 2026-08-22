using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Per-tenant role gate for locally authenticated users only. A token with no
/// <see cref="LocalTokenService.UserIdClaim"/> (i.e. every OIDC or dev-auth token) is treated as
/// fully authorized -- this feature only *adds* restriction when <c>Auth:Mode=Local</c> is in use,
/// so existing Authentik-backed and docker-compose dev deployments see no behavior change.
/// </summary>
/// <remarks>
/// Instance roles deliberately do <b>not</b> appear here. They gate instance-wide configuration
/// through <see cref="IInstanceAccessService"/>, but never substitute for a tenant role check.
/// Tenant power is 100% driven by <see cref="TenantAccessGrant"/> -- creating or first-syncing a
/// tenant grants its creator Owner (see <c>TenantsController</c>), and Owners share from there.
/// Two separate concerns, two separate mechanisms; conflating them was the muddle this replaced.
/// </remarks>
public class TenantAccessService : ITenantAccessService
{
    private readonly IHttpContextAccessor _accessor;
    private readonly BridgeDbContext _db;

    public TenantAccessService(IHttpContextAccessor accessor, BridgeDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    private bool IsLocalToken =>
        _accessor.HttpContext?.User.HasClaim(c => c.Type == LocalTokenService.UserIdClaim) == true;

    public Guid? CurrentUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(LocalTokenService.UserIdClaim), out var id) ? id : null;

    public async Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct)
    {
        if (!IsLocalToken) return true; // OIDC/dev-auth operator plane: unchanged, all-access.

        var userId = CurrentUserId!.Value;
        var grant = await _db.TenantAccessGrants.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.UserId == userId)
            .Select(g => new { g.Role, g.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        return grant is not null
            && grant.Role >= minimum
            && (grant.ExpiresAt is null || grant.ExpiresAt > DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct)
    {
        if (!IsLocalToken) return null;

        var userId = CurrentUserId!.Value;
        var grants = await _db.TenantAccessGrants.AsNoTracking()
            .Where(grant => grant.UserId == userId)
            .Select(grant => new { grant.TenantId, grant.Role, grant.ExpiresAt })
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return grants
            .Where(grant => grant.Role >= minimum
                && (grant.ExpiresAt is null || grant.ExpiresAt > now))
            .Select(grant => grant.TenantId)
            .ToList();
    }
}
