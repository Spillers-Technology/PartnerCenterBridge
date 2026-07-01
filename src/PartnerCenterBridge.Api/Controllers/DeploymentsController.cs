using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DeploymentsController : ControllerBase
{
    private readonly BridgeDbContext _db;

    public DeploymentsController(BridgeDbContext db) => _db = db;

    [HttpGet]
    public async Task<IReadOnlyList<DeploymentDto>> List(CancellationToken ct) =>
        (await _db.Deployments.OrderByDescending(d => d.CreatedAt).ToListAsync(ct))
        .Select(DeploymentDto.From).ToList();

    /// <summary>
    /// The deploy wizard endpoint: push a template to the selected tenants (creating or updating
    /// the Intune app), returning the per-tenant outcome.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<DeploymentDto>>> Deploy(
        DeployRequest req, [FromServices] DeploymentOrchestrator orchestrator, CancellationToken ct)
    {
        if (req.TenantIds.Count == 0) return BadRequest("Select at least one tenant.");
        try
        {
            var results = await orchestrator.DeployAsync(req.TemplateId, req.TenantIds, ct);
            return Ok(results.Select(DeploymentDto.From).ToList());
        }
        catch (KeyNotFoundException e) { return NotFound(e.Message); }
        catch (InvalidOperationException e) { return BadRequest(e.Message); }
    }
}
