using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.PartnerCenter;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly BridgeDbContext _db;

    public TenantsController(BridgeDbContext db) => _db = db;

    [HttpGet]
    public async Task<IReadOnlyList<TenantDto>> List(CancellationToken ct) =>
        (await _db.Tenants.OrderBy(t => t.DisplayName).ToListAsync(ct)).Select(TenantDto.From).ToList();

    /// <summary>Seed/refresh the tenant registry from the Partner Center customer list.</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<IReadOnlyList<TenantDto>>> Sync(
        [FromServices] PartnerCenterClient partnerCenter, CancellationToken ct)
    {
        var customers = await partnerCenter.ListCustomersAsync(ct);
        foreach (var c in customers)
        {
            var existing = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == c.TenantId, ct);
            if (existing is null)
            {
                _db.Tenants.Add(new Tenant
                {
                    TenantId = c.TenantId,
                    DisplayName = c.CompanyName,
                    DefaultDomain = c.Domain,
                    LastSeenAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                existing.DisplayName = c.CompanyName;
                existing.DefaultDomain = c.Domain;
                existing.LastSeenAt = DateTimeOffset.UtcNow;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(await List(ct));
    }

    [HttpPut("{id:guid}/contract")]
    public async Task<IActionResult> AssignContract(Guid id, [FromBody] Guid? contractId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null) return NotFound();
        tenant.ContractId = contractId;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
