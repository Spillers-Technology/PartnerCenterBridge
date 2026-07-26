using Microsoft.AspNetCore.Mvc;

namespace PartnerCenterBridge.Api.Auth;

public static class ControllerBaseExtensions
{
    /// <summary>The caller's local <c>AppUser</c> id, or null if this request wasn't authenticated via <c>Auth:Mode=Local</c>.</summary>
    public static Guid? LocalUserId(this ControllerBase controller) =>
        Guid.TryParse(controller.User.FindFirst(LocalTokenService.UserIdClaim)?.Value, out var id) ? id : null;
}
