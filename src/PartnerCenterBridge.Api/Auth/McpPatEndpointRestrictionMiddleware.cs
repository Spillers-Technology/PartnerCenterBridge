using System.IdentityModel.Tokens.Jwt;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Keeps MCP personal access tokens confined to the MCP transport. Interactive login tokens do
/// not carry a <c>jti</c>; MCP PATs do, and must never become general-purpose API credentials.
/// </summary>
public class McpPatEndpointRestrictionMiddleware
{
    private readonly RequestDelegate _next;

    public McpPatEndpointRestrictionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var isMcpPat = context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(claim => claim.Type == JwtRegisteredClaimNames.Jti);

        if (isMcpPat && !context.Request.Path.StartsWithSegments("/mcp"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
