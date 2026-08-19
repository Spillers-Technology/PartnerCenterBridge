using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PartnerCenterBridge.Api.Auth;

namespace PartnerCenterBridge.Tests;

public class McpPatEndpointRestrictionMiddlewareTests
{
    [Theory]
    [InlineData("/api/workflows/mfa-reset/remediate")]
    [InlineData("/api/auth/passkey/register/options")]
    public async Task Valid_authenticated_pat_is_forbidden_from_rest_mutation_endpoints(string path)
    {
        var nextCalled = false;
        var middleware = new McpPatEndpointRestrictionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateAuthenticatedContext(path,
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(LocalTokenService.UserIdClaim, Guid.NewGuid().ToString()),
            new Claim(LocalTokenService.SystemAdminClaim, "true"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Authenticated_pat_is_allowed_to_reach_mcp_transport()
    {
        var nextCalled = false;
        var middleware = new McpPatEndpointRestrictionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateAuthenticatedContext("/mcp/messages",
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Normal_login_token_without_jti_is_unaffected()
    {
        var nextCalled = false;
        var middleware = new McpPatEndpointRestrictionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateAuthenticatedContext("/api/workflows/mfa-reset/remediate",
            new Claim(LocalTokenService.UserIdClaim, Guid.NewGuid().ToString()),
            new Claim(LocalTokenService.SystemAdminClaim, "true"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateAuthenticatedContext(string path, params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return context;
    }
}
