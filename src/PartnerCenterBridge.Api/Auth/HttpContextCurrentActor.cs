using System.Security.Claims;
using PartnerCenterBridge.Core.Abstractions;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Reads the acting user off the current request for <see cref="ICurrentActor"/>, whichever auth mode produced it.</summary>
public class HttpContextCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentActor(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(User?.FindFirstValue(LocalTokenService.UserIdClaim), out var id) ? id : null;

    public string Name => User?.Identity?.Name ?? "anonymous";
}
