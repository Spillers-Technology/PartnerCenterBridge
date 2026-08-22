using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Reconcile;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ContractsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public ContractsController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ContractDto>> List(CancellationToken ct) =>
        (await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps).ToListAsync(ct))
        .Select(ContractDto.From).ToList();

    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create(CreateContractRequest req, CancellationToken ct)
    {
        var contract = new Contract { Name = req.Name, Notes = req.Notes };
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), ContractDto.From(contract));
    }

    /// <summary>
    /// Plan (dry-run) what would happen to bring every tenant on the contract to its desired
    /// state. Pure diff — no Graph calls — so it is safe to call freely from the UI.
    /// </summary>
    [HttpGet("{id:guid}/plan")]
    public async Task<ActionResult<IReadOnlyList<ReconcilePlanItemDto>>> Plan(Guid id, CancellationToken ct)
    {
        var contract = await _db.Contracts
            .Include(c => c.Tenants)
            .Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var templateIds = contract.DesiredApps.Select(a => a.Id).ToList();
        var tenantIds = contract.Tenants.Select(t => t.Id).ToList();
        var deployments = await _db.Deployments
            .Where(d => tenantIds.Contains(d.TenantId) && templateIds.Contains(d.AppTemplateId))
            .ToListAsync(ct);

        var plan = DesiredStateReconciler.Plan(contract.Tenants, contract.DesiredApps, deployments);
        return Ok(plan.Select(p => new ReconcilePlanItemDto(
            p.Tenant.Id, p.Tenant.DisplayName, p.Template.Id, p.Template.DisplayName, p.Action.ToString())).ToList());
    }

    /// <summary>
    /// Adds a template to the contract's desired-app list. Idempotent: adding an already-desired
    /// template is a harmless no-op success, so the frontend never has to check first.
    /// </summary>
    [HttpPost("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> AddDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        var template = await _db.AppTemplates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        if (!contract.DesiredApps.Any(a => a.Id == templateId)) contract.DesiredApps.Add(template);
        await _db.SaveChangesAsync(ct);
        return Ok(ContractDto.From(contract));
    }

    /// <summary>
    /// Removes a template from the contract's desired-app list. Idempotent: removing a template
    /// that isn't there is a harmless no-op success.
    /// </summary>
    [HttpDelete("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> RemoveDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var existing = contract.DesiredApps.FirstOrDefault(a => a.Id == templateId);
        if (existing is not null) contract.DesiredApps.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return Ok(ContractDto.From(contract));
    }
}
