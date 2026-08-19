using Microsoft.AspNetCore.Mvc;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class AppTemplatesControllerTests
{
    private static AppTemplate MakeTemplate() => new()
    {
        DisplayName = "Original Name",
        Description = "Original description",
        Publisher = "Original Publisher",
        InstallCommandLine = "install.exe",
        UninstallCommandLine = "uninstall.exe"
    };

    [Fact]
    public async Task Update_as_system_admin_edits_metadata_and_bumps_UpdatedAt()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var originalUpdatedAt = template.UpdatedAt;
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.Update(template.Id, new UpdateAppTemplateRequest(
            "New Name", "New description", "New Publisher", "new-install.exe", "new-uninstall.exe"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AppTemplateDto>(ok.Value);
        Assert.Equal("New Name", dto.DisplayName);
        Assert.Equal("New description", dto.Description);
        Assert.Equal("New Publisher", dto.Publisher);
        Assert.Equal("new-install.exe", dto.InstallCommandLine);
        Assert.Equal("new-uninstall.exe", dto.UninstallCommandLine);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.AppTemplates.FindAsync(template.Id);
        Assert.Equal("New Name", persisted!.DisplayName);
        Assert.True(persisted.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task Update_never_touches_ContentVersion()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        template.ContentVersion = 3;
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        await controller.Update(template.Id, new UpdateAppTemplateRequest(
            "New Name", null, null, "install.exe", "uninstall.exe"), CancellationToken.None);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.AppTemplates.FindAsync(template.Id);
        Assert.Equal(3, persisted!.ContentVersion);
    }

    [Fact]
    public async Task Update_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.Update(template.Id, new UpdateAppTemplateRequest(
            "New Name", null, null, "install.exe", "uninstall.exe"), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.AppTemplates.FindAsync(template.Id);
        Assert.Equal("Original Name", persisted!.DisplayName);
    }

    [Fact]
    public async Task Update_returns_NotFound_for_an_unknown_id()
    {
        using var db = new TestDb();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.Update(Guid.NewGuid(), new UpdateAppTemplateRequest(
            "New Name", null, null, "install.exe", "uninstall.exe"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_as_system_admin_removes_an_unreferenced_template()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.Delete(template.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        using var verifyContext = db.CreateContext();
        Assert.Null(await verifyContext.AppTemplates.FindAsync(template.Id));
    }

    [Fact]
    public async Task Delete_refuses_a_template_with_deployment_history()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        var tenant = new Tenant { TenantId = "t", DisplayName = "Test Tenant" };
        db.Context.AppTemplates.Add(template);
        db.Context.Tenants.Add(tenant);
        await db.Context.SaveChangesAsync();
        db.Context.Deployments.Add(new Deployment { AppTemplateId = template.Id, TenantId = tenant.Id });
        await db.Context.SaveChangesAsync();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.Delete(template.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        using var verifyContext = db.CreateContext();
        Assert.NotNull(await verifyContext.AppTemplates.FindAsync(template.Id));
    }

    [Fact]
    public async Task Delete_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new AppTemplatesController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.Delete(template.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        using var verifyContext = db.CreateContext();
        Assert.NotNull(await verifyContext.AppTemplates.FindAsync(template.Id));
    }
}
