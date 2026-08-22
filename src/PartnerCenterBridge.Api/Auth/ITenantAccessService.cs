using PartnerCenterBridge.Core;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// The single place controllers ask "can the current caller do X to tenant Y" -- resource-based
/// authorization (the tenant id isn't known statically, so a plain <c>[Authorize(Policy=...)]</c>
/// attribute can't express it) kept as one explicit, reusable check rather than each controller
/// re-deriving active-grant rules for itself.
/// </summary>
public interface ITenantAccessService
{
    /// <summary>True if the current caller is non-Local (OIDC/dev-auth), which is always authorized without restriction, or if the caller is Local-mode and holds a non-expired grant at or above <paramref name="minimum"/> on <paramref name="tenantId"/>. System admin never bypasses tenant grants on its own -- see TenantAccessService's remarks.</summary>
    Task<bool> HasRoleAsync(Guid tenantId, TenantRole minimum, CancellationToken ct);

    /// <summary>The current caller's local <c>AppUser</c> id, or null if this request wasn't authenticated via <c>Auth:Mode=Local</c>.</summary>
    Guid? CurrentUserId { get; }

    /// <summary>
    /// Active tenant ids at or above <paramref name="minimum"/> for a Local caller, or null for
    /// the intentionally unrestricted OIDC/Dev operator plane.
    /// </summary>
    Task<IReadOnlyList<Guid>?> GetAuthorizedTenantIdsAsync(TenantRole minimum, CancellationToken ct);
}
