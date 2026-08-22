using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class PasskeyControllerTests
{
    [Fact]
    public async Task LoginVerify_rejects_a_credential_belonging_to_a_deactivated_account_before_verifying_the_assertion()
    {
        using var db = new TestDb();
        var user = new AppUser
        {
            Email = "user@example.com",
            DisplayName = "User",
            PasswordHash = "hash",
            IsActive = false
        };
        var credentialId = Guid.NewGuid().ToByteArray();
        db.Context.AppUsers.Add(user);
        db.Context.PasskeyCredentials.Add(new PasskeyCredential
        {
            UserId = user.Id,
            CredentialId = credentialId,
            PublicKey = [1, 2, 3],
            UserHandle = user.Id.ToByteArray()
        });
        await db.Context.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var challenges = new ChallengeCache(cache);
        var challengeKey = challenges.Store(new AssertionOptions());
        var fido2 = new NeverCalledFido2();
        var tokens = new LocalTokenService(Options.Create(new LocalAuthOptions
        {
            SigningKey = "passkey-controller-test-signing-key-32",
            MinPasswordLength = 12
        }));
        var controller = new PasskeyController(db.Context, fido2, challenges, new AuthResponseFactory(db.Context, tokens));

        var req = new PasskeyLoginVerifyRequest(challengeKey,
            new AuthenticatorAssertionRawResponse { Id = Convert.ToBase64String(credentialId), RawId = credentialId });
        var result = await controller.LoginVerify(req, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.False(fido2.MakeAssertionCalled);
    }

    private sealed class NeverCalledFido2 : IFido2
    {
        public bool MakeAssertionCalled { get; private set; }
        public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams p) => throw new NotSupportedException();
        public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams p) => throw new NotSupportedException();
        public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(MakeNewCredentialParams p, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<VerifyAssertionResult> MakeAssertionAsync(MakeAssertionParams p, CancellationToken ct = default)
        {
            MakeAssertionCalled = true;
            throw new NotSupportedException();
        }
    }
}
