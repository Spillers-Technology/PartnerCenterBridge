using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Validates a Local identity and, when present, its MCP PAT against current database state.</summary>
public static class McpTokenValidator
{
    /// <summary>
    /// Returns false when the Local user is missing/inactive or an MCP PAT is missing, revoked,
    /// expired, or belongs to a different user. Role claims are intentionally irrelevant: current
    /// instance roles and tenant grants are resolved after authentication from the database.
    /// </summary>
    public static async Task<bool> ValidateAsync(
        ClaimsPrincipal? principal, BridgeDbContext db, CancellationToken ct = default)
    {
        if (!Guid.TryParse(principal?.FindFirstValue(LocalTokenService.UserIdClaim), out var userId))
            return false;
        if (!await db.AppUsers.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive, ct))
            return false;

        var jti = principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (jti is null) return true;
        if (!Guid.TryParse(jti, out var tokenId)) return false;

        var token = await db.McpTokens.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == tokenId && candidate.UserId == userId, ct);
        if (token is null || token.RevokedAt is not null
            || (token.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)) return false;

        var usedAt = DateTimeOffset.UtcNow;
        await db.McpTokens
            .Where(candidate => candidate.Id == tokenId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.LastUsedAt, usedAt), ct);
        return true;
    }
}
