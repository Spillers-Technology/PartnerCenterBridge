using PartnerCenterBridge.Core.TenantAudits;

namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A persisted tenant audit (health check) run. Insert-only, like <see cref="WorkflowRun"/>: the
/// full <see cref="TenantAuditReport"/> is kept as JSON in <see cref="ReportJson"/> (what ran, with
/// which parameters, every finding, and why any check could not run), and the summary counts are
/// copied into columns so history lists and estate rollups never need to parse the JSON.
/// Never contains tokens or secrets.
/// </summary>
public class TenantAuditRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Groups runs started together across tenants (an estate run). Null for a single-tenant run.</summary>
    public Guid? BatchId { get; set; }

    public string AuditName { get; set; } = "";
    public string Operator { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary><see cref="TenantAuditReport.SchemaVersion"/> of <see cref="ReportJson"/>.</summary>
    public int SchemaVersion { get; set; }
    /// <summary>PCB version that produced the run.</summary>
    public string EngineVersion { get; set; } = "";

    public AuditHealth Health { get; set; }
    public int ChecksRequested { get; set; }
    public int ChecksCompleted { get; set; }
    public int ChecksUnavailable { get; set; }
    public int ChecksErrored { get; set; }
    public int FailCount { get; set; }
    public int WarnCount { get; set; }
    public int UnknownCount { get; set; }
    public int InfoCount { get; set; }
    public int PassCount { get; set; }
    public int AffectedSubjects { get; set; }

    /// <summary>The complete <see cref="TenantAuditReport"/> (camelCase JSON, enums as names).</summary>
    public string ReportJson { get; set; } = "{}";

    public AuditSummaryCounts ToSummary() => new()
    {
        ChecksRequested = ChecksRequested, ChecksCompleted = ChecksCompleted, ChecksUnavailable = ChecksUnavailable,
        ChecksErrored = ChecksErrored, Fail = FailCount, Warn = WarnCount, Unknown = UnknownCount, Info = InfoCount,
        Pass = PassCount, AffectedSubjects = AffectedSubjects, Health = Health
    };
}
