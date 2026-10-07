using PartnerCenterBridge.Api.Hosting;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.TenantAudits;

/// <summary>
/// Runs an audit selection against a tenant and stores the result. Deliberately thin: selection
/// is resolved by <see cref="TenantAuditCatalog"/>, execution is <see cref="TenantAuditEngine"/>,
/// so a future background or estate-wide runner calls the same two pieces per tenant.
/// </summary>
public class TenantAuditService(BridgeDbContext db, ICurrentActor actor, TimeProvider? clock = null)
{
    public async Task<(TenantAuditRun Run, TenantAuditReport Report)> RunAsync(
        Tenant tenant, AuditSelection selection, Guid? batchId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var report = await TenantAuditEngine.RunAsync(tenant, selection, actor.Name, HostingInfo.ProductVersion,
            clock, id, batchId, ct);
        var run = ToEntity(report, tenant.Id, batchId);
        db.TenantAuditRuns.Add(run);
        // CancellationToken.None: a run that finished is recorded even if the caller went away.
        await db.SaveChangesAsync(CancellationToken.None);
        return (run, report);
    }

    public static TenantAuditRun ToEntity(TenantAuditReport report, Guid tenantId, Guid? batchId)
    {
        var s = report.Summary;
        return new TenantAuditRun
        {
            Id = Guid.Parse(report.RunId),
            TenantId = tenantId,
            BatchId = batchId,
            AuditName = report.AuditName,
            Operator = report.Operator,
            StartedAt = report.StartedAt,
            CompletedAt = report.CompletedAt,
            SchemaVersion = report.SchemaVersion,
            EngineVersion = report.EngineVersion,
            Health = s.Health,
            ChecksRequested = s.ChecksRequested,
            ChecksCompleted = s.ChecksCompleted,
            ChecksUnavailable = s.ChecksUnavailable,
            ChecksErrored = s.ChecksErrored,
            FailCount = s.Fail,
            WarnCount = s.Warn,
            UnknownCount = s.Unknown,
            InfoCount = s.Info,
            PassCount = s.Pass,
            AffectedSubjects = s.AffectedSubjects,
            ReportJson = TenantAuditJson.Serialize(report)
        };
    }
}
