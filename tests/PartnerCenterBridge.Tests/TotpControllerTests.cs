using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class TotpControllerTests
{
    [Fact]
    public async Task Challenge_rejects_a_ticket_for_an_account_deactivated_after_password_verification()
    {
        using var db = new TestDb();
        var user = new AppUser
        {
            Email = "user@example.com",
            DisplayName = "User",
            PasswordHash = "hash",
            IsActive = false,
            TotpEnabled = true,
            TotpSecretProtected = "irrelevant-once-inactive"
        };
        db.Context.AppUsers.Add(user);
        await db.Context.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var challenges = new ChallengeCache(cache);
        var mfaTicket = challenges.Store(new MfaTicketState { UserId = user.Id });

        var totp = new TotpService(DataProtectionProvider.Create("PartnerCenterBridge.Tests"));
        var tokens = new LocalTokenService(Microsoft.Extensions.Options.Options.Create(new LocalAuthOptions
        {
            SigningKey = "totp-controller-test-signing-key-32b",
            MinPasswordLength = 12
        }));
        var controller = new TotpController(db.Context, totp, challenges, new AuthResponseFactory(db.Context, tokens));

        var result = await controller.Challenge(new TotpChallengeRequest(mfaTicket, "000000"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
