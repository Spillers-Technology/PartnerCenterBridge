using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// WebAuthn/FIDO2 passkeys for <c>Auth:Mode=Local</c> -- the primary login method. Credentials are
/// registered as discoverable/resident keys so <c>login/options</c> needs no username: the
/// browser/authenticator presents whichever passkeys it holds for this relying party and the user
/// picks one, a single tap rather than typing an email first. Password stays the permanent
/// fallback credential (see <c>AuthController</c>) -- passkeys are additive, never the only way in.
/// </summary>
[ApiController]
[Route("api/auth/passkey")]
public class PasskeyController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IFido2 _fido2;
    private readonly ChallengeCache _challenges;
    private readonly AuthResponseFactory _responses;

    public PasskeyController(BridgeDbContext db, IFido2 fido2, ChallengeCache challenges, AuthResponseFactory responses)
    {
        _db = db;
        _fido2 = fido2;
        _challenges = challenges;
        _responses = responses;
    }

    [HttpPost("register/options")]
    [Authorize]
    public async Task<ActionResult<PasskeyRegisterOptionsResponse>> RegisterOptions(CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("Passkeys apply only to Auth:Mode=Local accounts.");
        var user = await _db.AppUsers.Include(u => u.PasskeyCredentials).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = user.PasskeyCredentials
                .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Required (not just preferred): a non-discoverable credential can't drive the
                // usernameless login flow this app relies on for "passkey primary".
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        return Ok(new PasskeyRegisterOptionsResponse(_challenges.Store(options), options));
    }

    [HttpPost("register/verify")]
    [Authorize]
    public async Task<IActionResult> RegisterVerify(PasskeyRegisterVerifyRequest req, CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("Passkeys apply only to Auth:Mode=Local accounts.");
        if (!_challenges.TryTake<CredentialCreateOptions>(req.ChallengeKey, out var options) || options is null)
            return BadRequest("Registration expired. Request new options and try again.");

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = req.AttestationResponse,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = async (p, cancel) =>
                !await _db.PasskeyCredentials.AnyAsync(c => c.CredentialId == p.CredentialId, cancel)
        }, ct);

        _db.PasskeyCredentials.Add(new PasskeyCredential
        {
            UserId = userId,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            UserHandle = result.User.Id,
            SignatureCounter = result.SignCount,
            AaGuid = result.AaGuid,
            Nickname = req.Nickname
        });
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.PasskeyRegistered,
            ActorUserId = userId,
            ActorName = User.Identity?.Name ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Usernameless: an empty allow-list lets the authenticator present every resident credential it holds for this site.</summary>
    [HttpPost("login/options")]
    [AllowAnonymous]
    public ActionResult<PasskeyLoginOptionsResponse> LoginOptions()
    {
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Preferred
        });
        return Ok(new PasskeyLoginOptionsResponse(_challenges.Store(options), options));
    }

    [HttpPost("login/verify")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> LoginVerify(PasskeyLoginVerifyRequest req, CancellationToken ct)
    {
        if (!_challenges.TryTake<AssertionOptions>(req.ChallengeKey, out var options) || options is null)
            return Unauthorized("Challenge expired or invalid. Try again.");

        var credential = await _db.PasskeyCredentials.Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == req.AssertionResponse.RawId, ct);
        if (credential?.User is null) return Unauthorized("Unrecognized passkey.");
        if (!credential.User.IsActive) return Unauthorized("Unrecognized passkey.");

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = req.AssertionResponse,
            OriginalOptions = options,
            StoredPublicKey = credential.PublicKey,
            StoredSignatureCounter = credential.SignatureCounter,
            IsUserHandleOwnerOfCredentialIdCallback = (p, _) =>
                Task.FromResult(credential.UserHandle.SequenceEqual(p.UserHandle))
        }, ct);

        credential.SignatureCounter = result.SignCount;
        credential.LastUsedAt = DateTimeOffset.UtcNow;
        var user = credential.User;
        user.LastLoginAt = DateTimeOffset.UtcNow;

        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.PasskeyLoginSucceeded,
            ActorUserId = user.Id,
            ActorName = user.DisplayName
        });
        await _db.SaveChangesAsync(ct);

        return Ok(await _responses.BuildAsync(user, ct));
    }

    [HttpGet]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<PasskeyDto>>> List(CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("Passkeys apply only to Auth:Mode=Local accounts.");
        var creds = await _db.PasskeyCredentials.AsNoTracking()
            .Where(c => c.UserId == userId).ToListAsync(ct);
        return Ok(creds.Select(c => new PasskeyDto(c.Id, c.Nickname, c.CreatedAt, c.LastUsedAt)).ToList());
    }

    [HttpDelete("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("Passkeys apply only to Auth:Mode=Local accounts.");
        var cred = await _db.PasskeyCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (cred is null) return NotFound();

        _db.PasskeyCredentials.Remove(cred);
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.PasskeyRemoved,
            ActorUserId = userId,
            ActorName = User.Identity?.Name ?? ""
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
