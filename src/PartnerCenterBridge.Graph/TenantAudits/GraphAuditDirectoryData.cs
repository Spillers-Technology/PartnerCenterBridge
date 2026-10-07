using System.Net;
using System.Text.Json;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Core.TenantAudits.Checks;
using PartnerCenterBridge.Graph.Operations;
using static PartnerCenterBridge.Graph.TenantAudits.AuditGraphReader;

namespace PartnerCenterBridge.Graph.TenantAudits;

/// <summary>Users, licenses, directory roles and MFA registration from Microsoft Graph, read once per tenant per run.</summary>
internal sealed class GraphAuditDirectoryData(TenantGraphRest graph) : IAuditDirectoryData
{
    private const string UserFields =
        "id,displayName,userPrincipalName,userType,accountEnabled,createdDateTime,onPremisesSyncEnabled," +
        "externalUserState,externalUserStateChangeDateTime,assignedLicenses";

    public Task<AuditUserSet> GetUsersAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync("graph:users", () => ReadAsync(graph, ctx, "users", ["User.Read.All"], async g =>
        {
            // signInActivity needs AuditLog.Read.All and Entra ID P1/P2, and caps $top at 120.
            // Without it the users still load; checks that need sign-in data report themselves
            // unavailable with the reason recorded here.
            try
            {
                var withSignIn = await g.GetAllAsync($"/users?$select={UserFields},signInActivity&$top=120", ctx.CancellationToken);
                return new AuditUserSet { Users = withSignIn.Select(u => MapUser(u, includeSignIn: true)).ToList() };
            }
            catch (GraphRequestException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                var plain = await g.GetAllAsync($"/users?$select={UserFields}&$top=999", ctx.CancellationToken);
                var (reason, missing) = SignInUnavailable(ex);
                return new AuditUserSet { Users = plain.Select(u => MapUser(u, includeSignIn: false)).ToList(), SignInUnavailableReason = reason, SignInMissing = missing };
            }
        }));

    internal static (string Reason, string[] Missing) SignInUnavailable(GraphRequestException ex)
    {
        var message = GraphErrors.Message(ex);
        if (ex.Status == HttpStatusCode.Forbidden && NeedsLicense(ex))
            return ($"Sign-in activity is only available in tenants with Microsoft Entra ID P1 or P2 (Graph 403: {message}).",
                ["License: Microsoft Entra ID P1 or P2"]);
        if (ex.Status == HttpStatusCode.Forbidden)
            return ($"PCB's access to this tenant cannot read sign-in activity (Graph 403: {message}). It needs AuditLog.Read.All, and the signed-in admin or GDAP role must be allowed to read sign-in reports (for example Reports Reader, Security Reader or Global Reader).",
                ["Graph permission AuditLog.Read.All"]);
        return ($"Microsoft Graph would not return sign-in activity for this tenant (Graph {(int)ex.Status}: {message}).", []);
    }

    internal static AuditUser MapUser(JsonElement u, bool includeSignIn)
    {
        AuditSignInActivity? signIn = null;
        if (includeSignIn)
        {
            var s = Obj(u, "signInActivity");
            signIn = new AuditSignInActivity(
                Date(s, "lastSuccessfulSignInDateTime"), Date(s, "lastSignInDateTime"), Date(s, "lastNonInteractiveSignInDateTime"));
        }
        var skus = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(l => Str(l, "skuId")).Where(s => s is not null).Select(s => s!).ToList()
            : new List<string>();
        return new AuditUser(
            Str(u, "id") ?? "",
            Str(u, "displayName") ?? "",
            Str(u, "userPrincipalName"),
            Str(u, "userType") ?? "Member",
            Bool(u, "accountEnabled") ?? false,
            Date(u, "createdDateTime"),
            Bool(u, "onPremisesSyncEnabled") == true,
            Str(u, "externalUserState"),
            Date(u, "externalUserStateChangeDateTime"),
            skus,
            signIn);
    }

    public Task<IReadOnlyList<AuditSku>> GetSubscribedSkusAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditSku>>("graph:skus", () => ReadAsync<IReadOnlyList<AuditSku>>(graph, ctx, "subscriptions", ["Organization.Read.All"], async g =>
            (await g.GetAllAsync("/subscribedSkus", ctx.CancellationToken)).Select(s =>
            {
                var units = Obj(s, "prepaidUnits");
                return new AuditSku(Str(s, "skuId") ?? "", Str(s, "skuPartNumber") ?? "", Str(s, "capabilityStatus"), Str(s, "appliesTo"),
                    Int(units, "enabled"), Int(units, "warning"), Int(units, "suspended"), Int(units, "lockedOut"), Int(s, "consumedUnits"));
            }).ToList()));

    public Task<IReadOnlyList<AuditRoleMember>> GetDirectoryRoleMembersAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditRoleMember>>("graph:roles", () => ReadAsync<IReadOnlyList<AuditRoleMember>>(graph, ctx, "directory roles",
            ["RoleManagement.Read.Directory", "Directory.Read.All"], async g =>
        {
            var result = new List<AuditRoleMember>();
            // Members are read per role ($expand=members stops at 20 objects).
            var roles = await g.GetAllAsync("/directoryRoles?$select=id,displayName,roleTemplateId", ctx.CancellationToken);
            foreach (var role in roles)
            {
                var roleId = Str(role, "id") ?? "";
                var templateId = Str(role, "roleTemplateId") ?? "";
                var roleName = Str(role, "displayName") ?? templateId;
                var privileged = PrivilegedRoles.IsPrivileged(templateId);
                var members = await g.GetAllAsync($"/directoryRoles/{Escape(roleId)}/members?$select=id,displayName,userPrincipalName", ctx.CancellationToken);
                foreach (var m in members)
                {
                    var type = ODataType(m);
                    var id = Str(m, "id") ?? "";
                    var name = Str(m, "displayName") ?? id;
                    if (type == "group")
                    {
                        var expanded = await GroupUsersAsync(g, id, ctx.CancellationToken);
                        if (expanded is null)
                            result.Add(new(templateId, roleName, privileged, "group", id, name, null, null));
                        else
                            result.AddRange(expanded.Select(u => new AuditRoleMember(templateId, roleName, privileged, "user",
                                Str(u, "id") ?? "", Str(u, "displayName") ?? "", Str(u, "userPrincipalName"), name)));
                    }
                    else
                    {
                        result.Add(new(templateId, roleName, privileged, type == "servicePrincipal" ? "servicePrincipal" : "user",
                            id, name, Str(m, "userPrincipalName"), null));
                    }
                }
            }
            return result;
        }));

    /// <summary>Users in a role-assignable group, or null when the group's members cannot be read.</summary>
    private static async Task<List<JsonElement>?> GroupUsersAsync(AuditGraphReader g, string groupId, CancellationToken ct)
    {
        try
        {
            return await g.GetAllAsync($"/groups/{Escape(groupId)}/transitiveMembers/microsoft.graph.user?$select=id,displayName,userPrincipalName", ct);
        }
        catch (GraphRequestException) { return null; }
    }

    public Task<IReadOnlyList<AuditAuthRegistration>> GetAuthMethodRegistrationsAsync(AuditCheckContext ctx) =>
        ctx.Cache.GetAsync<IReadOnlyList<AuditAuthRegistration>>("graph:auth-registrations", () => ReadAsync<IReadOnlyList<AuditAuthRegistration>>(graph, ctx,
            "the authentication methods registration report", ["AuditLog.Read.All"], async g =>
            (await g.GetAllAsync("/reports/authenticationMethods/userRegistrationDetails", ctx.CancellationToken)).Select(r => new AuditAuthRegistration(
                Str(r, "id") ?? "", Str(r, "userPrincipalName"), Str(r, "userDisplayName"), Str(r, "userType"),
                Bool(r, "isAdmin") == true, Bool(r, "isMfaCapable") == true, Bool(r, "isMfaRegistered") == true,
                Bool(r, "isPasswordlessCapable") == true, Strings(r, "methodsRegistered"))).ToList()));
}
