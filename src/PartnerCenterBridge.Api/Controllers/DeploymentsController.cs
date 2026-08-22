using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DeploymentsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public DeploymentsController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeploymentDto>>> List(CancellationToken ct)
    {
        var query = _db.Deployments.AsNoTracking().AsQueryable();

        var allowed = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        if (allowed is not null)
            query = query.Where(d => allowed.Contains(d.TenantId));

        return Ok((await query.OrderByDescending(d => d.CreatedAt).ToListAsync(ct)).Select(DeploymentDto.From).ToList());
    }

    /// <summary>
    /// The deploy wizard endpoint: push a template to the selected tenants (creating or updating
    /// the Intune app), returning the per-tenant outcome. Requires Operator access to every
    /// selected tenant -- a partial grant (Operator on some, nothing on others) is rejected rather
    /// than silently deploying to the subset the caller can reach.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<DeploymentDto>>> Deploy(
        DeployRequest req, [FromServices] DeploymentOrchestrator orchestrator, CancellationToken ct)
    {
        if (req.TenantIds.Count == 0) return BadRequest("Select at least one tenant.");

        foreach (var tenantId in req.TenantIds)
            if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct))
                return Forbid();

        try
        {
            var results = await orchestrator.DeployAsync(req.TemplateId, req.TenantIds, ct);
            return Ok(results.Select(DeploymentDto.From).ToList());
        }
        catch (KeyNotFoundException e) { return NotFound(e.Message); }
        catch (InvalidOperationException e) { return BadRequest(e.Message); }
    }
}
