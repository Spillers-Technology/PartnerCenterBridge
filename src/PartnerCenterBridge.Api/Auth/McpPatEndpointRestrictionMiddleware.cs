using System.IdentityModel.Tokens.Jwt;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Keeps MCP personal access tokens confined to the MCP transport. Interactive Local login tokens
/// do not carry a <c>jti</c>; MCP PATs (issued only by <see cref="LocalTokenService.IssueMcpToken"/>,
/// itself only reachable under <see cref="AuthModeInfo.Local"/> -- see McpTokensController's
/// CurrentUserId gate) do, and must never become general-purpose API credentials. Scoped to Local
/// mode specifically because standard OIDC access tokens (e.g. Entra ID) commonly carry their own
/// unrelated <c>jti</c> claim, which this signal must not misread as an MCP PAT.
/// </summary>
public class McpPatEndpointRestrictionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AuthModeInfo _authMode;

    public McpPatEndpointRestrictionMiddleware(RequestDelegate next, AuthModeInfo authMode)
    {
        _next = next;
        _authMode = authMode;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isMcpPat = _authMode.IsLocal
            && context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(claim => claim.Type == JwtRegisteredClaimNames.Jti);

        if (isMcpPat && !context.Request.Path.StartsWithSegments("/mcp"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
