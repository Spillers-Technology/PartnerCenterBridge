using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Self-registration and password login for <c>Auth:Mode=Local</c> deployments. Registration is
/// deliberately open (no invite code, no admin approval) -- the safety story is that a fresh
/// account starts with zero <see cref="TenantAccessGrant"/>s and can't touch a tenant until an
/// owner shares it in, not a signup gate. See <see cref="TenantAccessController"/> for sharing,
/// <see cref="TotpController"/> for the second-factor challenge this returns, and
/// <see cref="PasskeyController"/> for the passwordless primary login path.
/// Every outcome here (including failed logins) is recorded to <see cref="BridgeDbContext.AuditEvents"/>.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly LocalAuthOptions _options;
    private readonly AuthModeInfo _mode;
    private readonly ChallengeCache _challenges;
    private readonly AuthResponseFactory _responses;
    private static readonly PasswordHasher<AppUser> Hasher = new();

    public AuthController(
        BridgeDbContext db, IOptions<LocalAuthOptions> options, AuthModeInfo mode,
        ChallengeCache challenges, AuthResponseFactory responses)
    {
        _db = db;
        _options = options.Value;
        _mode = mode;
        _challenges = challenges;
        _responses = responses;
    }

    [HttpGet("mode")]
    [AllowAnonymous]
    public object Mode() => new { mode = _mode.Mode };

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        if (!_mode.IsLocal) return BadRequest("Self-registration is not enabled on this deployment (Auth:Mode is not Local).");

        var email = req.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest("A valid email is required.");
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest("Display name is required.");
        if (req.Password.Length < _options.MinPasswordLength)
            return BadRequest($"Password must be at least {_options.MinPasswordLength} characters.");
        if (await _db.AppUsers.AnyAsync(u => u.Email == email, ct))
            return Conflict("An account with that email already exists.");

        // First account on a fresh database becomes the bootstrap admin -- otherwise nobody could
        // ever grant tenant access to anyone, including themselves. This is unrelated to tenant
        // power (see ITenantAccessService remarks): it only gates the SAM admin endpoints.
        var isFirstUser = !await _db.AppUsers.AnyAsync(ct);

        var user = new AppUser
        {
            Email = email,
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = "",
            IsSystemAdmin = isFirstUser
        };
        user.PasswordHash = Hasher.HashPassword(user, req.Password);

        _db.AppUsers.Add(user);
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.UserRegistered,
            ActorUserId = user.Id,
            ActorName = user.DisplayName,
            EntityType = nameof(AppUser),
            EntityId = user.Id.ToString(),
            Detail = isFirstUser ? "\"first account; granted system admin\"" : null
        });
        await _db.SaveChangesAsync(ct);

        return Ok(await _responses.BuildAsync(user, ct));
    }

    /// <summary>
    /// Password login. Returns <see cref="AuthResponse"/> directly, or -- if the account has TOTP
    /// enabled -- a <see cref="MfaChallengeResponse"/> the caller must resolve via
    /// <c>POST /api/auth/totp/challenge</c> before getting a token.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest req, CancellationToken ct)
    {
        if (!_mode.IsLocal) return BadRequest("Local login is not enabled on this deployment (Auth:Mode is not Local).");

        var email = req.Email.Trim().ToLowerInvariant();
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Same generic failure for "no such user" and "wrong password" -- don't let login responses
        // confirm which emails have accounts.
        if (user is null)
        {
            await RecordLoginFailureAsync(null, email, ct);
            return Unauthorized("Invalid email or password.");
        }

        var now = DateTimeOffset.UtcNow;
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
            return StatusCode(423, $"Account locked until {lockedUntil:u} after repeated failed logins.");

        if (!user.IsActive)
            return Unauthorized("Account is disabled.");

        var verify = Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _options.MaxFailedLogins)
                user.LockedUntil = now.AddMinutes(_options.LockoutMinutes);
            await RecordLoginFailureAsync(user.Id, email, ct);
            return Unauthorized("Invalid email or password.");
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;

        if (user.TotpEnabled)
        {
            // Password proven correct, but not a "login" yet -- no token, no LastLoginAt, and the
            // audit trail records this distinctly from a completed login (see TotpController.Challenge).
            await _db.SaveChangesAsync(ct);
            var ticket = _challenges.Store(new MfaTicketState { UserId = user.Id });
            return Ok(new MfaChallengeResponse(ticket));
        }

        user.LastLoginAt = now;
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.LoginSucceeded,
            ActorUserId = user.Id,
            ActorName = user.DisplayName
        });
        await _db.SaveChangesAsync(ct);

        return Ok(await _responses.BuildAsync(user, ct));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        // Stateless JWTs: nothing to revoke server-side (v1). Recorded anyway so "who was logged
        // in when" is reconstructable from the audit trail alone.
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.LogoutSucceeded,
            ActorName = User.Identity?.Name ?? "anonymous"
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        if (this.LocalUserId() is not { } userId) return BadRequest("Not a local account.");
        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();
        return Ok((await _responses.BuildAsync(user, ct)).User);
    }

    private async Task RecordLoginFailureAsync(Guid? userId, string attemptedEmail, CancellationToken ct)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            EventType = AuditEventType.LoginFailed,
            ActorUserId = userId,
            ActorName = attemptedEmail,
            Success = false
        });
        // Always persisted, even though the caller is about to return 401 -- a failed-login audit
        // trail is the whole point.
        await _db.SaveChangesAsync(ct);
    }
}
