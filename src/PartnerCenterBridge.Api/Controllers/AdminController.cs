using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Instance-level infrastructure: the SAM refresh token is a single, shared secret that backs
/// Microsoft-plane access for every tenant on the whole deployment. This is the one place
/// <see cref="ITenantAccessService.IsSystemAdmin"/> is used to gate anything -- overwriting it is
/// not a per-tenant action, so per-tenant grants don't apply here. Under <c>Auth:Mode=Oidc</c> or
/// <c>Dev</c>, <c>IsSystemAdmin</c> is unconditionally true (unchanged pre-existing behavior).
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ISamTokenStore _store;
    private readonly ITenantAccessService _access;
    private readonly BridgeDbContext _db;

    public AdminController(ISamTokenStore store, ITenantAccessService access, BridgeDbContext db)
    {
        _store = store;
        _access = access;
        _db = db;
    }

    /// <summary>Whether the Secure Application Model has been bootstrapped (a refresh token is stored).</summary>
    [HttpGet("sam/status")]
    public async Task<object> Status(CancellationToken ct) =>
        new { bootstrapped = await _store.GetRefreshTokenAsync(ct) is not null };

    /// <summary>
    /// Manually seed the SAM refresh token (e.g. one captured out-of-band). Prefer the interactive
    /// <c>bootstrap-sam</c> CLI flow; this exists for paste-in and rotation-recovery scenarios.
    /// </summary>
    [HttpPost("sam/seed")]
    public async Task<IActionResult> Seed([FromBody] SeedRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return BadRequest("refreshToken is required.");
        await _store.SaveRefreshTokenAsync(req.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>
    /// Switches a tenant between the default Queue approval mode and ClientTrust. Deliberately
    /// IsSystemAdmin-only and nothing else -- this changes how OTHER users' already-authorized
    /// operator actions get gated, not the admin's own access to the tenant's data, so it stays
    /// inside the boundary ITenantAccessService's remarks draw rather than crossing it.
    /// </summary>
    [HttpPatch("tenants/{id:guid}/mcp-mode")]
    public async Task<IActionResult> SetMcpMode(Guid id, [FromBody] SetMcpModeRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null) return NotFound();
        tenant.McpApprovalMode = req.Mode;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record SeedRequest(string RefreshToken);
    public record SetMcpModeRequest(McpApprovalMode Mode);
}
