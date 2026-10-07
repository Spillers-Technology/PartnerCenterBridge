using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.TenantAudits;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Tenant audits (health checks): read-only checks against a tenant's Microsoft 365 state, kept as
/// history and exportable as CSV, JSON or Markdown. Viewer on the tenant can run and read them --
/// an audit only reads, the same reasoning that lets a Viewer plan Access Parity. No endpoint here
/// changes a tenant; findings link to the existing planned operations for that.
/// </summary>
[ApiController]
[Authorize]
public class TenantAuditsController(
    BridgeDbContext db, ITenantAccessService access, TenantAuditCatalog catalog, TenantAuditService audits) : ControllerBase
{
    /// <summary>Every check with its metadata, plus categories, presets and parameters -- what the run picker shows.</summary>
    [HttpGet("api/tenant-audits/catalog")]
    public ActionResult<AuditCatalogDto> Catalog() => Ok(new AuditCatalogDto(
        AuditCategories.All.Select(c => new AuditCategoryDto(c, AuditCategories.Label(c))).ToList(),
        catalog.Presets,
        catalog.Parameters,
        catalog.All.Select(c => AuditCheckInfoDto.From(c.Descriptor)).ToList(),
        TenantAuditReport.CurrentSchemaVersion));

    [HttpGet("api/tenants/{tenantId:guid}/audits")]
    public async Task<ActionResult<IReadOnlyList<TenantAuditRunSummaryDto>>> List(Guid tenantId, CancellationToken ct)
    {
        if (!await access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var runs = await db.TenantAuditRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Select(r => new { Run = r, TenantName = r.Tenant!.DisplayName })
            .ToListAsync(ct);
        // Ordered in memory: SQLite stores DateTimeOffset as binary, which sorts correctly, but
        // keeping the ordering here makes both providers behave identically.
        return Ok(runs.OrderByDescending(x => x.Run.StartedAt).Take(50)
            .Select(x => { x.Run.ReportJson = ""; return TenantAuditRunSummaryDto.From(x.Run, x.TenantName); }).ToList());
    }

    /// <summary>Run an audit now and return the full report. Read-only against the tenant; the run is stored.</summary>
    [HttpPost("api/tenants/{tenantId:guid}/audits")]
    public async Task<IActionResult> Run(Guid tenantId, RunTenantAuditRequest req, CancellationToken ct)
    {
        if (!await access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var tenant = await db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound("Tenant not found.");

        AuditSelection selection;
        try { selection = catalog.Resolve(req.CheckIds, req.Categories, req.Parameters); }
        catch (AuditRequestException ex) { return BadRequest(ex.Message); }

        var (run, _) = await audits.RunAsync(tenant, selection, batchId: null, ct);
        return Content(run.ReportJson, "application/json", Encoding.UTF8);
    }

    [HttpGet("api/tenants/{tenantId:guid}/audits/{runId:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, Guid runId, CancellationToken ct)
    {
        if (!await access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var run = await FindAsync(tenantId, runId, ct);
        return run is null ? NotFound() : Content(run.ReportJson, "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Download a run: <c>format=csv</c> (default; one row per finding subject), <c>json</c> (the
    /// full report) or <c>markdown</c> (ticket/customer notes).
    /// </summary>
    [HttpGet("api/tenants/{tenantId:guid}/audits/{runId:guid}/export")]
    public async Task<IActionResult> Export(Guid tenantId, Guid runId, [FromQuery] string? format, CancellationToken ct)
    {
        if (!await access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var run = await FindAsync(tenantId, runId, ct);
        if (run is null) return NotFound();
        var report = TenantAuditJson.Deserialize(run.ReportJson);
        if (report is null) return Problem("The stored audit report could not be read.");

        var name = FileName(report);
        return (format ?? "csv").ToLowerInvariant() switch
        {
            "csv" => File(TenantAuditCsv.WriteBytes(report), "text/csv; charset=utf-8", name + ".csv"),
            "json" => File(Encoding.UTF8.GetBytes(TenantAuditJson.Write(report)), "application/json", name + ".json"),
            "markdown" or "md" => File(Encoding.UTF8.GetBytes(TenantAuditMarkdown.Write(report)), "text/markdown; charset=utf-8", name + ".md"),
            _ => BadRequest("format must be csv, json or markdown.")
        };
    }

    /// <summary>The latest audit of each tenant the caller can see, rolled up into an estate summary.</summary>
    [HttpGet("api/tenant-audits/estate")]
    public async Task<ActionResult<AuditEstateDto>> Estate(CancellationToken ct)
    {
        var allowed = await access.GetAuthorizedTenantIdsAsync(TenantRole.Viewer, ct);
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => allowed == null || allowed.Contains(t.Id))
            .Select(t => new { t.Id, t.DisplayName })
            .ToListAsync(ct);
        var ids = tenants.Select(t => t.Id).ToList();
        var runs = await db.TenantAuditRuns.AsNoTracking()
            .Where(r => ids.Contains(r.TenantId))
            .Select(r => new
            {
                r.Id, r.TenantId, r.BatchId, r.AuditName, r.Operator, r.StartedAt, r.CompletedAt, r.SchemaVersion, r.EngineVersion,
                r.Health, r.ChecksRequested, r.ChecksCompleted, r.ChecksUnavailable, r.ChecksErrored,
                r.FailCount, r.WarnCount, r.UnknownCount, r.InfoCount, r.PassCount, r.AffectedSubjects
            })
            .ToListAsync(ct);
        var latest = runs.GroupBy(r => r.TenantId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());

        var rows = tenants.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).Select(t =>
        {
            if (!latest.TryGetValue(t.Id, out var r)) return new AuditEstateTenantDto(t.Id, t.DisplayName, null);
            var entity = new Core.Entities.TenantAuditRun
            {
                Id = r.Id, TenantId = r.TenantId, BatchId = r.BatchId, AuditName = r.AuditName, Operator = r.Operator,
                StartedAt = r.StartedAt, CompletedAt = r.CompletedAt, SchemaVersion = r.SchemaVersion, EngineVersion = r.EngineVersion,
                Health = r.Health, ChecksRequested = r.ChecksRequested, ChecksCompleted = r.ChecksCompleted,
                ChecksUnavailable = r.ChecksUnavailable, ChecksErrored = r.ChecksErrored, FailCount = r.FailCount, WarnCount = r.WarnCount,
                UnknownCount = r.UnknownCount, InfoCount = r.InfoCount, PassCount = r.PassCount, AffectedSubjects = r.AffectedSubjects
            };
            return new AuditEstateTenantDto(t.Id, t.DisplayName, TenantAuditRunSummaryDto.From(entity, t.DisplayName));
        }).ToList();

        var summary = AuditEstateSummary.From(rows.Where(r => r.LatestRun is not null).Select(r => r.LatestRun!.Summary));
        return Ok(new AuditEstateDto(summary, rows.Count(r => r.LatestRun is null), rows));
    }

    /// <summary>The run, only if it belongs to the route's tenant (a foreign run id is simply not found).</summary>
    private Task<Core.Entities.TenantAuditRun?> FindAsync(Guid tenantId, Guid runId, CancellationToken ct) =>
        db.TenantAuditRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == tenantId, ct);

    /// <summary>"tenant-audit-contoso-ltd-20261007-1405-1a2b3c4d" -- ASCII only, sortable, unique per run.</summary>
    internal static string FileName(TenantAuditReport r)
    {
        var slug = new string(r.Tenant.DisplayName.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        if (slug.Length == 0) slug = "tenant";
        var stamp = r.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var id = r.RunId.Replace("-", "");
        return $"tenant-audit-{slug}-{stamp}-{id[..Math.Min(8, id.Length)]}";
    }
}
