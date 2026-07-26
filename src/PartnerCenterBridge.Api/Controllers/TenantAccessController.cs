using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// The "share tenant access for collab" surface: an <see cref="TenantRole.Owner"/> on a tenant
/// (or a system admin) can grant or revoke another registered user's access to it, self-service --
/// no central admin has to be the bottleneck for every hand-off. Only meaningful under
/// <c>Auth:Mode=Local</c>; under OIDC every authenticated operator already has full access, so
/// there is nothing to share.
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId:guid}/access")]
[Authorize]
public class TenantAccessController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public TenantAccessController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantGrantDto>>> List(Guid tenantId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Owner, ct)) return Forbid();

        var grants = await _db.TenantAccessGrants.AsNoTracking()
            .Include(g => g.User)
            .Where(g => g.TenantId == tenantId)
            .ToListAsync(ct);
        return Ok(grants.Select(g => new TenantGrantDto(g.UserId, g.User!.Email, g.Role, g.GrantedAt, g.ExpiresAt)).ToList());
    }

    /// <summary>Grant (or re-grant, e.g. to change role/expiry) another registered user access to this tenant.</summary>
    [HttpPost]
    public async Task<IActionResult> Grant(Guid tenantId, GrantAccessRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Owner, ct)) return Forbid();

        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");

        var email = req.Email.Trim().ToLowerInvariant();
        var targetUser = await _db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (targetUser is null) return NotFound("No registered account with that email. They need to register first.");

        // Sharing only means something between local accounts -- an OIDC/dev-auth caller (who
        // passed the check above by virtue of that plane's unrestricted access) has no AppUser id
        // to record as the grantor.
        if (_access.CurrentUserId is not { } actorId)
            return BadRequest("Tenant access sharing applies only to Auth:Mode=Local accounts.");

        var grant = await _db.TenantAccessGrants
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.UserId == targetUser.Id, ct);
        if (grant is null)
        {
            grant = new TenantAccessGrant { TenantId = tenantId, UserId = targetUser.Id };
            _db.TenantAccessGrants.Add(grant);
        }
        grant.Role = req.Role;
        grant.ExpiresAt = req.ExpiresAt;
        grant.GrantedByUserId = actorId;
        grant.GrantedAt = DateTimeOffset.UtcNow;

        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.TenantAccessGranted,
            ActorUserId = actorId,
            ActorName = User.Identity?.Name ?? "",
            TenantId = tenantId,
            EntityType = nameof(TenantAccessGrant),
            EntityId = targetUser.Id.ToString(),
            Detail = System.Text.Json.JsonSerializer.Serialize(new { targetUser.Email, req.Role, req.ExpiresAt })
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Revoke(Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Owner, ct)) return Forbid();

        var grant = await _db.TenantAccessGrants
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.UserId == userId, ct);
        if (grant is null) return NotFound();

        _db.TenantAccessGrants.Remove(grant);
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.TenantAccessRevoked,
            ActorUserId = _access.CurrentUserId,
            ActorName = User.Identity?.Name ?? "",
            TenantId = tenantId,
            EntityType = nameof(TenantAccessGrant),
            EntityId = userId.ToString()
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
