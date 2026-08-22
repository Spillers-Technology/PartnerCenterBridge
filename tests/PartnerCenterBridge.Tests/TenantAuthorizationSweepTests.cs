using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Controllers;
using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Tests;

public class TenantAuthorizationSweepTests
{
    [Fact]
    public async Task Dashboard_filters_every_tenant_collection_even_for_an_instance_administrator()
    {
        using var db = new TestDb();
        var admin = NewAdmin();
        var allowed = new Tenant { TenantId = "allowed", DisplayName = "Allowed" };
        var hidden = new Tenant { TenantId = "hidden", DisplayName = "Hidden" };
        var template = new AppTemplate
        {
            DisplayName = "App", InstallCommandLine = "install", UninstallCommandLine = "uninstall"
        };
        db.Context.AddRange(admin, allowed, hidden, template);
        db.Context.TenantAccessGrants.Add(new TenantAccessGrant
        {
            UserId = admin.Id,
            TenantId = allowed.Id,
            Role = TenantRole.Operator,
            GrantedByUserId = admin.Id
        });
        db.Context.Deployments.AddRange(
            new Deployment { TenantId = allowed.Id, AppTemplateId = template.Id, Status = DeploymentStatus.Failed },
            new Deployment { TenantId = hidden.Id, AppTemplateId = template.Id, Status = DeploymentStatus.Failed });
        db.Context.WorkflowRuns.AddRange(
            new WorkflowRun { TenantId = allowed.Id, WorkflowId = "wf", WorkflowName = "Allowed run", Succeeded = false },
            new WorkflowRun { TenantId = hidden.Id, WorkflowId = "wf", WorkflowName = "Hidden run", Succeeded = false });
        db.Context.PendingActions.AddRange(
            new PendingAction { TenantId = allowed.Id, ActionType = "x", PayloadJson = "{}", PreviewSummary = "Allowed action" },
            new PendingAction { TenantId = hidden.Id, ActionType = "x", PayloadJson = "{}", PreviewSummary = "Hidden action" });
        await db.Context.SaveChangesAsync();
        var access = NewTenantAccess(db, admin.Id);
        var controller = new DashboardController(db.Context, access);

        var result = await controller.Get(CancellationToken.None);

        Assert.Equal(1, result.Stats.Tenants);
        Assert.Equal(1, result.Stats.Deployments);
        Assert.Equal(1, result.Stats.RunsFailedLast7d);
        Assert.All(result.NeedsAttention, item => Assert.Equal(allowed.Id, item.TenantId));
        Assert.All(result.RecentRuns, run => Assert.Equal(allowed.Id, run.TenantId));
    }

    [Fact]
    public async Task Instance_administrator_without_a_tenant_grant_cannot_call_directory_exchange_or_provisioning()
    {
        using var db = new TestDb();
        var admin = NewAdmin();
        var tenant = new Tenant { TenantId = "hidden", DisplayName = "Hidden" };
        db.Context.AddRange(admin, tenant);
        await db.Context.SaveChangesAsync();
        var access = NewTenantAccess(db, admin.Id);
        var graph = new CountingGraphUserService();
        var exchange = new CountingExchangeService();

        var directoryResult = await new DirectoryController(db.Context, graph, access)
            .Users(tenant.Id, null, CancellationToken.None);
        var exchangeResult = await new ExchangeController(db.Context, exchange, access)
            .RemediateArchive(tenant.Id, "user@example.com", new(), CancellationToken.None);
        var hireResult = await new ProvisioningController(db.Context, graph, exchange, access)
            .Hire(new HireApiRequest(tenant.Id, new NewHireRequest
            {
                DisplayName = "User", UserPrincipalName = "user@example.com", MailNickname = "user"
            }), CancellationToken.None);

        Assert.IsType<ForbidResult>(directoryResult.Result);
        Assert.IsType<ForbidResult>(exchangeResult.Result);
        Assert.IsType<ForbidResult>(hireResult.Result);
        Assert.Equal(0, graph.Calls);
        Assert.Equal(0, exchange.Calls);
    }

    [Fact]
    public async Task Tenant_access_changes_cannot_remove_the_last_permanent_owner()
    {
        using var db = new TestDb();
        var owner = NewAdmin();
        var tenant = new Tenant { TenantId = "tenant", DisplayName = "Tenant" };
        db.Context.AddRange(owner, tenant);
        db.Context.TenantAccessGrants.Add(new TenantAccessGrant
        {
            UserId = owner.Id,
            TenantId = tenant.Id,
            Role = TenantRole.Owner,
            GrantedByUserId = owner.Id
        });
        await db.Context.SaveChangesAsync();
        var controller = new TenantAccessController(db.Context,
            new FakeTenantAccessService(isSystemAdmin: true, currentUserId: owner.Id));

        var revoke = await controller.Revoke(tenant.Id, owner.Id, CancellationToken.None);
        var demote = await controller.Grant(tenant.Id,
            new GrantAccessRequest(owner.Email, TenantRole.Operator, null), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(revoke);
        Assert.IsType<ConflictObjectResult>(demote);
        Assert.Equal(TenantRole.Owner, (await db.Context.TenantAccessGrants.SingleAsync()).Role);
    }

    private static AppUser NewAdmin() => new()
    {
        Email = "admin@example.com",
        DisplayName = "Admin",
        PasswordHash = "hash",
        InstanceRoles = InstanceRole.Administrator
    };

    private static TenantAccessService NewTenantAccess(TestDb db, Guid userId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(LocalTokenService.UserIdClaim, userId.ToString())], "test"))
        };
        return new TenantAccessService(new HttpContextAccessor { HttpContext = http }, db.Context);
    }

    private sealed class CountingGraphUserService : IGraphUserService
    {
        public int Calls { get; private set; }
        private Task<T> Called<T>(T result) { Calls++; return Task.FromResult(result); }
        public Task<ProvisioningResult> CreateUserAsync(Tenant tenant, NewHireRequest request, CancellationToken ct = default) => Called(new ProvisioningResult());
        public Task<ProvisioningResult> TerminateUserAsync(Tenant tenant, TerminationRequest request, CancellationToken ct = default) => Called(new ProvisioningResult());
        public Task<IReadOnlyList<SkuSummary>> ListSkusAsync(Tenant tenant, CancellationToken ct = default) => Called<IReadOnlyList<SkuSummary>>([]);
        public Task<IReadOnlyList<DirectoryObject>> ListGroupsAsync(Tenant tenant, CancellationToken ct = default) => Called<IReadOnlyList<DirectoryObject>>([]);
        public Task<IReadOnlyList<DirectoryObject>> ListUsersAsync(Tenant tenant, string? search = null, CancellationToken ct = default) => Called<IReadOnlyList<DirectoryObject>>([]);
    }

    private sealed class CountingExchangeService : IExchangeOnlineService
    {
        public int Calls { get; private set; }
        private Task<T> Called<T>(T result) { Calls++; return Task.FromResult(result); }
        public Task<MailboxInfo?> GetMailboxAsync(Tenant tenant, string identity, CancellationToken ct = default) => Called<MailboxInfo?>(null);
        public Task<ExoResult> ConvertToSharedAsync(Tenant tenant, string identity, string? forwardingSmtpAddress, bool deliverToMailboxAndForward, CancellationToken ct = default) => Called(new ExoResult());
        public Task<IReadOnlyList<MailboxInfo>> ListSharedMailboxesAsync(Tenant tenant, CancellationToken ct = default) => Called<IReadOnlyList<MailboxInfo>>([]);
        public Task<ArchiveState?> GetArchiveStateAsync(Tenant tenant, string identity, CancellationToken ct = default) => Called<ArchiveState?>(null);
        public Task<ArchiveRemediationResult> RemediateArchiveAsync(Tenant tenant, string identity, ArchiveRemediationOptions options, CancellationToken ct = default) => Called(new ArchiveRemediationResult());
        public Task<ArchiveRemediationResult> NudgeArchiveAsync(Tenant tenant, string identity, CancellationToken ct = default) => Called(new ArchiveRemediationResult());
    }
}
