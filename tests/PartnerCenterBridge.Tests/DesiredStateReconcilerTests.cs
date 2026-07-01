using PartnerCenterBridge.Core;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Core.Reconcile;

namespace PartnerCenterBridge.Tests;

public class DesiredStateReconcilerTests
{
    private static Tenant Tenant(TenantStatus status = TenantStatus.Active) =>
        new() { TenantId = Guid.NewGuid().ToString(), DisplayName = "T", Status = status };

    private static AppTemplate Template(int version = 1) =>
        new() { DisplayName = "App", InstallCommandLine = "i", UninstallCommandLine = "u", ContentVersion = version };

    [Fact]
    public void Deploy_when_no_existing_deployment()
    {
        var t = Tenant();
        var app = Template();

        var plan = DesiredStateReconciler.Plan(new[] { t }, new[] { app }, Array.Empty<Deployment>());

        var item = Assert.Single(plan);
        Assert.Equal(ReconcileAction.Deploy, item.Action);
    }

    [Fact]
    public void UpToDate_when_deployed_version_matches()
    {
        var t = Tenant();
        var app = Template(version: 3);
        var dep = new Deployment
        {
            TenantId = t.Id, AppTemplateId = app.Id, DeployedTemplateVersion = 3, Status = DeploymentStatus.Succeeded
        };

        var plan = DesiredStateReconciler.Plan(new[] { t }, new[] { app }, new[] { dep });

        Assert.Equal(ReconcileAction.UpToDate, plan.Single().Action);
    }

    [Fact]
    public void Update_when_template_version_is_newer()
    {
        var t = Tenant();
        var app = Template(version: 5);
        var dep = new Deployment
        {
            TenantId = t.Id, AppTemplateId = app.Id, DeployedTemplateVersion = 4, Status = DeploymentStatus.Succeeded
        };

        var plan = DesiredStateReconciler.Plan(new[] { t }, new[] { app }, new[] { dep });

        Assert.Equal(ReconcileAction.Update, plan.Single().Action);
    }

    [Fact]
    public void Retry_when_prior_deployment_failed()
    {
        var t = Tenant();
        var app = Template();
        var dep = new Deployment
        {
            TenantId = t.Id, AppTemplateId = app.Id, DeployedTemplateVersion = 1, Status = DeploymentStatus.Failed
        };

        var plan = DesiredStateReconciler.Plan(new[] { t }, new[] { app }, new[] { dep });

        Assert.Equal(ReconcileAction.Retry, plan.Single().Action);
    }

    [Fact]
    public void Skips_tenants_without_delegation()
    {
        var ok = Tenant();
        var noDelegation = Tenant(TenantStatus.NoDelegation);
        var app = Template();

        var plan = DesiredStateReconciler.Plan(new[] { ok, noDelegation }, new[] { app }, Array.Empty<Deployment>());

        Assert.Equal(ok.Id, Assert.Single(plan).Tenant.Id);
    }
}
