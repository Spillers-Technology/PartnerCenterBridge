using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Api.GitSync;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core.ConfigSnapshots;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class ConfigSnapshotsControllerTests
{
    [Fact]
    public async Task Diff_rejects_a_run_id_that_belongs_to_a_different_tenant()
    {
        using var db = new TestDb();
        var mine = new Tenant { TenantId = "mine", DisplayName = "Mine" };
        var theirs = new Tenant { TenantId = "theirs", DisplayName = "Theirs" };
        db.Context.AddRange(mine, theirs);

        var mineRun = new ConfigSnapshotRun { TenantId = mine.Id, Operator = "op" };
        var theirsRun = new ConfigSnapshotRun { TenantId = theirs.Id, Operator = "op" };
        db.Context.AddRange(mineRun, theirsRun);
        db.Context.ConfigSnapshotSections.AddRange(
            new ConfigSnapshotSection { RunId = mineRun.Id, SectionId = "ca", SectionName = "CA", ContentJson = "[{\"id\":\"mine\"}]" },
            new ConfigSnapshotSection { RunId = theirsRun.Id, SectionId = "ca", SectionName = "CA", ContentJson = "[{\"id\":\"secret\"}]" });
        await db.Context.SaveChangesAsync();

        var controller = NewController(db);

        // Each run ID is checked independently, so a foreign ID must be rejected regardless of
        // which of the two positions (before/after) it's placed in.
        var foreignAfter = await controller.Diff(mine.Id, mineRun.Id, theirsRun.Id, null, CancellationToken.None);
        var foreignBefore = await controller.Diff(mine.Id, theirsRun.Id, mineRun.Id, null, CancellationToken.None);
        var foreignAfterExport = await controller.ExportDiff(mine.Id, mineRun.Id, theirsRun.Id, null, CancellationToken.None);
        var sameTenant = await controller.Diff(mine.Id, mineRun.Id, mineRun.Id, null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(foreignAfter.Result);
        Assert.IsType<NotFoundObjectResult>(foreignBefore.Result);
        Assert.IsType<NotFoundObjectResult>(foreignAfterExport);
        Assert.IsType<OkObjectResult>(sameTenant.Result);
    }

    [Fact]
    public async Task ExportRun_rejects_a_run_id_that_belongs_to_a_different_tenant()
    {
        using var db = new TestDb();
        var mine = new Tenant { TenantId = "mine", DisplayName = "Mine" };
        var theirs = new Tenant { TenantId = "theirs", DisplayName = "Theirs" };
        db.Context.AddRange(mine, theirs);
        var theirsRun = new ConfigSnapshotRun { TenantId = theirs.Id, Operator = "op" };
        db.Context.Add(theirsRun);
        await db.Context.SaveChangesAsync();

        var controller = NewController(db);

        var result = await controller.ExportRun(mine.Id, theirsRun.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static ConfigSnapshotsController NewController(TestDb db) => new(
        db.Context,
        new FakeTenantAccessService(isSystemAdmin: true),
        new ConfigSnapshotService(db.Context, new ConfigSectionCatalog([]),
            new GitSyncService(Options.Create(new GitSyncOptions()), NullLogger<GitSyncService>.Instance),
            NullLogger<ConfigSnapshotService>.Instance),
        new ConfigSectionCatalog([]));
}
