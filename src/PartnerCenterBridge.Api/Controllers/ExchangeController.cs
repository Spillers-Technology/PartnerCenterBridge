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
}
