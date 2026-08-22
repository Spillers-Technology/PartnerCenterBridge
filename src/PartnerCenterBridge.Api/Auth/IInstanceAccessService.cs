using PartnerCenterBridge.Core;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Instance-wide configuration authorization. This service never consults tenant grants and must
/// never be used as a substitute for <see cref="ITenantAccessService.HasRoleAsync"/>.
/// </summary>
public interface IInstanceAccessService
{
    Guid? CurrentUserId { get; }
    Task<InstanceRole> GetRolesAsync(CancellationToken ct);
    Task<bool> HasPermissionAsync(InstancePermission permission, CancellationToken ct);
}
