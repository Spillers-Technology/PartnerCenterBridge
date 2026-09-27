using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;
using PartnerCenterBridge.Data;
using PartnerCenterBridge.Graph.Operations;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProvisioningController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly IGraphUserService _users;
    private readonly IOffboardingService _offboarding;
    private readonly ITenantAccessService _access;
    private readonly OperationRunRecorder _recorder;

    public ProvisioningController(
        BridgeDbContext db, IGraphUserService users, IOffboardingService offboarding,
        ITenantAccessService access, OperationRunRecorder recorder)
    {
        _db = db;
        _users = users;
        _offboarding = offboarding;
        _access = access;
        _recorder = recorder;
    }

    /// <summary>Create a new-hire user in the selected tenant, applying licenses/groups/manager.</summary>
    [HttpPost("hire")]
    public async Task<ActionResult<ProvisioningResult>> Hire(HireApiRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(req.TenantId, TenantRole.Operator, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        return Ok(await _users.CreateUserAsync(tenant, req.Hire, ct));
    }

    /// <summary>
    /// Ordered offboarding plan under the effective policy (contract policy, request overrides).
    /// Read-only; recorded as a Plan run. Viewer grant.
    /// </summary>
    [HttpPost("terminate/plan")]
    public async Task<ActionResult<OperationPlan>> TerminatePlan(TerminateApiRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(req.TenantId, TenantRole.Viewer, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        var (policy, invalid) = await EffectivePolicyAsync(tenant, req, ct);
        if (invalid is not null) return invalid;

        var session = _recorder.Start(OffboardingOperation.OperationId, OffboardingOperation.OperationName, tenant,
            WorkflowRunKind.Plan, Inputs(req.Termination.UserId, policy!), req.Termination.UserId);
        OperationPlan? plan = null;
        Exception? error = null;
        try
        {
            plan = await _offboarding.PlanAsync(tenant, req.Termination.UserId, policy!, ct);
            return Ok(plan);
        }
        catch (OperationInputException ex) { error = ex; return BadRequest(ex.Message); }
        catch (Exception ex) { error = ex; return StatusCode(502, ex.Message); }
        finally
        {
            await _recorder.CompleteAsync(session, plan is null ? null : WorkflowEvidenceAdapter.FromPlan(plan), error);
        }
    }

    /// <summary>
    /// Offboard a user: plan -> apply -> verify under the effective policy. Mailbox conversion runs
    /// before license removal, and license removal/group cleanup are skipped (with the reason) when a
    /// requested conversion is not verified. Persisted as a run with evidence.
    /// </summary>
    [HttpPost("terminate")]
    public async Task<ActionResult<TerminateResultDto>> Terminate(TerminateApiRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(req.TenantId, TenantRole.Operator, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([req.TenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");
        var (policy, invalid) = await EffectivePolicyAsync(tenant, req, ct);
        if (invalid is not null) return invalid;

        var session = _recorder.Start(OffboardingOperation.OperationId, OffboardingOperation.OperationName, tenant,
            WorkflowRunKind.Apply, Inputs(req.Termination.UserId, policy!), req.Termination.UserId);
        OperationEvidence? evidence = null;
        Exception? error = null;
        try
        {
            evidence = await _offboarding.ApplyAsync(tenant, req.Termination.UserId, policy!, ct);
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
        var stamped = session.Run.Evidence!;
        return Ok(new TerminateResultDto(
            req.Termination.UserId, null, null,
            OffboardingOperation.ToSteps(stamped),
            OutcomeRules.IsSuccess(stamped.Outcome),
            stamped, policy!));
    }

    /// <summary>
    /// Base = request policy, else the tenant's contract policy, else built-in defaults (today's
    /// behavior). Termination flags that are present override the base for this run.
    /// </summary>
    private async Task<(OffboardingPolicy? Policy, ActionResult? Invalid)> EffectivePolicyAsync(
        Tenant tenant, TerminateApiRequest req, CancellationToken ct)
    {
        if (req.Termination is null || string.IsNullOrWhiteSpace(req.Termination.UserId))
            return (null, BadRequest("termination.userId is required."));

        var basePolicy = req.Policy;
        if (basePolicy is null && tenant.ContractId is { } contractId)
            basePolicy = await _db.Contracts.AsNoTracking().Where(c => c.Id == contractId)
                .Select(c => c.OffboardingPolicy).FirstOrDefaultAsync(ct);
        var policy = (basePolicy ?? new OffboardingPolicy()).Clone();

        var t = req.Termination;
        if (t.BlockSignIn is { } block) policy.BlockSignIn = block;
        if (t.RevokeSessions is { } revoke) policy.RevokeSessions = revoke;
        if (t.RemoveLicenses is { } licenses) policy.RemoveLicenses = licenses;
        if (t.ConvertMailboxToShared is { } convert) policy.ConvertMailboxToShared = convert;
        if (t.RemoveFromGroups is { } groups)
            policy.GroupCleanup = !groups ? GroupCleanupMode.None
                : policy.GroupCleanup == GroupCleanupMode.None ? GroupCleanupMode.RemoveAll : policy.GroupCleanup;
        if (!string.IsNullOrWhiteSpace(t.ForwardingSmtpAddress)) policy.ForwardTo = t.ForwardingSmtpAddress.Trim();

        var errors = policy.Validate();
        return errors.Count > 0 ? (null, BadRequest(string.Join(" ", errors))) : (policy, null);
    }

    private static Dictionary<string, string> Inputs(string userId, OffboardingPolicy p) => new()
    {
        ["userId"] = userId.Trim(),
        ["blockSignIn"] = p.BlockSignIn.ToString().ToLowerInvariant(),
        ["revokeSessions"] = p.RevokeSessions.ToString().ToLowerInvariant(),
        ["groupCleanup"] = p.GroupCleanup.ToString(),
        ["convertMailboxToShared"] = p.ConvertMailboxToShared.ToString().ToLowerInvariant(),
        ["removeLicenses"] = p.RemoveLicenses.ToString().ToLowerInvariant(),
        ["hideFromGal"] = p.HideFromGal.ToString().ToLowerInvariant(),
        ["forwardTo"] = p.ForwardTo ?? "",
        ["managerAccess"] = p.ManagerAccess.ToString(),
        ["wipeDevices"] = p.WipeDevices.ToString(),
        ["followUpDays"] = p.FollowUpDays.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
