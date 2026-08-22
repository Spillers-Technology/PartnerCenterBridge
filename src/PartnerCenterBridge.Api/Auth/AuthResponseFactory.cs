using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Builds the <see cref="AuthResponse"/> (token + profile + tenant access) shared by every login path (password, TOTP challenge, passkey) so they stay in sync by construction rather than by convention.</summary>
public class AuthResponseFactory
{
    private readonly BridgeDbContext _db;
    private readonly LocalTokenService _tokens;

    public AuthResponseFactory(BridgeDbContext db, LocalTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    public async Task<AuthResponse> BuildAsync(AppUser user, CancellationToken ct)
    {
        var access = await _db.TenantAccessGrants.AsNoTracking()
            .Include(g => g.Tenant)
            .Where(g => g.UserId == user.Id
                     && (g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(g => new TenantAccessDto(g.TenantId, g.Tenant!.DisplayName, g.Role))
            .ToListAsync(ct);
        var roles = InstanceRolePermissions.Expand(user.InstanceRoles);
        var me = new MeDto(
            user.Id, user.Email, user.DisplayName,
            user.InstanceRoles.HasFlag(Core.InstanceRole.Administrator),
            user.TotpEnabled, access, roles,
            InstanceRolePermissions.PermissionNames(user.InstanceRoles),
            user.AuthorizationVersion);
        return new AuthResponse(_tokens.IssueAccessToken(user), me);
    }
}
