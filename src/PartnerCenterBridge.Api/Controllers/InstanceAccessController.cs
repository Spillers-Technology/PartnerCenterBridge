using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize]
public class InstanceAccessController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IInstanceAccessService _access;

    public InstanceAccessController(BridgeDbContext db, IInstanceAccessService access)
    {
        _db = db;
        _access = access;
    }

    public record InstanceUserDto(
        Guid Id, string Email, string DisplayName, bool IsActive,
        IReadOnlyList<InstanceRole> Roles, long AuthorizationVersion);

    public record ReplaceInstanceRolesRequest(
        IReadOnlyList<InstanceRole> Roles, long ExpectedAuthorizationVersion);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceUserDto>>> List(CancellationToken ct)
    {
        if (!await _access.HasPermissionAsync(InstancePermission.ManageRoles, ct)) return Forbid();
        var users = await _db.AppUsers.AsNoTracking().OrderBy(user => user.Email).ToListAsync(ct);
        return Ok(users.Select(ToDto).ToList());
    }

    [HttpPut("{id:guid}/roles")]
    public async Task<ActionResult<InstanceUserDto>> ReplaceRoles(
        Guid id, ReplaceInstanceRolesRequest req, CancellationToken ct)
    {
        if (!await _access.HasPermissionAsync(InstancePermission.ManageRoles, ct)) return Forbid();

        var requested = req.Roles.Aggregate(InstanceRole.None, (roles, role) => roles | role);
        if (!InstanceRolePermissions.IsValidAssignment(requested)
            || req.Roles.Any(role => role == InstanceRole.None)
            || req.Roles.Distinct().Count() != req.Roles.Count)
            return BadRequest("Invalid instance-role assignment.");

        await using var authorizationLock = await InstanceAuthorizationLock.AcquireAsync(_db, ct);

        if (_access.CurrentUserId is { } actorId)
        {
            var actorIsAdministrator = await _db.AppUsers.AsNoTracking()
                .AnyAsync(user => user.Id == actorId && user.IsActive
                    && (user.InstanceRoles & InstanceRole.Administrator) != 0, ct);
            if (!actorIsAdministrator) return Forbid();
            if (actorId == id) return Conflict("Administrators cannot change their own instance roles.");
        }

        var target = await _db.AppUsers.SingleOrDefaultAsync(user => user.Id == id, ct);
        if (target is null) return NotFound();
        if (!target.IsActive) return Conflict("Instance roles cannot be changed for an inactive user.");
        if (target.AuthorizationVersion != req.ExpectedAuthorizationVersion)
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                "This user's instance roles changed since you opened the editor. Refresh and try again.");

        var before = target.InstanceRoles;
        if (before.HasFlag(InstanceRole.Administrator)
            && !requested.HasFlag(InstanceRole.Administrator))
        {
            var anotherAdministrator = await _db.AppUsers.AsNoTracking()
                .AnyAsync(user => user.Id != target.Id && user.IsActive
                    && (user.InstanceRoles & InstanceRole.Administrator) != 0, ct);
            if (!anotherAdministrator)
                return Conflict("At least one active Administrator must remain.");
        }

        if (before == requested) return Ok(ToDto(target));

        target.InstanceRoles = requested;
        target.AuthorizationVersion++;
        authorizationLock.State.Revision++;
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.InstanceRolesChanged,
            ActorUserId = _access.CurrentUserId,
            ActorName = ControllerContext.HttpContext?.User.Identity?.Name ?? "",
            EntityType = nameof(AppUser),
            EntityId = target.Id.ToString(),
            Detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                target.Email,
                before = InstanceRolePermissions.Expand(before).Select(role => role.ToString()),
                after = InstanceRolePermissions.Expand(requested).Select(role => role.ToString()),
                authorizationVersion = target.AuthorizationVersion
            })
        });
        await _db.SaveChangesAsync(ct);
        await authorizationLock.CommitAsync(ct);
        return Ok(ToDto(target));
    }

    private static InstanceUserDto ToDto(AppUser user) => new(
        user.Id, user.Email, user.DisplayName, user.IsActive,
        InstanceRolePermissions.Expand(user.InstanceRoles), user.AuthorizationVersion);
}
