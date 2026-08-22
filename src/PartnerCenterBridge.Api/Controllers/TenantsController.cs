using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
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
    private readonly ITenantAccessService _access;
    private readonly IInstanceAccessService _instanceAccess;

    public TenantsController(
        BridgeDbContext db, ITenantAccessService access, IInstanceAccessService instanceAccess)
    {
        _db = db;
        _access = access;
        _instanceAccess = instanceAccess;
    }

    /// <summary>
    /// Under <c>Auth:Mode=Local</c>, a caller only sees tenants they hold a grant on -- there is no
    /// admin bypass (see <see cref="ITenantAccessService"/> remarks); the MSP's full customer list
    /// being visible to every registered account would defeat the point of per-tenant sharing.
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<TenantDto>> List(CancellationToken ct)
    {
        var query = _db.Tenants.AsNoTracking().AsQueryable();

        var allowed = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        if (allowed is not null)
            query = query.Where(t => allowed.Contains(t.Id));

        return (await query.OrderBy(t => t.DisplayName).ToListAsync(ct)).Select(TenantDto.From).ToList();
    }

    /// <summary>
    /// Manually register a tenant the caller already has a GDAP relationship for -- the
    /// self-service path for "I need to onboard one customer and start automating against it"
    /// without waiting on anyone else. The caller becomes its Owner immediately, and can share it
    /// from there via <c>TenantAccessController</c>.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest req, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageTenantRegistry, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.TenantId) || string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest("tenantId and displayName are required.");
        if (await _db.Tenants.AnyAsync(t => t.TenantId == req.TenantId, ct))
            return Conflict("A tenant with that Entra tenant id is already registered.");

        var tenant = new Tenant
        {
            TenantId = req.TenantId.Trim(),
            DisplayName = req.DisplayName.Trim(),
            DefaultDomain = req.DefaultDomain?.Trim(),
            LastSeenAt = DateTimeOffset.UtcNow
        };
        _db.Tenants.Add(tenant);
        GrantOwnerIfLocal(tenant.Id);
        await _db.SaveChangesAsync(ct);
        return Ok(TenantDto.From(tenant));
    }

    /// <summary>
    /// Seed/refresh the tenant registry from the Partner Center customer list. Open to any
    /// authenticated caller -- a newly *discovered* tenant grants Owner to whoever ran the sync
    /// (same self-service onboarding as <see cref="Create"/>); a tenant that already exists just
    /// gets its display name/domain refreshed, ownership untouched.
    /// </summary>
    [HttpPost("sync")]
    public async Task<ActionResult<IReadOnlyList<TenantDto>>> Sync(
        [FromServices] PartnerCenterClient partnerCenter, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageTenantRegistry, ct)) return Forbid();
        IReadOnlyList<CustomerSummary> customers;
        try { customers = await partnerCenter.ListCustomersAsync(ct); }
        catch (InvalidOperationException e) { return BadRequest(e.Message); }
        foreach (var c in customers)
        {
            var existing = await _db.Tenants.FirstOrDefaultAsync(t => t.TenantId == c.TenantId, ct);
            if (existing is null)
            {
                var tenant = new Tenant
                {
                    TenantId = c.TenantId,
                    DisplayName = c.CompanyName,
                    DefaultDomain = c.Domain,
                    LastSeenAt = DateTimeOffset.UtcNow
                };
                _db.Tenants.Add(tenant);
                GrantOwnerIfLocal(tenant.Id);
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

    /// <summary>OIDC/dev-auth callers have no AppUser to own anything -- their unrestricted access already covers every tenant, so there's nothing to grant.</summary>
    private void GrantOwnerIfLocal(Guid tenantId)
    {
        if (_access.CurrentUserId is not { } userId) return;
        _db.TenantAccessGrants.Add(new TenantAccessGrant
        {
            TenantId = tenantId,
            UserId = userId,
            Role = TenantRole.Owner,
            GrantedByUserId = userId,
            GrantedAt = DateTimeOffset.UtcNow
        });
    }

    [HttpPut("{id:guid}/contract")]
    public async Task<IActionResult> AssignContract(Guid id, [FromBody] Guid? contractId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(id, TenantRole.Owner, ct)) return Forbid();

        var tenant = await _db.Tenants.FindAsync([id], ct);
        if (tenant is null) return NotFound();
        tenant.ContractId = contractId;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
