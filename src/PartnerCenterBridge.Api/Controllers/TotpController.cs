using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// TOTP (RFC 6238) second factor for <c>Auth:Mode=Local</c> password logins. Enrollment is a
/// two-step confirm (generate, then prove a live code) so a typo'd authenticator app can't lock
/// an account out of itself. A successful passkey login never reaches this controller -- WebAuthn
/// possession + verification is already MFA-equivalent, so it isn't asked to clear this too.
/// </summary>
[ApiController]
[Route("api/auth/totp")]
public class TotpController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly TotpService _totp;
    private readonly ChallengeCache _challenges;
    private readonly AuthResponseFactory _responses;
    private static readonly PasswordHasher<AppUser> Hasher = new();

    public TotpController(BridgeDbContext db, TotpService totp, ChallengeCache challenges, AuthResponseFactory responses)
    {
        _db = db;
        _totp = totp;
        _challenges = challenges;
        _responses = responses;
    }

    [HttpPost("enroll")]
    [Authorize]
    public async Task<ActionResult<TotpEnrollResponse>> Enroll(CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("TOTP applies only to Auth:Mode=Local accounts.");
        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();
        if (user.TotpEnabled) return Conflict("TOTP is already enabled. Disable it before re-enrolling.");

        var (secret, uri) = _totp.GenerateEnrollment(user.Email);
        // The secret is held server-side only in the short-lived challenge cache until proven --
        // it never touches the AppUser row (and isn't "enabled") until VerifyEnroll succeeds.
        var pendingKey = _challenges.Store(secret);
        return Ok(new TotpEnrollResponse(pendingKey, secret, uri));
    }

    [HttpPost("verify-enroll")]
    [Authorize]
    public async Task<ActionResult<TotpVerifyEnrollResponse>> VerifyEnroll(TotpVerifyEnrollRequest req, CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("TOTP applies only to Auth:Mode=Local accounts.");
        if (!_challenges.TryTake<string>(req.PendingKey, out var secret) || secret is null)
            return BadRequest("Enrollment expired. Request a new secret and try again.");
        if (!_totp.VerifyCode(secret, req.Code)) return BadRequest("Incorrect code.");

        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();

        user.TotpSecretProtected = _totp.Protect(secret);
        user.TotpEnabled = true;
        var (plaintextCodes, hashes) = _totp.GenerateRecoveryCodes(user);
        user.TotpRecoveryCodeHashes = hashes;

        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.TotpEnabled,
            ActorUserId = user.Id,
            ActorName = user.DisplayName
        });
        await _db.SaveChangesAsync(ct);

        // Recovery codes are shown exactly once here -- only their hashes are ever persisted.
        return Ok(new TotpVerifyEnrollResponse(plaintextCodes));
    }

    [HttpPost("disable")]
    [Authorize]
    public async Task<IActionResult> Disable(TotpDisableRequest req, CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("TOTP applies only to Auth:Mode=Local accounts.");
        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();

        // Re-confirm the password rather than trusting the bearer token alone -- disabling 2FA is
        // exactly the kind of action a stolen-but-still-valid session token shouldn't be able to do.
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
            return Unauthorized("Incorrect password.");

        user.TotpEnabled = false;
        user.TotpSecretProtected = null;
        user.TotpRecoveryCodeHashes = new();

        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.TotpDisabled,
            ActorUserId = user.Id,
            ActorName = user.DisplayName
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Second step of a password login when the account has TOTP enabled. Accepts either a live
    /// 6-digit code or an unused recovery code. A wrong code does not burn the ticket -- it's
    /// retryable up to <see cref="MfaTicketState.MaxAttempts"/> within the ticket's TTL, so a
    /// typo doesn't force the user back through their password.
    /// </summary>
    [HttpPost("challenge")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Challenge(TotpChallengeRequest req, CancellationToken ct)
    {
        if (!_challenges.TryPeek<MfaTicketState>(req.MfaTicket, out var state) || state is null)
            return Unauthorized("Challenge expired or invalid. Log in again.");

        var user = await _db.AppUsers.FindAsync([state.UserId], ct);
        if (user is null || !user.IsActive || !user.TotpEnabled || user.TotpSecretProtected is null)
        {
            _challenges.Remove(req.MfaTicket);
            return Unauthorized();
        }

        var ok = _totp.VerifyCode(_totp.Unprotect(user.TotpSecretProtected), req.Code);
        var usedRecoveryCode = false;
        if (!ok && _totp.TryConsumeRecoveryCode(user, req.Code, out var remaining))
        {
            ok = true;
            usedRecoveryCode = true;
            user.TotpRecoveryCodeHashes = remaining;
        }

        if (!ok)
        {
            state.Attempts++;
            if (state.Attempts >= MfaTicketState.MaxAttempts) _challenges.Remove(req.MfaTicket);

            _db.AuditEvents.Add(new AuditEvent
            {
                EventType = AuditEventType.TotpChallengeFailed,
                ActorUserId = user.Id,
                ActorName = user.DisplayName,
                Success = false,
                Detail = $"{{\"attempt\":{state.Attempts}}}"
            });
            await _db.SaveChangesAsync(ct);
            return Unauthorized(state.Attempts >= MfaTicketState.MaxAttempts
                ? "Too many incorrect attempts. Log in again."
                : "Invalid code.");
        }

        _challenges.Remove(req.MfaTicket); // success: one-shot from here, prevents replaying the same ticket
        user.LastLoginAt = DateTimeOffset.UtcNow;
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = usedRecoveryCode ? AuditEventType.RecoveryCodeUsed : AuditEventType.LoginSucceeded,
            ActorUserId = user.Id,
            ActorName = user.DisplayName
        });
        await _db.SaveChangesAsync(ct);

        return Ok(await _responses.BuildAsync(user, ct));
    }
}
