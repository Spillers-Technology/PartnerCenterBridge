using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Services;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/pending-actions")]
[Authorize]
public class PendingActionsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly PendingActionService _pending;
    private readonly ITenantAccessService _access;
    private readonly IReadOnlyList<IPendingActionExecutor> _executors;

    public PendingActionsController(
        BridgeDbContext db, PendingActionService pending, ITenantAccessService access,
        IEnumerable<IPendingActionExecutor> executors)
    {
        _db = db;
        _pending = pending;
        _access = access;
        _executors = executors.ToList();
    }

    public record PendingActionDto(
        Guid Id, Guid TenantId, string TenantName, string ActionType, string PreviewSummary,
        PendingActionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
        string? ExecutionError);

    /// <summary>Pending and retryable items across every tenant the caller holds Operator+ access to (unrestricted for a system admin, same convention as WorkflowsController.Runs).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PendingActionDto>>> List(CancellationToken ct)
    {
        var query = _db.PendingActions.AsNoTracking().Include(a => a.Tenant)
            .Where(a => a.Status == PendingActionStatus.Pending
                     || (a.Status == PendingActionStatus.Approved && a.ExecutionError != null));

        if (!_access.IsSystemAdmin)
        {
            var allowed = await _db.TenantAccessGrants.AsNoTracking()
                .Where(g => g.UserId == _access.CurrentUserId && g.Role >= TenantRole.Operator
                         && (g.ExpiresAt == null || g.ExpiresAt > DateTimeOffset.UtcNow))
                .Select(g => g.TenantId).ToListAsync(ct);
            query = query.Where(a => allowed.Contains(a.TenantId));
        }

        var items = await query
            .Select(a => new PendingActionDto(
                a.Id, a.TenantId, a.Tenant!.DisplayName, a.ActionType, a.PreviewSummary,
                a.Status, a.CreatedAt, a.ExpiresAt, a.ExecutionError))
            .ToListAsync(ct);
        return Ok(items.OrderBy(a => a.CreatedAt).ToList());
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.FindAsync([id], ct);
        if (action is null) return NotFound();
        if (!await _access.HasRoleAsync(action.TenantId, TenantRole.Operator, ct)) return Forbid();

        var executor = _executors.FirstOrDefault(e => e.ActionType == action.ActionType);
        if (executor is null) return StatusCode(500, $"No executor registered for '{action.ActionType}'.");

        try
        {
            await _pending.ApproveAsync(id, _access.CurrentUserId ?? Guid.Empty, a => executor.ExecuteAsync(a, ct), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.FindAsync([id], ct);
        if (action is null) return NotFound();
        if (!await _access.HasRoleAsync(action.TenantId, TenantRole.Operator, ct)) return Forbid();

        try { await _pending.RejectAsync(id, _access.CurrentUserId ?? Guid.Empty, ct); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        return NoContent();
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var action = await _db.PendingActions.FindAsync([id], ct);
        if (action is null) return NotFound();
        if (!await _access.HasRoleAsync(action.TenantId, TenantRole.Operator, ct)) return Forbid();

        var executor = _executors.FirstOrDefault(e => e.ActionType == action.ActionType);
        if (executor is null) return StatusCode(500, $"No executor registered for '{action.ActionType}'.");

        try
        {
            await _pending.RetryAsync(id, a => executor.ExecuteAsync(a, ct), ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        return NoContent();
    }
}
