using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record WorkflowSummaryDto(string Id, string Name, string Description, string Category, IReadOnlyList<WorkflowInput> Inputs);
public record WorkflowRunRequest(Guid TenantId, Dictionary<string, string> Inputs);

/// <summary>
/// Uniform catalog + dispatch for the "known-fix" workflows. The UI lists these, renders their
/// inputs, and calls diagnose/remediate generically — adding a workflow needs no controller change.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WorkflowsController : ControllerBase
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;

    public WorkflowsController(WorkflowCatalog catalog, BridgeDbContext db)
    {
        _catalog = catalog;
        _db = db;
    }

    [HttpGet]
    public IReadOnlyList<WorkflowSummaryDto> List() =>
        _catalog.All
            .OrderBy(w => w.Category).ThenBy(w => w.Name)
            .Select(w => new WorkflowSummaryDto(w.Id, w.Name, w.Description, w.Category, w.Inputs))
            .ToList();

    [HttpPost("{id}/diagnose")]
    public async Task<ActionResult<DiagnosisResult>> Diagnose(string id, WorkflowRunRequest req, CancellationToken ct)
    {
        var (workflow, tenant, error) = await Resolve(id, req, ct);
        if (error is not null) return error;
        try { return Ok(await workflow!.DiagnoseAsync(tenant!, req.Inputs, ct)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    [HttpPost("{id}/remediate")]
    public async Task<ActionResult<WorkflowRunResult>> Remediate(string id, WorkflowRunRequest req, CancellationToken ct)
    {
        var (workflow, tenant, error) = await Resolve(id, req, ct);
        if (error is not null) return error;
        try { return Ok(await workflow!.RemediateAsync(tenant!, req.Inputs, ct)); }
        catch (Exception ex) { return StatusCode(502, ex.Message); }
    }

    private async Task<(IWorkflow? Workflow, Core.Entities.Tenant? Tenant, ActionResult? Error)> Resolve(
        string id, WorkflowRunRequest req, CancellationToken ct)
    {
        var workflow = _catalog.Find(id);
        if (workflow is null) return (null, null, NotFound($"Unknown workflow '{id}'."));
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return (null, null, NotFound("Tenant not found."));

        var inputs = req.Inputs ?? new();
        foreach (var input in workflow.Inputs.Where(i => i.Required))
            if (!inputs.TryGetValue(input.Key, out var v) || string.IsNullOrWhiteSpace(v))
                return (null, null, BadRequest($"Missing required input '{input.Key}'."));
        return (workflow, tenant, null);
    }
}
