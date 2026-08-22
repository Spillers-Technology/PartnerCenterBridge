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
/// Instance permissions gate these shared settings. Tenant grants never apply here, and instance
/// permissions never grant access back into tenant-scoped resources.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly ISamTokenStore _store;
    private readonly IInstanceAccessService _access;
    private readonly BridgeDbContext _db;

    public AdminController(ISamTokenStore store, IInstanceAccessService access, BridgeDbContext db)
    {
        _store = store;
        _access = access;
        _db = db;
    }

    /// <summary>Whether the Secure Application Model has been bootstrapped (a refresh token is stored).</summary>
    [HttpGet("sam/status")]
    public async Task<ActionResult<object>> Status(CancellationToken ct)
    {
        if (!await _access.HasPermissionAsync(InstancePermission.ManageSam, ct)) return Forbid();
        return Ok(new { bootstrapped = await _store.GetRefreshTokenAsync(ct) is not null });
    }

    /// <summary>
    /// Manually seed the SAM refresh token (e.g. one captured out-of-band). Prefer the interactive
    /// <c>bootstrap-sam</c> CLI flow; this exists for paste-in and rotation-recovery scenarios.
    /// </summary>
    [HttpPost("sam/seed")]
    public async Task<IActionResult> Seed([FromBody] SeedRequest req, CancellationToken ct)
    {
        if (!await _access.HasPermissionAsync(InstancePermission.ManageSam, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.RefreshToken)) return BadRequest("refreshToken is required.");
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _store.SaveRefreshTokenAsync(req.RefreshToken, ct);
        _db.AuditEvents.Add(new Core.Entities.AuditEvent
        {
            EventType = AuditEventType.SamCredentialRotated,
            ActorUserId = _access.CurrentUserId,
            ActorName = ControllerContext.HttpContext?.User.Identity?.Name ?? "",
            EntityType = "SecureApplicationModelCredential",
            EntityId = "sam-refresh-token"
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Switches a tenant between the default Queue approval mode and ClientTrust. Deliberately
    /// Instance-policy permission only -- this changes how OTHER users' already-authorized
    /// operator actions get gated, not the admin's own access to the tenant's data, so it stays
    /// inside the boundary ITenantAccessService's remarks draw rather than crossing it.
    /// </summary>
    [HttpPatch("tenants/{id:guid}/mcp-mode")]
    public async Task<IActionResult> SetMcpMode(Guid id, [FromBody] SetMcpModeRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(McpApprovalMode), req.Mode)) return BadRequest("Invalid mode.");
        if (!await _access.HasPermissionAsync(InstancePermission.ManageMcpPolicy, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null) return NotFound();
        var before = tenant.McpApprovalMode;
        tenant.McpApprovalMode = req.Mode;
        _db.AuditEvents.Add(new Core.Entities.AuditEvent
        {
            EventType = AuditEventType.McpApprovalModeChanged,
            ActorUserId = _access.CurrentUserId,
            ActorName = ControllerContext.HttpContext?.User.Identity?.Name ?? "",
            TenantId = id,
            EntityType = nameof(Core.Entities.Tenant),
            EntityId = id.ToString(),
            Detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                before = before.ToString(),
                after = req.Mode.ToString()
            })
        });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public record SeedRequest(string RefreshToken);
    public record SetMcpModeRequest(McpApprovalMode Mode);
}
