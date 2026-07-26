namespace PartnerCenterBridge.Core.Abstractions;

/// <summary>
/// Identifies whoever is making the current request, for audit attribution. Implemented per-host
/// (the Api project reads <c>HttpContext.User</c>) so Core and Data stay free of ASP.NET Core's
/// request-pipeline types.
/// </summary>
public interface ICurrentActor
{
    /// <summary>Local <c>AppUser</c> id, when the caller authenticated via <c>Auth:Mode=Local</c>. Null for OIDC/dev-auth/background callers.</summary>
    Guid? UserId { get; }

    /// <summary>Display name for the audit trail regardless of auth mode. "anonymous" if unauthenticated, "system" for background/CLI work.</summary>
    string Name { get; }
}
