using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Auth;

/// <summary>Validates a presented MCP PAT against its persisted revocation state.</summary>
public static class McpTokenValidator
{
    /// <summary>
    /// Returns false when a principal presents an MCP PAT that is missing or revoked. Normal
    /// login tokens have no jti and are unaffected. The usage heartbeat is a set-based update so
    /// it does not create an audit event for every authenticated request.
    /// </summary>
    public static async Task<bool> ValidateAsync(
        ClaimsPrincipal? principal, BridgeDbContext db, CancellationToken ct = default)
    {
        var jti = principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (jti is null) return true;
        if (!Guid.TryParse(jti, out var tokenId)) return false;

        var token = await db.McpTokens.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == tokenId, ct);
        if (token is null || token.RevokedAt is not null) return false;

        var usedAt = DateTimeOffset.UtcNow;
        await db.McpTokens
            .Where(candidate => candidate.Id == tokenId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.LastUsedAt, usedAt), ct);
        return true;
    }
}
