using System.Diagnostics;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Operations;

namespace PartnerCenterBridge.Core.TenantAudits;

/// <summary>
/// Runs a selection of checks against one tenant and assembles a <see cref="TenantAuditReport"/>.
/// Checks run sequentially (they share cached datasets, and sequential calls are kinder to Graph
/// throttling). Each check is isolated: unavailability or an error in one is recorded on that
/// check and the rest still run. The engine has no persistence and no HTTP: the API, a future
/// scheduler, or a multi-tenant batch all call it the same way, once per tenant.
/// </summary>
public static class TenantAuditEngine
{
    /// <summary>Subjects kept per finding. The true total stays in <see cref="AuditFinding.SubjectCount"/>.</summary>
    public const int MaxSubjectsPerFinding = 5000;

    public static async Task<TenantAuditReport> RunAsync(
        Tenant tenant, AuditSelection selection, string operatorName, string engineVersion,
        TimeProvider? clock = null, Guid? runId = null, Guid? batchId = null, CancellationToken ct = default)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetUtcNow();
        var context = new AuditCheckContext(tenant, new AuditParameters(selection.Parameters), started, ct);

        var report = new TenantAuditReport
        {
            RunId = (runId ?? Guid.NewGuid()).ToString(),
            BatchId = batchId?.ToString(),
            AuditName = selection.AuditName,
            Tenant = new OperationTenantRef { Id = tenant.Id.ToString(), DisplayName = tenant.DisplayName, TenantId = tenant.TenantId },
            Operator = operatorName,
            StartedAt = started,
            EngineVersion = engineVersion,
            Parameters = new SortedDictionary<string, int>(selection.Parameters, StringComparer.Ordinal),
            RequestedCheckIds = selection.Checks.Select(c => c.Descriptor.Id).ToList()
        };

        foreach (var check in selection.Checks)
            report.Checks.Add(await RunCheckAsync(check, context, ct));

        report.CompletedAt = clock.GetUtcNow();
        report.Summary = AuditSummary.Compute(report);
        return report;
    }

    /// <summary>Runs one check, converting its failure modes into a result instead of an exception.</summary>
    public static async Task<AuditCheckResult> RunCheckAsync(ITenantAuditCheck check, AuditCheckContext context, CancellationToken ct)
    {
        var d = check.Descriptor;
        var result = new AuditCheckResult { CheckId = d.Id, CheckName = d.Name, Category = d.Category, CheckVersion = d.Version };
        var clock = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            var output = await check.RunAsync(context);
            result.Status = AuditCheckStatus.Completed;
            result.Findings = Normalize(d.Id, output.Findings);
            if (result.Findings.Count == 0)
                result.Findings.Add(AuditFindingBuilder.Pass(d, "No issues found."));
            result.Notes.AddRange(output.Notes);
        }
        catch (AuditUnavailableException ex)
        {
            result.Status = AuditCheckStatus.Unavailable;
            result.StatusReason = ex.Message;
            result.MissingRequirements = ex.Missing.ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Status = AuditCheckStatus.Error;
            result.StatusReason = ex.Message;
        }
        result.Notes.AddRange(d.Limitations);
        result.DurationMs = clock.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Deterministic ordering for display, diff and export: findings worst-first then by id;
    /// subjects by name, then UPN, then id (ordinal). Subject lists are capped after sorting.
    /// </summary>
    internal static List<AuditFinding> Normalize(string checkId, IEnumerable<AuditFinding> findings)
    {
        var list = findings.ToList();
        foreach (var f in list)
        {
            f.CheckId = checkId;
            var total = Math.Max(f.SubjectCount, f.Subjects.Count);
            f.Subjects = f.Subjects
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Upn ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(MaxSubjectsPerFinding)
                .ToList();
            f.SubjectCount = total;
        }
        return list
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Counting and health classification for one report, and the rollup across many.</summary>
public static class AuditSummary
{
    public static AuditSummaryCounts Compute(TenantAuditReport report)
    {
        var c = new AuditSummaryCounts
        {
            ChecksRequested = report.Checks.Count,
            ChecksCompleted = report.Checks.Count(x => x.Status == AuditCheckStatus.Completed),
            ChecksUnavailable = report.Checks.Count(x => x.Status == AuditCheckStatus.Unavailable),
            ChecksErrored = report.Checks.Count(x => x.Status == AuditCheckStatus.Error)
        };
        foreach (var f in report.Checks.Where(x => x.Status == AuditCheckStatus.Completed).SelectMany(x => x.Findings))
        {
            switch (f.Severity)
            {
                case AuditSeverity.Pass: c.Pass++; break;
                case AuditSeverity.Info: c.Info++; break;
                case AuditSeverity.Unknown: c.Unknown++; break;
                case AuditSeverity.Warn: c.Warn++; break;
                case AuditSeverity.Fail: c.Fail++; break;
            }
            if (f.Severity is AuditSeverity.Warn or AuditSeverity.Fail) c.AffectedSubjects += f.SubjectCount;
        }
        c.Health = Classify(c);
        return c;
    }

    /// <summary>
    /// Fail anywhere is high risk; Warn is attention needed; a run where nothing completed says
    /// nothing about the tenant, so it is "insufficient access" rather than healthy. Unknown and
    /// Info findings do not by themselves move a tenant out of Healthy.
    /// </summary>
    public static AuditHealth Classify(AuditSummaryCounts c) =>
        c.ChecksRequested > 0 && c.ChecksCompleted == 0 ? AuditHealth.InsufficientAccess
        : c.Fail > 0 ? AuditHealth.HighRisk
        : c.Warn > 0 ? AuditHealth.AttentionNeeded
        : AuditHealth.Healthy;
}

/// <summary>"42 tenants scanned: 31 healthy, 7 attention needed, 3 high risk, 1 insufficient access."</summary>
public sealed class AuditEstateSummary
{
    public int TenantsScanned { get; set; }
    public int Healthy { get; set; }
    public int AttentionNeeded { get; set; }
    public int HighRisk { get; set; }
    public int InsufficientAccess { get; set; }
    /// <summary>Tenants whose run had at least one check unavailable or in error.</summary>
    public int WithCoverageGaps { get; set; }

    public static AuditEstateSummary From(IEnumerable<AuditSummaryCounts> latestPerTenant)
    {
        var s = new AuditEstateSummary();
        foreach (var c in latestPerTenant)
        {
            s.TenantsScanned++;
            switch (c.Health)
            {
                case AuditHealth.Healthy: s.Healthy++; break;
                case AuditHealth.AttentionNeeded: s.AttentionNeeded++; break;
                case AuditHealth.HighRisk: s.HighRisk++; break;
                case AuditHealth.InsufficientAccess: s.InsufficientAccess++; break;
            }
            if (c.ChecksUnavailable + c.ChecksErrored > 0) s.WithCoverageGaps++;
        }
        return s;
    }
}
