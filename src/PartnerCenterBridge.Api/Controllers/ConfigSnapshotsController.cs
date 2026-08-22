using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>
/// Config snapshots: point-in-time captures of a tenant's configuration (Conditional Access,
/// device compliance, etc. -- see <see cref="ConfigSectionCatalog"/>), diffable against each other
/// section-by-section or whole-tenant, and exportable/importable as a portable "workbook" file.
/// <para/>
/// Deliberately one-directional: there is no "apply this diff/workbook to a tenant" endpoint.
/// Import only ever creates a new local record from already-captured data (for comparison, or
/// moving a snapshot between instances) -- it never issues a single write against live Graph.
/// Making changes to a tenant stays the job of the Deploy wizard and the known-fix workflows,
/// where every write is a reviewed, single, named action. A generic "replay this JSON at a
/// tenant" endpoint would be an unreviewable, un-auditable way to mutate a customer's identity
/// and device configuration -- out of scope on purpose, not an oversight.
/// </summary>
[ApiController]
[Route("api/tenants/{tenantId:guid}/config-snapshots")]
[Authorize]
public class ConfigSnapshotsController : ControllerBase
{
    private readonly BridgeDbContext _db;
    private readonly ITenantAccessService _access;
    private readonly ConfigSnapshotService _snapshots;
    private readonly ConfigSectionCatalog _catalog;

    public ConfigSnapshotsController(BridgeDbContext db, ITenantAccessService access, ConfigSnapshotService snapshots, ConfigSectionCatalog catalog)
    {
        _db = db;
        _access = access;
        _snapshots = snapshots;
        _catalog = catalog;
    }

    /// <summary>The registered sections every snapshot captures -- not tenant-scoped, just the catalog.</summary>
    [HttpGet("~/api/config-sections")]
    public ActionResult<IReadOnlyList<ConfigSectionDto>> Sections() =>
        Ok(_catalog.All.Select(s => new ConfigSectionDto(s.Id, s.Name, s.Category)).ToList());

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConfigSnapshotRunDto>>> List(Guid tenantId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var runs = await _db.ConfigSnapshotRuns.AsNoTracking()
            .Include(r => r.Sections)
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct);
        return Ok(runs.Select(ConfigSnapshotRunDto.From).ToList());
    }

    /// <summary>Take a snapshot now -- runs every registered section against live Graph for this tenant.</summary>
    [HttpPost]
    public async Task<ActionResult<ConfigSnapshotRunDto>> Capture(Guid tenantId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct)) return Forbid();
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);
        if (tenant is null) return NotFound();

        var run = await _snapshots.CaptureAsync(tenant, User.Identity?.Name ?? "anonymous", ct);
        return Ok(ConfigSnapshotRunDto.From(run));
    }

    [HttpGet("diff")]
    public async Task<ActionResult<IReadOnlyList<SectionDiffDto>>> Diff(
        Guid tenantId, [FromQuery] Guid beforeRunId, [FromQuery] Guid afterRunId, [FromQuery] string? sectionId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var diffs = await BuildDiffsAsync(tenantId, beforeRunId, afterRunId, sectionId, ct);
        if (diffs is null) return NotFound("One or both runs not found.");
        return Ok(diffs.Select(SectionDiffDto.From).ToList());
    }

    /// <summary>Same diff, rendered as a downloadable patch-style text file -- the "export a workbook of what changed" path.</summary>
    [HttpGet("diff/export")]
    public async Task<IActionResult> ExportDiff(
        Guid tenantId, [FromQuery] Guid beforeRunId, [FromQuery] Guid afterRunId, [FromQuery] string? sectionId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var diffs = await BuildDiffsAsync(tenantId, beforeRunId, afterRunId, sectionId, ct);
        if (diffs is null) return NotFound("One or both runs not found.");

        var text = ConfigDiffFormatter.ToPatchText(diffs);
        return File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain", $"config-diff-{beforeRunId:N}-{afterRunId:N}.patch");
    }

    /// <summary>Export one run's full captured content as a portable workbook file.</summary>
    [HttpGet("{runId:guid}/export")]
    public async Task<IActionResult> ExportRun(Guid tenantId, Guid runId, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Viewer, ct)) return Forbid();
        var run = await _db.ConfigSnapshotRuns.AsNoTracking().Include(r => r.Tenant).Include(r => r.Sections)
            .FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == tenantId, ct);
        if (run is null) return NotFound();

        var workbook = new ConfigWorkbookDto(
            run.Tenant?.DisplayName ?? "", run.StartedAt, run.Operator,
            run.Sections.Select(s => new ConfigWorkbookSectionDto(s.SectionId, s.SectionName, s.ContentJson)).ToList());
        // Explicit camelCase here -- this bypasses the MVC JSON formatter (it's a raw file
        // download, not an action result), so it doesn't inherit the app's configured naming
        // policy automatically the way every other endpoint's response does.
        var json = System.Text.Json.JsonSerializer.Serialize(workbook, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"config-snapshot-{runId:N}.json");
    }

    /// <summary>
    /// Create a new run from a previously exported workbook -- for comparing a snapshot captured
    /// elsewhere (a different instance, an offline export) against this tenant's history. Pure
    /// data import: no Graph call, no write of any kind against the tenant.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<ConfigSnapshotRunDto>> Import(Guid tenantId, ImportWorkbookRequest req, CancellationToken ct)
    {
        if (!await _access.HasRoleAsync(tenantId, TenantRole.Operator, ct)) return Forbid();
        if (req.Sections.Count == 0) return BadRequest("Workbook has no sections.");

        var run = new ConfigSnapshotRun
        {
            TenantId = tenantId,
            Operator = User.Identity?.Name ?? "anonymous",
            Imported = true,
            CompletedAt = DateTimeOffset.UtcNow,
            Succeeded = true
        };
        foreach (var s in req.Sections)
        {
            var itemCount = (System.Text.Json.Nodes.JsonNode.Parse(s.ContentJson) as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
            run.Sections.Add(new ConfigSnapshotSection
            {
                Run = run, RunId = run.Id, SectionId = s.SectionId, SectionName = s.SectionName,
                ContentJson = s.ContentJson, ItemCount = itemCount
            });
        }
        _db.ConfigSnapshotRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return Ok(ConfigSnapshotRunDto.From(run));
    }

    /// <summary>
    /// Both run IDs are confirmed to belong to <paramref name="tenantId"/> before any section
    /// content is read -- the caller only authorizes the route's own tenantId, so a run ID from a
    /// different tenant must never resolve here, even to a "not found" vs "forbidden" timing tell.
    /// </summary>
    private async Task<List<SectionDiff>?> BuildDiffsAsync(Guid tenantId, Guid beforeRunId, Guid afterRunId, string? sectionId, CancellationToken ct)
    {
        if (!await _db.ConfigSnapshotRuns.AnyAsync(r => r.Id == beforeRunId && r.TenantId == tenantId, ct)) return null;
        if (!await _db.ConfigSnapshotRuns.AnyAsync(r => r.Id == afterRunId && r.TenantId == tenantId, ct)) return null;

        var before = await _db.ConfigSnapshotSections.AsNoTracking().Where(s => s.RunId == beforeRunId).ToListAsync(ct);
        var after = await _db.ConfigSnapshotSections.AsNoTracking().Where(s => s.RunId == afterRunId).ToListAsync(ct);

        var sectionIds = sectionId is not null
            ? [sectionId]
            : before.Select(s => s.SectionId).Union(after.Select(s => s.SectionId)).Distinct();

        var diffs = new List<SectionDiff>();
        foreach (var sid in sectionIds)
        {
            var b = before.FirstOrDefault(s => s.SectionId == sid);
            var a = after.FirstOrDefault(s => s.SectionId == sid);
            diffs.Add(ConfigDiffer.Diff(sid, a?.SectionName ?? b?.SectionName ?? sid, b?.ContentJson ?? "[]", a?.ContentJson ?? "[]"));
        }
        return diffs;
    }
}
