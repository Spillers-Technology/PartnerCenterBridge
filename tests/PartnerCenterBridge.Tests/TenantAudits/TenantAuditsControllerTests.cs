using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Api.TenantAudits;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.TenantAudits;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class TenantAuditsControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private TenantAuditsController Controller(TenantRole? role = TenantRole.Viewer, params ITenantAuditCheck[] checks) => ControllerAt(Now, role, checks);

    private TenantAuditsController ControllerAt(DateTimeOffset at, TenantRole? role, params ITenantAuditCheck[] checks) =>
        OpsTest.WithHttp(new TenantAuditsController(_db.Context, new RoleAccess(role), new TenantAuditCatalog(checks),
            new TenantAuditService(_db.Context, new FakeCurrentActor(), new FixedClock(at))));

    private static TenantAuditEngineTests.LambdaCheck Warns(string id = "warns") => new(id, _ => Task.FromResult(new AuditCheckOutput().Add(new AuditFinding
    {
        Id = $"{id}:x", Severity = AuditSeverity.Warn, Title = "Something", Summary = "1 thing",
        Subjects = [new AuditSubject { Type = "user", Id = "u1", Name = "Ada", Upn = "ada@contoso.com" }], SubjectCount = 1
    })));

    private static TenantAuditEngineTests.LambdaCheck Unavailable(string id = "gone") =>
        new(id, _ => throw new AuditUnavailableException("no access", ["Graph permission X"]), AuditCategories.Security);

    private static JsonElement Json(IActionResult result) =>
        JsonDocument.Parse(Assert.IsType<ContentResult>(result).Content!).RootElement;

    [Fact]
    public async Task A_viewer_can_run_an_audit_which_is_stored_with_its_counts()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var controller = Controller(TenantRole.Viewer, Warns(), Unavailable());

        var report = Json(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None));

        Assert.Equal("Full tenant health check", report.GetProperty("auditName").GetString());
        Assert.Equal("Unavailable", report.GetProperty("checks")[1].GetProperty("status").GetString());
        var run = Assert.Single(_db.Context.TenantAuditRuns);
        Assert.Equal((AuditHealth.AttentionNeeded, 1, 1, 1, "test actor"), (run.Health, run.WarnCount, run.ChecksCompleted, run.ChecksUnavailable, run.Operator));
        Assert.Equal(TenantAuditReport.CurrentSchemaVersion, run.SchemaVersion);
        Assert.DoesNotContain("token", run.ReportJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_a_grant_nothing_runs_or_reads()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var check = Warns();
        var controller = Controller(null, check);

        Assert.IsType<ForbidResult>(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None));
        Assert.IsType<ForbidResult>((await controller.List(tenant.Id, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>(await controller.Export(tenant.Id, Guid.NewGuid(), "csv", CancellationToken.None));
        Assert.Equal(0, check.Calls);
    }

    [Fact]
    public async Task Bad_selection_is_a_400_with_the_reason()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);

        var result = await Controller(TenantRole.Viewer, Warns()).Run(tenant.Id,
            new RunTenantAuditRequest(["nope"], null, null), CancellationToken.None);

        Assert.Contains("Unknown audit check", Assert.IsType<BadRequestObjectResult>(result).Value as string);
        Assert.Empty(_db.Context.TenantAuditRuns);
    }

    [Fact]
    public async Task History_lists_newest_first_and_get_returns_the_stored_report()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var controller = Controller(TenantRole.Viewer, Warns());
        var first = Json(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None));
        var second = Json(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None));

        var list = Assert.IsType<OkObjectResult>((await controller.List(tenant.Id, CancellationToken.None)).Result).Value as IReadOnlyList<TenantAuditRunSummaryDto>;
        var again = Json(await controller.Get(tenant.Id, Guid.Parse(second.GetProperty("runId").GetString()!), CancellationToken.None));

        Assert.Equal(2, list!.Count);
        Assert.Equal("Contoso", list[0].TenantName);
        Assert.Equal(second.GetProperty("runId").GetString(), again.GetProperty("runId").GetString());
        Assert.NotEqual(first.GetProperty("runId").GetString(), again.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_run_from_another_tenant_is_not_found()
    {
        var mine = await OpsTest.AddTenantAsync(_db, "Mine");
        var theirs = await OpsTest.AddTenantAsync(_db, "Theirs");
        var controller = Controller(TenantRole.Viewer, Warns());
        var theirRun = Guid.Parse(Json(await controller.Run(theirs.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None)).GetProperty("runId").GetString()!);

        Assert.IsType<NotFoundResult>(await controller.Get(mine.Id, theirRun, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Export(mine.Id, theirRun, "json", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "text/csv; charset=utf-8", ".csv", "AuditRunId,")]
    [InlineData("csv", "text/csv; charset=utf-8", ".csv", "AuditRunId,")]
    [InlineData("json", "application/json", ".json", "\"schemaVersion\"")]
    [InlineData("markdown", "text/markdown; charset=utf-8", ".md", "# Full tenant health check")]
    public async Task Exports_in_each_format(string? format, string contentType, string extension, string contains)
    {
        var tenant = await OpsTest.AddTenantAsync(_db, "Contoso, Ltd.");
        var controller = Controller(TenantRole.Viewer, Warns());
        var runId = Guid.Parse(Json(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None)).GetProperty("runId").GetString()!);

        var file = Assert.IsType<FileContentResult>(await controller.Export(tenant.Id, runId, format, CancellationToken.None));

        Assert.Equal(contentType, file.ContentType);
        Assert.StartsWith("tenant-audit-contoso-ltd-20261001-1200-", file.FileDownloadName);
        Assert.EndsWith(extension, file.FileDownloadName);
        Assert.Contains(contains, Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task Unknown_export_format_is_rejected()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var controller = Controller(TenantRole.Viewer, Warns());
        var runId = Guid.Parse(Json(await controller.Run(tenant.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None)).GetProperty("runId").GetString()!);

        Assert.IsType<BadRequestObjectResult>(await controller.Export(tenant.Id, runId, "xlsx", CancellationToken.None));
    }

    [Fact]
    public async Task Estate_rolls_up_the_latest_run_of_each_tenant()
    {
        var a = await OpsTest.AddTenantAsync(_db, "Alpha");
        var b = await OpsTest.AddTenantAsync(_db, "Bravo");
        await OpsTest.AddTenantAsync(_db, "Charlie");
        await Controller(TenantRole.Viewer, Unavailable()).Run(a.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None);
        await ControllerAt(Now.AddMinutes(5), TenantRole.Viewer, Warns()).Run(a.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None);
        await Controller(TenantRole.Viewer, Unavailable()).Run(b.Id, new RunTenantAuditRequest(null, null, null), CancellationToken.None);

        var estate = Assert.IsType<OkObjectResult>((await Controller(TenantRole.Viewer).Estate(CancellationToken.None)).Result).Value as AuditEstateDto;

        Assert.Equal((2, 1, 1, 1), (estate!.Summary.TenantsScanned, estate.Summary.AttentionNeeded, estate.Summary.InsufficientAccess, estate.TenantsWithoutAudit));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], estate.Tenants.Select(t => t.TenantName));
        Assert.Equal(AuditHealth.AttentionNeeded, estate.Tenants[0].LatestRun!.Summary.Health);
    }

    [Fact]
    public void Catalog_lists_checks_presets_and_parameters()
    {
        var catalog = Assert.IsType<OkObjectResult>(Controller(TenantRole.Viewer, Warns()).Catalog().Result).Value as AuditCatalogDto;

        Assert.Equal(AuditCategories.All, catalog!.Categories.Select(c => c.Id));
        Assert.Equal("warns", Assert.Single(catalog.Checks).Id);
        Assert.Equal("full", catalog.Presets[0].Id);
    }
}

public class ConfigDriftCheckTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private sealed class LiveSection(string id, string name, string json) : IConfigSection
    {
        public string Id => id;
        public string Name => name;
        public string Category => "Identity";
        public Task<string> CaptureAsync(Tenant tenant, CancellationToken ct) => Task.FromResult(json);
    }

    private async Task<Tenant> TenantWithSnapshotAsync(string caJson)
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var run = new ConfigSnapshotRun { TenantId = tenant.Id, Operator = "op", StartedAt = Now.AddDays(-10), Succeeded = true };
        run.Sections.Add(new ConfigSnapshotSection { RunId = run.Id, SectionId = "conditional-access-policies", SectionName = "Conditional Access Policies", ContentJson = caJson });
        _db.Context.ConfigSnapshotRuns.Add(run);
        await _db.Context.SaveChangesAsync();
        return tenant;
    }

    private AuditCheckContext Ctx(Tenant t) => new(t, AuditParameters.Defaults, Now, CancellationToken.None);

    [Fact]
    public async Task Without_a_snapshot_the_check_says_how_to_get_one()
    {
        var tenant = await OpsTest.AddTenantAsync(_db);
        var check = new ConfigDriftCheck(_db.Context, new ConfigSectionCatalog([]));

        var r = await TenantAuditEngine.RunCheckAsync(check, Ctx(tenant), CancellationToken.None);

        Assert.Equal(AuditCheckStatus.Unavailable, r.Status);
        Assert.Contains("Tenants > Snapshots", r.StatusReason);
    }

    [Fact]
    public async Task Removed_conditional_access_policy_warns_and_edits_are_listed()
    {
        var tenant = await TenantWithSnapshotAsync("""[{"id":"p1","displayName":"MFA all","state":"enabled"},{"id":"p2","displayName":"Block legacy","state":"enabled"}]""");
        var live = new LiveSection("conditional-access-policies", "Conditional Access Policies",
            """[{"id":"p1","displayName":"MFA all","state":"enabledForReportingButNotEnforced"}]""");
        var check = new ConfigDriftCheck(_db.Context, new ConfigSectionCatalog([live]));

        var r = await TenantAuditEngine.RunCheckAsync(check, Ctx(tenant), CancellationToken.None);

        Assert.Equal(["Block legacy"], Names(Finding(r, "ca-removed")));
        var changed = Assert.Single(Finding(r, "changes").Subjects);
        Assert.Equal(("MFA all", "Modified", "state"), (changed.Name, changed.Properties["change"], changed.Properties["fields"]));
    }

    [Fact]
    public async Task Matching_configuration_passes()
    {
        const string json = """[{"id":"p1","displayName":"MFA all","state":"enabled"}]""";
        var tenant = await TenantWithSnapshotAsync(json);
        var check = new ConfigDriftCheck(_db.Context, new ConfigSectionCatalog([new LiveSection("conditional-access-policies", "CA", json)]));

        var r = await TenantAuditEngine.RunCheckAsync(check, Ctx(tenant), CancellationToken.None);

        Assert.Equal(AuditSeverity.Pass, r.Findings.Single().Severity);
    }
}
