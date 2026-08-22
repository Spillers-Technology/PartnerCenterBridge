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
        Assert.DoesNotContain(parsed.Claims, claim => claim.Type == LocalTokenService.SystemAdminClaim);
    }

    [Fact]
    public async Task ValidateAsync_rejects_inactive_user_and_token_owned_by_someone_else()
    {
        using var db = new TestDb();
        var inactive = new AppUser
        {
            Email = "inactive@example.com", DisplayName = "Inactive", PasswordHash = "x", IsActive = false
        };
        var owner = new AppUser { Email = "owner@example.com", DisplayName = "Owner", PasswordHash = "x" };
        var token = new McpToken { UserId = owner.Id, Name = "owner token" };
        db.Context.AddRange(inactive, owner, token);
        await db.Context.SaveChangesAsync();

        var inactivePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(LocalTokenService.UserIdClaim, inactive.Id.ToString())], "test"));
        Assert.False(await McpTokenValidator.ValidateAsync(inactivePrincipal, db.Context));

        inactive.IsActive = true;
        await db.Context.SaveChangesAsync();
        var wrongOwnerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(LocalTokenService.UserIdClaim, inactive.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, token.Id.ToString())
            ], "test"));
        Assert.False(await McpTokenValidator.ValidateAsync(wrongOwnerPrincipal, db.Context));
    }

    [Fact]
    public async Task ValidateAsync_without_jti_leaves_normal_login_token_valid_and_unaffected()
    {
        using var db = new TestDb();
        var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
        db.Context.AppUsers.Add(user);
        await db.Context.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(LocalTokenService.UserIdClaim, user.Id.ToString())], "test"));

        var valid = await McpTokenValidator.ValidateAsync(principal, db.Context);

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
            [
                new Claim(LocalTokenService.UserIdClaim, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, token.Id.ToString())
            ], "test"));

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
        var user = new AppUser { Email = "a@b.com", DisplayName = "A B", PasswordHash = "x" };
        db.Context.AppUsers.Add(user);
        if (createRevokedToken)
        {
            db.Context.McpTokens.Add(new McpToken
            {
                Id = tokenId,
                UserId = user.Id,
                Name = "revoked",
                RevokedAt = DateTimeOffset.UtcNow
            });
        }
        await db.Context.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(LocalTokenService.UserIdClaim, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, tokenId.ToString())
            ], "test"));

        var valid = await McpTokenValidator.ValidateAsync(principal, db.Context);

        Assert.False(valid);
    }
}
