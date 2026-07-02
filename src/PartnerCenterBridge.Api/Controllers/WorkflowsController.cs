using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record WorkflowSummaryDto(string Id, string Name, string Description, string Category, IReadOnlyList<WorkflowInput> Inputs);
public record WorkflowRunRequest(Guid TenantId, Dictionary<string, string> Inputs);
public record WorkflowRunDto(
    Guid Id, string WorkflowId, string WorkflowName, Guid TenantId, string TenantName,
    WorkflowRunKind Kind, string Operator, Dictionary<string, string> Inputs,
    List<Finding> Findings, List<ProvisioningStep> Steps,
    bool Succeeded, bool? Healthy, string? Error, DateTimeOffset StartedAt, long DurationMs)
{
    public static WorkflowRunDto From(WorkflowRun r) => new(
        r.Id, r.WorkflowId, r.WorkflowName, r.TenantId, r.Tenant?.DisplayName ?? "",
        r.Kind, r.Operator, r.Inputs, r.Findings, r.Steps,
        r.Succeeded, r.Healthy, r.Error, r.StartedAt, r.DurationMs);
}

/// <summary>
/// Uniform catalog + dispatch for the "known-fix" workflows. The UI lists these, renders their
/// inputs, and calls diagnose/remediate generically — adding a workflow needs no controller change.
/// Every run is persisted as a <see cref="WorkflowRun"/> audit record and pushed to the configured
/// notifier on failure.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WorkflowsController : ControllerBase
{
    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly IRunNotifier _notifier;

    public WorkflowsController(WorkflowCatalog catalog, BridgeDbContext db, IRunNotifier notifier)
    {
        _catalog = catalog;
        _db = db;
        _notifier = notifier;
    }

    [HttpGet]
    public IReadOnlyList<WorkflowSummaryDto> List() =>
        _catalog.All
            .OrderBy(w => w.Category).ThenBy(w => w.Name)
            .Select(w => new WorkflowSummaryDto(w.Id, w.Name, w.Description, w.Category, w.Inputs))
            .ToList();

    /// <summary>Recent run history, newest first, optionally filtered by tenant and/or workflow.</summary>
    [HttpGet("runs")]
    public async Task<IReadOnlyList<WorkflowRunDto>> Runs(
        [FromQuery] Guid? tenantId, [FromQuery] string? workflowId, [FromQuery] int take, CancellationToken ct)
    {
        var query = _db.WorkflowRuns.AsNoTracking().Include(r => r.Tenant).AsQueryable();
        if (tenantId is not null) query = query.Where(r => r.TenantId == tenantId);
        if (!string.IsNullOrEmpty(workflowId)) query = query.Where(r => r.WorkflowId == workflowId);

        return (await query
                .OrderByDescending(r => r.StartedAt)
                .Take(Math.Clamp(take == 0 ? 50 : take, 1, 200))
                .ToListAsync(ct))
            .Select(WorkflowRunDto.From).ToList();
    }

    [HttpPost("{id}/diagnose")]
    public async Task<ActionResult<DiagnosisResult>> Diagnose(string id, WorkflowRunRequest req, CancellationToken ct)
    {
        var (workflow, tenant, error) = await Resolve(id, req, ct);
        if (error is not null) return error;

        return await Record(workflow!, tenant!, req, WorkflowRunKind.Diagnose, async run =>
        {
            var result = await workflow!.DiagnoseAsync(tenant!, req.Inputs, ct);
            run.Findings = result.Findings;
            run.Healthy = result.Healthy;
            return result;
        });
    }

    [HttpPost("{id}/remediate")]
    public async Task<ActionResult<WorkflowRunResult>> Remediate(string id, WorkflowRunRequest req, CancellationToken ct)
    {
        var (workflow, tenant, error) = await Resolve(id, req, ct);
        if (error is not null) return error;

        return await Record(workflow!, tenant!, req, WorkflowRunKind.Remediate, async run =>
        {
            var result = await workflow!.RemediateAsync(tenant!, req.Inputs, ct);
            run.Steps = result.Steps;
            run.Findings = result.PostState?.Findings ?? new();
            run.Healthy = result.PostState?.Healthy;
            run.Succeeded = result.Succeeded;
            return result;
        });
    }

    /// <summary>
    /// Runs the action, persisting a <see cref="WorkflowRun"/> whatever happens. Persistence and
    /// notification use CancellationToken.None so an aborted request still leaves an audit trail.
    /// </summary>
    private async Task<ActionResult<T>> Record<T>(
        IWorkflow workflow, Tenant tenant, WorkflowRunRequest req, WorkflowRunKind kind,
        Func<WorkflowRun, Task<T>> action)
    {
        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            TenantId = tenant.Id,
            Tenant = tenant,
            Kind = kind,
            Operator = User.Identity?.Name ?? "anonymous",
            Inputs = new(req.Inputs),
            Succeeded = true
        };
        var sw = Stopwatch.StartNew();
        try
        {
            return Ok(await action(run));
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            return StatusCode(502, ex.Message);
        }
        finally
        {
            run.DurationMs = sw.ElapsedMilliseconds;
            _db.WorkflowRuns.Add(run);
            await _db.SaveChangesAsync(CancellationToken.None);
            await _notifier.NotifyAsync(run, CancellationToken.None);
        }
    }

    private async Task<(IWorkflow? Workflow, Tenant? Tenant, ActionResult? Error)> Resolve(
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
