using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Reconcile;

public enum ReconcileAction
{
    /// <summary>Template is not yet deployed to the tenant; create + upload.</summary>
    Deploy,
    /// <summary>Deployed, but the template's content version is newer than what is committed.</summary>
    Update,
    /// <summary>A prior deployment failed and should be retried.</summary>
    Retry,
    /// <summary>Committed version matches the template; nothing to do.</summary>
    UpToDate
}

/// <summary>One planned unit of work for a (tenant, template) pair.</summary>
public record ReconcileItem(Tenant Tenant, AppTemplate Template, ReconcileAction Action, Deployment? Existing);

/// <summary>
/// Pure diff of desired state (a contract's app templates) against recorded deployments. Produces
/// the set of actions needed to bring every tenant on a contract to the contract's desired state.
/// No I/O — deliberately trivial to unit test.
/// </summary>
public static class DesiredStateReconciler
{
    /// <summary>
    /// Plan the work to reconcile <paramref name="tenants"/> to <paramref name="desiredApps"/>.
    /// <paramref name="existingDeployments"/> is every known deployment for those tenants/templates.
    /// </summary>
    public static IReadOnlyList<ReconcileItem> Plan(
        IEnumerable<Tenant> tenants,
        IEnumerable<AppTemplate> desiredApps,
        IEnumerable<Deployment> existingDeployments)
    {
        var apps = desiredApps.ToList();
        var byKey = existingDeployments
            .GroupBy(d => (d.TenantId, d.AppTemplateId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedAt).First());

        var plan = new List<ReconcileItem>();
        foreach (var tenant in tenants)
        {
            // Only reconcile tenants we can actually act in.
            if (tenant.Status is TenantStatus.NoDelegation or TenantStatus.Removed)
                continue;

            foreach (var app in apps)
            {
                byKey.TryGetValue((tenant.Id, app.Id), out var existing);
                plan.Add(new ReconcileItem(tenant, app, Decide(app, existing), existing));
            }
        }
        return plan;
    }

    private static ReconcileAction Decide(AppTemplate app, Deployment? existing)
    {
        if (existing is null)
            return ReconcileAction.Deploy;

        if (existing.Status == DeploymentStatus.Failed)
            return ReconcileAction.Retry;

        return existing.DeployedTemplateVersion < app.ContentVersion
            ? ReconcileAction.Update
            : ReconcileAction.UpToDate;
    }
}
