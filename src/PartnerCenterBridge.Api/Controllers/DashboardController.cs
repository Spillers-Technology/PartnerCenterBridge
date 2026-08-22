using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

public record DashboardStats(
    int Tenants, int TenantsNoDelegation,
    int Deployments, int DeploymentsFailed, int DeploymentsUpdateAvailable,
    int RunsLast24h, int RunsFailedLast7d);

public record AttentionItem(string Kind, Guid TenantId, string TenantName, string Subject, string Detail, DateTimeOffset? When);

public record DashboardDto(DashboardStats Stats, IReadOnlyList<AttentionItem> NeedsAttention, IReadOnlyList<WorkflowRunDto> RecentRuns);

/// <summary>
/// The landing view's aggregate: everything comes from the local database (no Graph calls), so
/// it is fast and safe to hit on every page load. "Needs attention" is the triage list - failed
/// deployments, tenants without delegation, and recent failed workflow runs.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;

    public DashboardController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<DashboardDto> Get(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);

        var viewerIds = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        var operatorIds = await _access.GetAuthorizedTenantIdsAsync(TenantRole.Operator, ct);
        var tenants = _db.Tenants.AsNoTracking().AsQueryable();
        var deployments = _db.Deployments.AsNoTracking().AsQueryable();
        var runs = _db.WorkflowRuns.AsNoTracking().AsQueryable();
        var pendingActions = _db.PendingActions.AsNoTracking().AsQueryable();
        if (viewerIds is not null)
        {
            tenants = tenants.Where(tenant => viewerIds.Contains(tenant.Id));
            deployments = deployments.Where(deployment => viewerIds.Contains(deployment.TenantId));
            runs = runs.Where(run => viewerIds.Contains(run.TenantId));
        }
        if (operatorIds is not null)
            pendingActions = pendingActions.Where(action => operatorIds.Contains(action.TenantId));

        var stats = new DashboardStats(
            Tenants: await tenants.CountAsync(ct),
            TenantsNoDelegation: await tenants.CountAsync(t => t.Status == TenantStatus.NoDelegation, ct),
            Deployments: await deployments.CountAsync(ct),
            DeploymentsFailed: await deployments.CountAsync(d => d.Status == DeploymentStatus.Failed, ct),
            DeploymentsUpdateAvailable: await deployments.CountAsync(d => d.Status == DeploymentStatus.UpdateAvailable, ct),
            RunsLast24h: await runs.CountAsync(r => r.StartedAt >= dayAgo, ct),
            RunsFailedLast7d: await runs.CountAsync(r => !r.Succeeded && r.StartedAt >= weekAgo, ct));

        var attention = new List<AttentionItem>();

        attention.AddRange(await deployments
            .Include(d => d.Tenant).Include(d => d.AppTemplate)
            .Where(d => d.Status == DeploymentStatus.Failed)
            .OrderByDescending(d => d.LastSyncedAt ?? d.CreatedAt)
            .Take(10)
            .Select(d => new AttentionItem("Deployment failed", d.TenantId, d.Tenant!.DisplayName,
                d.AppTemplate!.DisplayName, d.LastError ?? "unknown error", d.LastSyncedAt ?? d.CreatedAt))
            .ToListAsync(ct));

        attention.AddRange(await tenants
            .Where(t => t.Status == TenantStatus.NoDelegation)
            .OrderBy(t => t.DisplayName)
            .Take(10)
            .Select(t => new AttentionItem("No delegation", t.Id, t.DisplayName,
                t.DefaultDomain ?? t.TenantId, "GDAP relationship missing or expired - the bridge cannot act here.", t.LastSeenAt))
            .ToListAsync(ct));

        attention.AddRange((await runs
            .Include(r => r.Tenant)
            .Where(r => !r.Succeeded && r.StartedAt >= weekAgo)
            .OrderByDescending(r => r.StartedAt)
            .Take(10)
            .ToListAsync(ct))
            .Select(r => new AttentionItem("Workflow failed", r.TenantId, r.Tenant?.DisplayName ?? "",
                r.WorkflowName, r.Error ?? "one or more steps failed", r.StartedAt)));

        attention.AddRange(await pendingActions
            .Include(a => a.Tenant)
            .Where(a => a.Status == PendingActionStatus.Pending
                     || (a.Status == PendingActionStatus.Approved && a.ExecutionError != null))
            .OrderByDescending(a => a.CreatedAt)
            .Take(10)
            .Select(a => new AttentionItem(
                a.Status == PendingActionStatus.Pending ? "Needs approval" : "Execution failed",
                a.TenantId, a.Tenant!.DisplayName, a.ActionType,
                a.Status == PendingActionStatus.Approved && a.ExecutionError != null
                    ? a.ExecutionError : a.PreviewSummary,
                a.CreatedAt))
            .ToListAsync(ct));

        var recentRuns = (await runs
            .Include(r => r.Tenant)
            .OrderByDescending(r => r.StartedAt)
            .Take(10)
            .ToListAsync(ct))
            .Select(WorkflowRunDto.From).ToList();

        return new DashboardDto(
            stats,
            attention.OrderByDescending(a => a.When ?? DateTimeOffset.MinValue).ToList(),
            recentRuns);
    }
}
