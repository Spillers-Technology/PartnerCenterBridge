using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>Per-tenant Exchange Online mailbox lookups (EXO PowerShell V3, app-only cert).</summary>
[ApiController]
[Route("api/exchange/{tenantId:guid}")]
[Authorize]
public class ExchangeController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IExchangeOnlineService _exchange;

    public ExchangeController(BridgeDbContext db, IExchangeOnlineService exchange)
    {
        _db = db;
        _exchange = exchange;
    }

    [HttpGet("mailbox/{identity}")]
    public async Task<ActionResult<MailboxInfo>> Mailbox(Guid tenantId, string identity, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        try
        {
            var mbx = await _exchange.GetMailboxAsync(tenant, identity, ct);
            return mbx is null ? NotFound("Mailbox not found.") : Ok(mbx);
        }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    [HttpGet("shared")]
    public async Task<ActionResult<IReadOnlyList<MailboxInfo>>> Shared(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        try { return Ok(await _exchange.ListSharedMailboxesAsync(tenant, ct)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    /// <summary>Diagnose why a mailbox is full / not archiving (sizes, quotas, and processing blockers).</summary>
    [HttpGet("archive")]
    public async Task<ActionResult<ArchiveState>> ArchiveState(Guid tenantId, [FromQuery] string identity, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (string.IsNullOrWhiteSpace(identity)) return BadRequest("identity is required.");
        try
        {
            var state = await _exchange.GetArchiveStateAsync(tenant, identity, ct);
            return state is null ? NotFound("Mailbox not found.") : Ok(state);
        }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    /// <summary>Apply the archive fix (enable archive/auto-expand, retention policy, clear blocks, trigger MFA).</summary>
    [HttpPost("archive/remediate")]
    public async Task<ActionResult<ArchiveRemediationResult>> RemediateArchive(
        Guid tenantId, [FromQuery] string identity, [FromBody] ArchiveRemediationOptions? options, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (string.IsNullOrWhiteSpace(identity)) return BadRequest("identity is required.");
        try { return Ok(await _exchange.RemediateArchiveAsync(tenant, identity, options ?? new(), ct)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    /// <summary>Re-trigger the Managed Folder Assistant and return refreshed state (the async "nudge").</summary>
    [HttpPost("archive/nudge")]
    public async Task<ActionResult<ArchiveRemediationResult>> NudgeArchive(Guid tenantId, [FromQuery] string identity, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (string.IsNullOrWhiteSpace(identity)) return BadRequest("identity is required.");
        try { return Ok(await _exchange.NudgeArchiveAsync(tenant, identity, ct)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }
}
