using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

    [Fact]
    public async Task ValidateAsync_without_jti_leaves_normal_login_token_valid_and_unaffected()
    {
        using var db = new TestDb();

        var valid = await McpTokenValidator.ValidateAsync(new ClaimsPrincipal(), db.Context);

        Assert.True(valid);
    }

    [Fact]
    public async Task ValidateAsync_with_active_token_updates_last_used_at()
    {
        using var db = new TestDb();
        var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
        db.Context.AppUsers.Add(user);
        var token = new McpToken { UserId = user.Id, Name = "laptop" };
        db.Context.McpTokens.Add(token);
        await db.Context.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Jti, token.Id.ToString())], "test"));

        var valid = await McpTokenValidator.ValidateAsync(principal, db.Context);

        Assert.True(valid);
        await using var verificationContext = db.CreateContext();
        Assert.NotNull((await verificationContext.McpTokens.FindAsync(token.Id))!.LastUsedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_with_revoked_or_missing_token_fails(bool createRevokedToken)
    {
        using var db = new TestDb();
        var tokenId = Guid.NewGuid();
        if (createRevokedToken)
        {
            var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
            db.Context.AppUsers.Add(user);
            db.Context.McpTokens.Add(new McpToken
            {
                Id = tokenId,
                UserId = user.Id,
                Name = "revoked",
                RevokedAt = DateTimeOffset.UtcNow
            });
            await db.Context.SaveChangesAsync();
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Jti, tokenId.ToString())], "test"));

        var valid = await McpTokenValidator.ValidateAsync(principal, db.Context);

        Assert.False(valid);
    }
}
