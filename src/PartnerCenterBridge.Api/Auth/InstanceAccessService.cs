using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

public class InstanceAccessService : IInstanceAccessService
{
    private readonly IHttpContextAccessor _accessor;
    private readonly BridgeDbContext _db;

    public InstanceAccessService(IHttpContextAccessor accessor, BridgeDbContext db)
    {
        _accessor = accessor;
        _db = db;
    }

    private bool IsLocalToken =>
        _accessor.HttpContext?.User.HasClaim(c => c.Type == LocalTokenService.UserIdClaim) == true;

    public Guid? CurrentUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(LocalTokenService.UserIdClaim), out var id) ? id : null;

    public async Task<InstanceRole> GetRolesAsync(CancellationToken ct)
    {
        if (!IsLocalToken) return InstanceRole.Administrator;
        if (CurrentUserId is not { } userId) return InstanceRole.None;
        return await _db.AppUsers.AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => user.InstanceRoles)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct) =>
        !IsLocalToken || InstanceRolePermissions.Includes(await GetRolesAsync(ct), permission);
}
