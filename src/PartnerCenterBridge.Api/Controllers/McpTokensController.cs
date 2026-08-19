using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Self-service PAT management for headless/scripted MCP clients (a browser-based OAuth flow is a
/// separate, later addition for Auth:Mode=Local -- see the MCP server design spec). Local-account
/// only, same as PasskeyController/TotpController.
/// </summary>
[ApiController]
[Route("api/mcp-tokens")]
[Authorize]
public class McpTokensController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly LocalTokenService _tokens;
    private readonly ITenantAccessService _access;

    public McpTokensController(BridgeDbContext db, LocalTokenService tokens, ITenantAccessService access)
    {
        _db = db;
        _tokens = tokens;
        _access = access;
    }

    public record McpTokenDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt);
    public record CreateMcpTokenRequest(string Name);
    public record CreatedMcpTokenDto(Guid Id, string Name, string Jwt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<McpTokenDto>>> List(CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type == JwtRegisteredClaimNames.Jti)) return ForbidMcpTokenManagement();
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        return Ok(await _db.McpTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new McpTokenDto(t.Id, t.Name, t.CreatedAt, t.ExpiresAt, t.LastUsedAt))
            .ToListAsync(ct));
    }

    /// <summary>Returns the raw JWT once -- like TOTP recovery codes, it is never retrievable again after this response.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedMcpTokenDto>> Create(CreateMcpTokenRequest req, CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type == JwtRegisteredClaimNames.Jti)) return ForbidMcpTokenManagement();
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");

        var user = await _db.AppUsers.FindAsync([userId], ct);
        if (user is null) return NotFound();

        var token = new McpToken { UserId = userId, Name = req.Name.Trim() };
        _db.McpTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        var jwt = _tokens.IssueMcpToken(user, token);
        return Ok(new CreatedMcpTokenDto(token.Id, token.Name, jwt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (User.HasClaim(c => c.Type == JwtRegisteredClaimNames.Jti)) return ForbidMcpTokenManagement();
        if (_access.CurrentUserId is not { } userId) return BadRequest("Not a local account.");
        var token = await _db.McpTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (token is null) return NotFound();
        token.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private ForbidResult ForbidMcpTokenManagement()
    {
        Response.Headers["X-PartnerCenterBridge-Reason"] =
            "MCP token management requires an interactive login token.";
        return Forbid();
    }
}
