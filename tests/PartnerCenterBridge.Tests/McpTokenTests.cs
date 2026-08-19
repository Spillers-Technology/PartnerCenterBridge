using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class McpTokenTests
{
    private static LocalTokenService NewService() =>
        new(Options.Create(new LocalAuthOptions { SigningKey = "test-signing-key-at-least-32-bytes-long!!", AccessTokenLifetimeHours = 8 }));

    [Fact]
    public void IssueMcpToken_embeds_the_token_id_as_jti()
    {
        var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
        var token = new McpToken { UserId = user.Id, Name = "laptop", User = user };

        var jwt = NewService().IssueMcpToken(user, token);
        var parsed = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt);

        Assert.Equal(token.Id.ToString(), parsed.Claims.First(c => c.Type == "jti").Value);
        Assert.Equal(user.Id.ToString(), parsed.Claims.First(c => c.Type == LocalTokenService.UserIdClaim).Value);
    }
}
