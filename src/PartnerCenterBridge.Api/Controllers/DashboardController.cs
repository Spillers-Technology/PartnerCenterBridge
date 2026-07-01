using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public DashboardController(BridgeDbContext db) => _db = db;

    [HttpGet]
    public async Task<DashboardDto> Get(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);

        var stats = new DashboardStats(
            Tenants: await _db.Tenants.CountAsync(ct),
            TenantsNoDelegation: await _db.Tenants.CountAsync(t => t.Status == TenantStatus.NoDelegation, ct),
            Deployments: await _db.Deployments.CountAsync(ct),
            DeploymentsFailed: await _db.Deployments.CountAsync(d => d.Status == DeploymentStatus.Failed, ct),
            DeploymentsUpdateAvailable: await _db.Deployments.CountAsync(d => d.Status == DeploymentStatus.UpdateAvailable, ct),
            RunsLast24h: await _db.WorkflowRuns.CountAsync(r => r.StartedAt >= dayAgo, ct),
            RunsFailedLast7d: await _db.WorkflowRuns.CountAsync(r => !r.Succeeded && r.StartedAt >= weekAgo, ct));

        var attention = new List<AttentionItem>();

        attention.AddRange(await _db.Deployments.AsNoTracking()
            .Include(d => d.Tenant).Include(d => d.AppTemplate)
            .Where(d => d.Status == DeploymentStatus.Failed)
            .OrderByDescending(d => d.LastSyncedAt ?? d.CreatedAt)
            .Take(10)
            .Select(d => new AttentionItem("Deployment failed", d.TenantId, d.Tenant!.DisplayName,
                d.AppTemplate!.DisplayName, d.LastError ?? "unknown error", d.LastSyncedAt ?? d.CreatedAt))
            .ToListAsync(ct));

        attention.AddRange(await _db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.NoDelegation)
            .OrderBy(t => t.DisplayName)
            .Take(10)
            .Select(t => new AttentionItem("No delegation", t.Id, t.DisplayName,
                t.DefaultDomain ?? t.TenantId, "GDAP relationship missing or expired - the bridge cannot act here.", t.LastSeenAt))
            .ToListAsync(ct));

        attention.AddRange((await _db.WorkflowRuns.AsNoTracking()
            .Include(r => r.Tenant)
            .Where(r => !r.Succeeded && r.StartedAt >= weekAgo)
            .OrderByDescending(r => r.StartedAt)
            .Take(10)
            .ToListAsync(ct))
            .Select(r => new AttentionItem("Workflow failed", r.TenantId, r.Tenant?.DisplayName ?? "",
                r.WorkflowName, r.Error ?? "one or more steps failed", r.StartedAt)));

        var recentRuns = (await _db.WorkflowRuns.AsNoTracking()
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
