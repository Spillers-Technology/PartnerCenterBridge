using System.Text.Json;
using PartnerCenterBridge.Core.TenantAudits;
using static PartnerCenterBridge.Graph.TenantAudits.AuditGraphReader;

namespace PartnerCenterBridge.Graph.TenantAudits;

/// <summary>Intune managed devices, compliance policies and tenant device settings.</summary>
internal sealed class GraphAuditDeviceData(TenantGraphRest graph) : IAuditDeviceData
{
    private const string DeviceFields =
        "id,deviceName,operatingSystem,osVersion,complianceState,lastSyncDateTime,isEncrypted,userPrincipalName," +
        "managedDeviceOwnerType,enrolledDateTime,serialNumber,managementAgent";

    public Task<IReadOnlyList<AuditDevice>> GetManagedDevicesAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditDevice>>("graph:devices", () => ReadAsync<IReadOnlyList<AuditDevice>>(graph, ctx, "Intune managed devices",
            ["DeviceManagementManagedDevices.Read.All"], async g =>
            (await g.GetAllAsync($"/deviceManagement/managedDevices?$select={DeviceFields}", ctx.CancellationToken)).Select(d => new AuditDevice(
                Str(d, "id") ?? "", Str(d, "deviceName"), Str(d, "operatingSystem"), Str(d, "osVersion"), Str(d, "complianceState"),
                Date(d, "lastSyncDateTime"), Bool(d, "isEncrypted"), Str(d, "userPrincipalName"), Str(d, "managedDeviceOwnerType"),
                Date(d, "enrolledDateTime"), Str(d, "serialNumber"), Str(d, "managementAgent"))).ToList()));

    public Task<IReadOnlyList<AuditCompliancePolicy>> GetCompliancePoliciesAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditCompliancePolicy>>("graph:compliance-policies", () => ReadAsync<IReadOnlyList<AuditCompliancePolicy>>(graph, ctx,
            "device compliance policies", ["DeviceManagementConfiguration.Read.All"], async g =>
            (await g.GetAllAsync("/deviceManagement/deviceCompliancePolicies?$expand=assignments", ctx.CancellationToken)).Select(p => new AuditCompliancePolicy(
                Str(p, "id") ?? "", Str(p, "displayName") ?? "", Platform(ODataType(p)),
                p.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0)).ToList()));

    public Task<AuditDeviceManagementSettings> GetDeviceManagementSettingsAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:device-settings", () => ReadAsync(graph, ctx, "Intune tenant settings",
            ["DeviceManagementConfiguration.Read.All"], async g =>
        {
            using var doc = await g.GetAsync("/deviceManagement", ctx.CancellationToken);
            return new AuditDeviceManagementSettings(Bool(Obj(doc.RootElement, "settings"), "secureByDefault"));
        }));

    /// <summary>Compliance policy @odata.type -> the platform names <c>DeviceRows.Platform</c> uses.</summary>
    internal static string Platform(string odataType)
    {
        if (odataType.StartsWith("windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (odataType.StartsWith("ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (odataType.StartsWith("android", StringComparison.OrdinalIgnoreCase) || odataType.StartsWith("aosp", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (odataType.StartsWith("macOS", StringComparison.OrdinalIgnoreCase)) return "macOS";
        return "Other";
    }
}

/// <summary>Conditional Access, security defaults, the authorization policy and delegated consent grants.</summary>
internal sealed class GraphAuditSecurityData(TenantGraphRest graph) : IAuditSecurityData
{
    /// <summary>Microsoft's own app-owning tenants: apps owned here are first-party.</summary>
    private static readonly HashSet<string> MicrosoftOwnerTenants = new(StringComparer.OrdinalIgnoreCase)
    {
        "f8cdef31-a31e-4b4a-93e4-5f571e91255a", "72f988bf-86f1-41af-91ab-2d7cd011db47"
    };

    /// <summary>Exclusion ids resolved to names per policy set; more than this are left as ids.</summary>
    private const int MaxNameLookups = 100;

    public Task<AuditConditionalAccess> GetConditionalAccessAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:ca", () => ReadAsync(graph, ctx, "Conditional Access policies", ["Policy.Read.All"], async g =>
        {
            var policies = (await g.GetAllAsync("/identity/conditionalAccess/policies", ctx.CancellationToken)).Select(MapPolicy).ToList();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = policies.SelectMany(p => p.ExcludeUsers.Concat(p.ExcludeGroups))
                .Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNameLookups);
            foreach (var id in ids)
            {
                try
                {
                    using var doc = await g.GetAsync($"/directoryObjects/{Escape(id)}?$select=id,displayName,userPrincipalName", ctx.CancellationToken);
                    var name = Str(doc.RootElement, "displayName");
                    var upn = Str(doc.RootElement, "userPrincipalName");
                    if (name is not null) names[id] = upn is null ? name : $"{name} ({upn})";
                }
                catch (GraphRequestException) { /* name stays an id; the exclusion itself is still reported */ }
            }
            return new AuditConditionalAccess { Policies = policies, Names = names };
        }));

    internal static AuditConditionalAccessPolicy MapPolicy(JsonElement p)
    {
        var conditions = Obj(p, "conditions");
        var users = Obj(conditions, "users");
        var apps = Obj(conditions, "applications");
        var grant = Obj(p, "grantControls");
        return new AuditConditionalAccessPolicy(
            Str(p, "id") ?? "", Str(p, "displayName") ?? "", Str(p, "state") ?? "",
            Strings(users, "includeUsers"), Strings(users, "excludeUsers"),
            Strings(users, "includeGroups"), Strings(users, "excludeGroups"),
            Strings(users, "includeRoles"), Strings(users, "excludeRoles"),
            Obj(users, "excludeGuestsOrExternalUsers").ValueKind == JsonValueKind.Object,
            Strings(apps, "includeApplications"), Strings(conditions, "clientAppTypes"),
            Strings(grant, "builtInControls"), Obj(grant, "authenticationStrength").ValueKind == JsonValueKind.Object);
    }

    public Task<bool> GetSecurityDefaultsEnabledAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:security-defaults", () => ReadAsync(graph, ctx, "the security defaults policy", ["Policy.Read.All"], async g =>
        {
            using var doc = await g.GetAsync("/policies/identitySecurityDefaultsEnforcementPolicy", ctx.CancellationToken);
            return Bool(doc.RootElement, "isEnabled") == true;
        }));

    public Task<AuditAuthorizationPolicy> GetAuthorizationPolicyAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:authorization-policy", () => ReadAsync(graph, ctx, "the authorization policy", ["Policy.Read.All"], async g =>
        {
            using var doc = await g.GetAsync("/policies/authorizationPolicy", ctx.CancellationToken);
            return MapAuthorizationPolicy(doc.RootElement);
        }));

    /// <summary>Beta returns the singleton inside a "value" collection; v1.0 returns it directly. Both are accepted.</summary>
    internal static AuditAuthorizationPolicy MapAuthorizationPolicy(JsonElement root)
    {
        var p = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0] : root;
        var defaults = Obj(p, "defaultUserRolePermissions");
        var consent = Strings(p, "permissionGrantPolicyIdsAssignedToDefaultUserRole");
        if (consent.Count == 0) consent = Strings(defaults, "permissionGrantPoliciesAssigned");
        return new AuditAuthorizationPolicy(
            Str(p, "allowInvitesFrom"),
            Bool(defaults, "allowedToCreateApps"), Bool(defaults, "allowedToCreateTenants"), Bool(defaults, "allowedToCreateSecurityGroups"),
            consent, Str(p, "guestUserRoleId"));
    }

    public Task<IReadOnlyList<AuditPermissionGrant>> GetDelegatedPermissionGrantsAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditPermissionGrant>>("graph:oauth2-grants", () => ReadAsync<IReadOnlyList<AuditPermissionGrant>>(graph, ctx,
            "delegated permission grants", ["Directory.Read.All", "DelegatedPermissionGrant.Read.All"], async g =>
        {
            var grants = await g.GetAllAsync("/oauth2PermissionGrants", ctx.CancellationToken);
            var apps = new Dictionary<string, (string Name, string? Publisher, bool Microsoft)>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in grants.SelectMany(x => new[] { Str(x, "clientId"), Str(x, "resourceId") }).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = await g.GetAsync($"/servicePrincipals/{Escape(id!)}?$select=id,displayName,appOwnerOrganizationId,publisherName,verifiedPublisher", ctx.CancellationToken);
                    var sp = doc.RootElement;
                    var publisher = Str(Obj(sp, "verifiedPublisher"), "displayName") ?? Str(sp, "publisherName");
                    apps[id!] = (Str(sp, "displayName") ?? id!, publisher, MicrosoftOwnerTenants.Contains(Str(sp, "appOwnerOrganizationId") ?? ""));
                }
                catch (GraphRequestException) { apps[id!] = (id!, null, false); }
            }
            return grants.Select(x =>
            {
                var client = apps.GetValueOrDefault(Str(x, "clientId") ?? "", (Str(x, "clientId") ?? "", null, false));
                var resource = apps.GetValueOrDefault(Str(x, "resourceId") ?? "", (Str(x, "resourceId") ?? "", null, false));
                return new AuditPermissionGrant(Str(x, "id") ?? "", Str(x, "clientId") ?? "", client.Name, client.Publisher, client.Microsoft,
                    Str(x, "consentType") ?? "", Str(x, "principalId"), resource.Name,
                    (Str(x, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }).ToList();
        }));
}
