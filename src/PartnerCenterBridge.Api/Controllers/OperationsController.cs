using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Core.Workflows;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record AccessParityPlanRequest(string SourceUserId, string TargetUserId);
public record AccessParityApplyRequest(string SourceUserId, string TargetUserId, List<string>? ItemIds);

/// <summary>
/// Tenant-scoped planned operations. Access Parity: Viewer to plan, Operator to apply. Both
/// calls are recorded as runs with evidence (a plan is a read of two people's access, audited the
/// same way diagnose runs are).
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId:guid}/operations")]
[Authorize]
public class OperationsController : ControllerBase
{
    private const string AccessParityId = "access-parity";

    private readonly WorkflowCatalog _catalog;
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;
    private readonly OperationRunRecorder _recorder;

    public OperationsController(WorkflowCatalog catalog, BridgeDbContext db, ITenantAccessService access, OperationRunRecorder recorder)
    {
        _catalog = catalog;
        _db = db;
        _access = access;
        _recorder = recorder;
    }

    [HttpPost("access-parity/plan")]
    public async Task<ActionResult<OperationPlan>> PlanAccessParity(Guid tenantId, AccessParityPlanRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (Validate(req.SourceUserId, req.TargetUserId) is { } bad) return BadRequest(bad);
        if (_catalog.Find(AccessParityId) is not IPlannedOperation op) return NotFound("Access parity is not available.");

        var inputs = Inputs(req.SourceUserId, req.TargetUserId);
        var session = _recorder.Start(op.Id, op.Name, tenant, WorkflowRunKind.Plan, inputs, req.TargetUserId);
        OperationPlan? plan = null;
        Exception? error = null;
        try
        {
            plan = await op.PlanAsync(tenant, inputs, ct);
            return Ok(plan);
        }
        catch (OperationInputException ex) { error = ex; return BadRequest(ex.Message); }
        catch (Exception ex) { error = ex; return StatusCode(502, ex.Message); }
        finally
        {
            await _recorder.CompleteAsync(session, plan is null ? null : WorkflowEvidenceAdapter.FromPlan(plan), error);
        }
    }

    [HttpPost("access-parity/apply")]
    public async Task<ActionResult<OperationEvidence>> ApplyAccessParity(Guid tenantId, AccessParityApplyRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        if (Validate(req.SourceUserId, req.TargetUserId) is { } bad) return BadRequest(bad);
        if (req.ItemIds is null) return BadRequest("itemIds is required (the plan item ids to apply).");
        if (_catalog.Find(AccessParityId) is not IPlannedOperation op) return NotFound("Access parity is not available.");

        var inputs = Inputs(req.SourceUserId, req.TargetUserId);
        var session = _recorder.Start(op.Id, op.Name, tenant, WorkflowRunKind.Apply, inputs, req.TargetUserId);
        OperationEvidence? evidence = null;
        Exception? error = null;
        try
        {
            evidence = await op.ApplyAsync(tenant, inputs, req.ItemIds, ct);
        }
        catch (OperationInterruptedException ex)
        {
            // Cancelled or failed mid-apply: persist what already changed, marked as interrupted.
            evidence = ex.Partial;
            error = ex.InnerException ?? ex;
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            await _recorder.CompleteAsync(session, evidence, error);
        }

        if (error is OperationInputException) return BadRequest(error.Message);
        if (error is not null) return StatusCode(502, error.Message);
        return Ok(session.Run.Evidence);
    }

    private static string? Validate(string? source, string? target)
    {
        if (string.IsNullOrWhiteSpace(source)) return "sourceUserId is required.";
        if (string.IsNullOrWhiteSpace(target)) return "targetUserId is required.";
        if (string.Equals(source.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Source and target must be different users.";
        return null;
    }

    private static Dictionary<string, string> Inputs(string source, string target) => new()
    {
        ["sourceUserId"] = source.Trim(),
        ["targetUserId"] = target.Trim()
    };
}
