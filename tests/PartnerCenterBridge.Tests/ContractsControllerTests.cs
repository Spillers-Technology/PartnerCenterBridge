using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class ContractsControllerTests
{
    private static Contract MakeContract() => new() { Name = "Contoso baseline" };
    private static AppTemplate MakeTemplate(string name = "Defender") => new()
    {
        DisplayName = name,
        InstallCommandLine = "install.exe",
        UninstallCommandLine = "uninstall.exe",
        Content = new Win32ContentInfo
        {
            FileName = "app.intunewin",
            Size = 1,
            SizeEncrypted = 1,
            EncryptionKey = "key",
            MacKey = "mac-key",
            InitializationVector = "iv",
            Mac = "mac",
            ProfileIdentifier = "profile",
            FileDigest = "digest",
            FileDigestAlgorithm = "SHA256"
        }
    };

    [Fact]
    public async Task AddDesiredApp_as_system_admin_adds_the_template_and_returns_updated_dto()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Contains(template.Id, dto.DesiredAppIds);
        Assert.Equal(1, dto.DesiredAppCount);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Contains(persisted.DesiredApps, a => a.Id == template.Id);
    }

    [Fact]
    public async Task AddDesiredApp_is_idempotent_for_an_already_desired_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(1, dto.DesiredAppCount);
    }

    [Fact]
    public async Task AddDesiredApp_preserves_membership_in_another_contract()
    {
        using var db = new TestDb();
        var firstContract = MakeContract();
        var secondContract = new Contract { Name = "Fabrikam baseline" };
        var template = MakeTemplate();
        firstContract.DesiredApps.Add(template);
        db.Context.Contracts.AddRange(firstContract, secondContract);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(secondContract.Id, template.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var memberships = await verifyContext.Contracts
            .Include(c => c.DesiredApps)
            .Where(c => c.Id == firstContract.Id || c.Id == secondContract.Id)
            .ToListAsync();
        Assert.All(memberships, contract =>
            Assert.Contains(contract.DesiredApps, app => app.Id == template.Id));
    }

    [Fact]
    public async Task AddDesiredApp_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Empty(persisted.DesiredApps);
    }

    [Fact]
    public async Task AddDesiredApp_returns_NotFound_for_an_unknown_contract()
    {
        using var db = new TestDb();
        var template = MakeTemplate();
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(Guid.NewGuid(), template.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddDesiredApp_returns_NotFound_for_an_unknown_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        db.Context.Contracts.Add(contract);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddDesiredApp_rejects_a_template_without_a_package()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        template.Content = null;
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.AddDesiredApp(contract.Id, template.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps)
            .SingleAsync(c => c.Id == contract.Id);
        Assert.Empty(persisted.DesiredApps);
    }

    [Fact]
    public async Task RemoveDesiredApp_as_system_admin_removes_the_template_and_returns_updated_dto()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.DoesNotContain(template.Id, dto.DesiredAppIds);
        Assert.Equal(0, dto.DesiredAppCount);

        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.Empty(persisted.DesiredApps);
    }

    [Fact]
    public async Task RemoveDesiredApp_is_idempotent_for_an_absent_template()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(0, dto.DesiredAppCount);
    }

    [Fact]
    public async Task RemoveDesiredApp_rejects_a_non_system_admin_caller()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: false));

        var result = await controller.RemoveDesiredApp(contract.Id, template.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        using var verifyContext = db.CreateContext();
        var persisted = await verifyContext.Contracts.Include(c => c.DesiredApps).FirstAsync(c => c.Id == contract.Id);
        Assert.NotEmpty(persisted.DesiredApps);
    }

    [Fact]
    public async Task RemoveDesiredApp_returns_NotFound_for_an_unknown_contract()
    {
        using var db = new TestDb();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var result = await controller.RemoveDesiredApp(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_reflects_DesiredAppIds()
    {
        using var db = new TestDb();
        var contract = MakeContract();
        var template = MakeTemplate();
        contract.DesiredApps.Add(template);
        db.Context.Contracts.Add(contract);
        db.Context.AppTemplates.Add(template);
        await db.Context.SaveChangesAsync();
        var controller = new ContractsController(db.Context, new FakeTenantAccessService(isSystemAdmin: true));

        var list = await controller.List(CancellationToken.None);

        Assert.Contains(template.Id, list.Single(c => c.Id == contract.Id).DesiredAppIds);
    }
}
