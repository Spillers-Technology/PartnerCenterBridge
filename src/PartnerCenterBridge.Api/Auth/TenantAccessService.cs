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
/// <see cref="IsSystemAdmin"/> deliberately does <b>not</b> bypass tenant checks here. It gates
/// only instance-level infrastructure (<c>AdminController</c>'s SAM refresh-token management).
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

    /// <summary>Instance infrastructure admin only -- see the type-level remarks. Never used to gate tenant access.</summary>
    public bool IsSystemAdmin =>
        !IsLocalToken || _accessor.HttpContext?.User.FindFirst(LocalTokenService.SystemAdminClaim)?.Value == "true";

    public Guid? CurrentUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(LocalTokenService.UserIdClaim), out var id) ? id : null;

    public async Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct)
    {
        if (!IsLocalToken) return true; // OIDC/dev-auth operator plane: unchanged, all-access.

        var userId = CurrentUserId!.Value;
        var now = DateTimeOffset.UtcNow;

        var grant = await _db.TenantAccessGrants.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.UserId == userId)
            .Where(g => g.ExpiresAt == null || g.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);

        return grant is not null && grant.Role >= minimum;
    }
}
