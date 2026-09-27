using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>
/// Mints and validates the bridge's own JWTs for <c>Auth:Mode=Local</c>. Deliberately shaped like
/// the OIDC path (same <see cref="JwtBearerDefaults.AuthenticationScheme"/>, same
/// <see cref="ClaimTypes.Name"/> claim) so every existing <c>[Authorize]</c> controller and
/// <c>User.Identity.Name</c> read (e.g. <c>WorkflowsController</c>'s run history) works unchanged
/// regardless of which auth mode issued the token.
/// </summary>
public class LocalTokenService
{
    /// <summary>Claim carrying the local <see cref="AppUser.Id"/>, read back by <see cref="HttpContextCurrentActor"/>.</summary>
    public const string UserIdClaim = "pcb:userid";
    /// <summary>Legacy compatibility constant. New tokens do not emit it and authorization ignores it.</summary>
    public const string SystemAdminClaim = "pcb:sysadmin";
    /// <summary>Claim carrying <see cref="AppUser.SessionEpoch"/> at issue time; checked on every request.</summary>
    public const string EpochClaim = "pcb:epoch";

    /// <summary>The security epoch a principal's token was issued under; tokens without the claim count as 0.</summary>
    public static int? EpochOf(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(EpochClaim);
        if (value is null) return 0;
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : null;
    }

    public const string Issuer = "partnercenterbridge-local";
    public const string Audience = "partnercenterbridge";

    private readonly LocalAuthOptions _options;

    public LocalTokenService(IOptions<LocalAuthOptions> options) => _options = options.Value;

    public SymmetricSecurityKey SigningKey =>
        new(Encoding.UTF8.GetBytes(_options.SigningKey));

    public string IssueAccessToken(AppUser user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(UserIdClaim, user.Id.ToString()),
            new Claim(EpochClaim, user.SessionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer32)
        };

        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_options.AccessTokenLifetimeHours),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Issues a revocable PAT for headless MCP clients. Same claims shape as IssueAccessToken so
    /// every existing [Authorize]/ITenantAccessService check treats it identically to a normal
    /// login token -- the only addition is "jti", checked against McpToken.RevokedAt on validation.
    /// </summary>
    /// <param name="epoch">
    /// The security epoch of the credential that asked for the PAT (see <see cref="EpochOf"/>), not the
    /// user's current one: a PAT requested by a session that a concurrent "Protect with an account"
    /// just invalidated is born stale.
    /// </param>
    public string IssueMcpToken(AppUser user, McpToken token, int epoch)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(UserIdClaim, user.Id.ToString()),
            new Claim(EpochClaim, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer32),
            new Claim(JwtRegisteredClaimNames.Jti, token.Id.ToString())
        };

        var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: (token.ExpiresAt ?? DateTimeOffset.UtcNow.AddYears(1)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
