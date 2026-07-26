using PartnerCenterBridge.Core;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// The single place controllers ask "can the current caller do X to tenant Y" -- resource-based
/// authorization (the tenant id isn't known statically, so a plain <c>[Authorize(Policy=...)]</c>
/// attribute can't express it) kept as one explicit, reusable check rather than each controller
/// re-deriving the "system admin OR sufficient grant" rule for itself.
/// </summary>
public interface ITenantAccessService
{
    /// <summary>True if the current caller is a system admin (bypasses per-tenant grants entirely) or holds a non-expired grant at or above <paramref name="minimum"/> on <paramref name="tenantId"/>.</summary>
    Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct);

    /// <summary>True if the current caller is a system admin.</summary>
    bool IsSystemAdmin { get; }

    /// <summary>The current caller's local <c>AppUser</c> id, or null if this request wasn't authenticated via <c>Auth:Mode=Local</c>.</summary>
    Guid? CurrentUserId { get; }
}
