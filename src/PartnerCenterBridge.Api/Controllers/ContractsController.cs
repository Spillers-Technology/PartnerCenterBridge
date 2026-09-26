using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core;
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
    private readonly ITenantAccessService _tenantAccess;
    private readonly IInstanceAccessService _instanceAccess;

    public ContractsController(
        BridgeDbContext db, ITenantAccessService tenantAccess, IInstanceAccessService instanceAccess)
    {
        _db = db;
        _tenantAccess = tenantAccess;
        _instanceAccess = instanceAccess;
    }

    [HttpGet]
    public async Task<IReadOnlyList<ContractDto>> List(CancellationToken ct)
    {
        var allowed = await _tenantAccess.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        var contracts = allowed is null
            ? await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps).ToListAsync(ct)
            : await _db.Contracts
                .Include(c => c.Tenants.Where(tenant => allowed.Contains(tenant.Id)))
                .Include(c => c.DesiredApps)
                .ToListAsync(ct);
        return contracts.Select(ContractDto.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create(CreateContractRequest req, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageCatalog, ct)) return Forbid();
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

        var allowed = await _tenantAccess.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        if (allowed is not null)
            contract.Tenants = contract.Tenants.Where(tenant => allowed.Contains(tenant.Id)).ToList();

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
    /// The contract's offboarding policy. Returns the built-in defaults (today's offboarding
    /// behavior) when the contract has none configured. This is tenant data (it can name a
    /// forwarding address), so it needs a Viewer grant on at least one tenant served under the
    /// contract whatever the caller's instance role: the instance and tenant planes never stand in
    /// for each other. A catalog manager without such a grant can still replace the policy (PUT);
    /// the PUT response returns what was saved.
    /// </summary>
    [HttpGet("{id:guid}/offboarding-policy")]
    public async Task<ActionResult<OffboardingPolicy>> GetOffboardingPolicy(Guid id, CancellationToken ct)
    {
        var allowed = await _tenantAccess.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        if (allowed is not null && !await _db.Tenants.AsNoTracking()
                .AnyAsync(tenant => tenant.ContractId == id && allowed.Contains(tenant.Id), ct))
            return Forbid();
        var contract = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        return Ok(contract.OffboardingPolicy ?? new OffboardingPolicy());
    }

    /// <summary>Replace the contract's offboarding policy (validated). Catalog managers only.</summary>
    [HttpPut("{id:guid}/offboarding-policy")]
    public async Task<ActionResult<OffboardingPolicy>> PutOffboardingPolicy(Guid id, OffboardingPolicy policy, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageCatalog, ct)) return Forbid();
        var errors = policy.Validate();
        if (errors.Count > 0) return BadRequest(string.Join(" ", errors));

        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(policy.ForwardTo)) policy.ForwardTo = policy.ForwardTo.Trim();
        else policy.ForwardTo = null;

        if (contract.OffboardingPolicy is null) contract.OffboardingPolicy = policy;
        else
        {
            // Update in place so EF sees a modification of the owned JSON rather than a replace.
            var p = contract.OffboardingPolicy;
            p.BlockSignIn = policy.BlockSignIn;
            p.RevokeSessions = policy.RevokeSessions;
            p.GroupCleanup = policy.GroupCleanup;
            p.ConvertMailboxToShared = policy.ConvertMailboxToShared;
            p.RemoveLicenses = policy.RemoveLicenses;
            p.HideFromGal = policy.HideFromGal;
            p.ForwardTo = policy.ForwardTo;
            p.ManagerAccess = policy.ManagerAccess;
            p.WipeDevices = policy.WipeDevices;
            p.FollowUpDays = policy.FollowUpDays;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(contract.OffboardingPolicy);
    }

    /// <summary>
    /// Adds a template to the contract's desired-app list. Idempotent: adding an already-desired
    /// template is a harmless no-op success, so the frontend never has to check first.
    /// </summary>
    [HttpPost("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> AddDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageCatalog, ct)) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        var template = await _db.AppTemplates.FindAsync([templateId], ct);
        if (template is null) return NotFound();

        var alreadyDesired = contract.DesiredApps.Any(a => a.Id == templateId);
        if (!alreadyDesired && template.Content is null)
            return Conflict("Attach a package before adding this template to desired state.");
        if (!alreadyDesired) contract.DesiredApps.Add(template);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!alreadyDesired)
        {
            // Another caller may have inserted the same composite-key membership after our read.
            // Treat that realized desired state as the idempotent success the endpoint promises.
            _db.ChangeTracker.Clear();
            var persisted = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (persisted?.DesiredApps.Any(a => a.Id == templateId) == true)
                return Ok(ContractDto.From(persisted));
            throw;
        }
        return Ok(ContractDto.From(contract));
    }

    /// <summary>
    /// Removes a template from the contract's desired-app list. Idempotent: removing a template
    /// that isn't there is a harmless no-op success.
    /// </summary>
    [HttpDelete("{id:guid}/desired-apps/{templateId:guid}")]
    public async Task<ActionResult<ContractDto>> RemoveDesiredApp(Guid id, Guid templateId, CancellationToken ct)
    {
        if (!await _instanceAccess.HasPermissionAsync(InstancePermission.ManageCatalog, ct)) return Forbid();

        var contract = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var existing = contract.DesiredApps.FirstOrDefault(a => a.Id == templateId);
        if (existing is not null) contract.DesiredApps.Remove(existing);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (existing is not null)
        {
            // A concurrent delete that already realized the requested absence is also success.
            _db.ChangeTracker.Clear();
            var persisted = await _db.Contracts.Include(c => c.Tenants).Include(c => c.DesiredApps)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (persisted is not null && persisted.DesiredApps.All(a => a.Id != templateId))
                return Ok(ContractDto.From(persisted));
            throw;
        }
        return Ok(ContractDto.From(contract));
    }
}
