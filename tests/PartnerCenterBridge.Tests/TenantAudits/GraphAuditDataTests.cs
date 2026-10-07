using Microsoft.Extensions.Options;
using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Graph;
using PartnerCenterBridge.Graph.TenantAudits;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class GraphAuditDataTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    private TenantGraphRest Rest() => new(new FakeTokenProvider(), new SingleHttpClientFactory(),
        Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! }));

    private void Stub(string path, object body, int status = 200, Func<IRequestBuilder, IRequestBuilder>? match = null) =>
        _server.Given((match ?? (r => r))(Request.Create().WithPath(path).UsingGet()))
            .RespondWith(Response.Create().WithStatusCode(status).WithBodyAsJson(body));

    private static object GraphError(string code, string message) => new { error = new { code, message } };

    private static readonly object SignInUser = new
    {
        id = "u1", displayName = "Ada Lovelace", userPrincipalName = "ada@contoso.com", userType = "Member", accountEnabled = true,
        createdDateTime = "2024-01-02T03:04:05Z", onPremisesSyncEnabled = (bool?)null,
        assignedLicenses = new[] { new { skuId = E3, disabledPlans = Array.Empty<string>() } },
        signInActivity = new
        {
            lastSignInDateTime = "2026-09-01T10:00:00Z",
            lastNonInteractiveSignInDateTime = "2026-09-20T10:00:00Z",
            lastSuccessfulSignInDateTime = "2026-08-15T10:00:00Z"
        }
    };

    [Fact]
    public async Task Users_map_sign_in_fields_without_mixing_them_up()
    {
        Stub("/users", new { value = new[] { SignInUser } }, match: r => r.WithParam("$top", "120"));

        var set = await new GraphAuditDirectoryData(Rest()).GetUsersAsync(Context());

        Assert.True(set.SignInAvailable);
        var u = Assert.Single(set.Users);
        Assert.Equal(("u1", "ada@contoso.com", true, false), (u.Id, u.UserPrincipalName, u.AccountEnabled, u.IsGuest));
        Assert.Equal([E3], u.AssignedSkuIds);
        Assert.Equal(DateTimeOffset.Parse("2026-08-15T10:00:00Z"), u.SignIn!.LastSuccessful);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), u.SignIn.LastInteractive);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T10:00:00Z"), u.SignIn.LastNonInteractive);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T10:00:00Z"), u.SignIn.LastAttempt);
    }

    [Fact]
    public async Task Users_follow_paging()
    {
        _server.Given(Request.Create().WithPath("/users").WithParam("$top", "120").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(
                $$"""{"value":[{"id":"u1","displayName":"A"}],"@odata.nextLink":"{{_server.Url}}/users/page2"}"""));
        Stub("/users/page2", new { value = new[] { new { id = "u2", displayName = "B" } } });

        var set = await new GraphAuditDirectoryData(Rest()).GetUsersAsync(Context());

        Assert.Equal(["u1", "u2"], set.Users.Select(u => u.Id));
    }

    [Fact]
    public async Task Without_a_premium_license_users_still_load_and_sign_in_is_marked_unavailable_with_why()
    {
        Stub("/users", GraphError("Authentication_RequestFromNonPremiumTenantOrB2CTenant", "Neither tenant is B2C or tenant doesn't have premium license"),
            status: 403, match: r => r.WithParam("$top", "120"));
        Stub("/users", new { value = new[] { new { id = "u1", displayName = "Ada", accountEnabled = true } } },
            match: r => r.WithParam("$top", "999"));

        var set = await new GraphAuditDirectoryData(Rest()).GetUsersAsync(Context());

        Assert.Single(set.Users);
        Assert.Null(set.Users[0].SignIn);
        Assert.False(set.SignInAvailable);
        Assert.Contains("Entra ID P1 or P2", set.SignInUnavailableReason);
        Assert.Equal(["License: Microsoft Entra ID P1 or P2"], set.SignInMissing);
        var ex = Assert.Throws<AuditUnavailableException>(set.RequireSignIn);
        Assert.Contains("premium license", ex.Message);
    }

    [Fact]
    public async Task Missing_audit_log_permission_names_the_permission()
    {
        Stub("/users", GraphError("Authorization_RequestDenied", "Insufficient privileges to complete the operation."),
            status: 403, match: r => r.WithParam("$top", "120"));
        Stub("/users", new { value = Array.Empty<object>() }, match: r => r.WithParam("$top", "999"));

        var set = await new GraphAuditDirectoryData(Rest()).GetUsersAsync(Context());

        Assert.Contains("AuditLog.Read.All", set.SignInUnavailableReason);
        Assert.Equal(["Graph permission AuditLog.Read.All"], set.SignInMissing);
    }

    [Fact]
    public async Task No_access_to_users_at_all_is_unavailable_not_an_error()
    {
        Stub("/users", GraphError("Authorization_RequestDenied", "Insufficient privileges to complete the operation."), status: 403);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => new GraphAuditDirectoryData(Rest()).GetUsersAsync(Context()));

        Assert.Contains("cannot read users", ex.Message);
        Assert.Equal(["Graph permission User.Read.All"], ex.Missing);
    }

    [Fact]
    public async Task A_token_failure_makes_every_graph_read_unavailable_with_the_connection_reason()
    {
        var rest = new TenantGraphRest(new ThrowingTokenProvider(), new SingleHttpClientFactory(), Options.Create(new IntuneOptions { GraphBetaBaseUrl = _server.Url! }));

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => new GraphAuditDirectoryData(rest).GetSubscribedSkusAsync(Context()));

        Assert.Contains("Reconnect", ex.Message);
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task Each_dataset_is_fetched_once_per_run()
    {
        Stub("/subscribedSkus", new { value = new[] { new { skuId = E3, skuPartNumber = "SPE_E3", consumedUnits = 8, prepaidUnits = new { enabled = 10, warning = 2, suspended = 0, lockedOut = 0 } } } });
        var data = new GraphAuditDirectoryData(Rest());
        var ctx = Context();

        var first = await data.GetSubscribedSkusAsync(ctx);
        await data.GetSubscribedSkusAsync(ctx);

        Assert.Single(_server.LogEntries);
        Assert.Equal(new AuditSku(E3, "SPE_E3", null, null, 10, 2, 0, 0, 8), first[0]);
    }

    [Fact]
    public async Task Role_members_are_read_per_role_and_groups_expand_to_their_users()
    {
        Stub("/directoryRoles", new { value = new[] { new { id = "r1", displayName = "Global Administrator", roleTemplateId = "62e90394-69f5-4237-9190-012177145e10" } } });
        Stub("/directoryRoles/r1/members", new
        {
            value = new object[]
            {
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.user", ["id"] = "u1", ["displayName"] = "Ada", ["userPrincipalName"] = "ada@contoso.com" },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.group", ["id"] = "g1", ["displayName"] = "Tier0 Admins" },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.servicePrincipal", ["id"] = "sp1", ["displayName"] = "Backup App" }
            }
        });
        Stub("/groups/g1/transitiveMembers/microsoft.graph.user", new { value = new[] { new { id = "u2", displayName = "Grace", userPrincipalName = "grace@contoso.com" } } });

        var members = await new GraphAuditDirectoryData(Rest()).GetDirectoryRoleMembersAsync(Context());

        Assert.Equal(3, members.Count);
        Assert.All(members, m => Assert.True(m.IsPrivileged));
        Assert.Contains(members, m => m is { PrincipalId: "u2", PrincipalType: "user", ViaGroup: "Tier0 Admins" });
        Assert.Contains(members, m => m is { PrincipalId: "sp1", PrincipalType: "servicePrincipal" });
    }

    [Fact]
    public async Task Intune_not_in_use_is_unavailable_with_the_product_named()
    {
        Stub("/deviceManagement/managedDevices", GraphError("BadRequest", "Request not applicable to target tenant."), status: 400);

        var ex = await Assert.ThrowsAsync<AuditUnavailableException>(() => new GraphAuditDeviceData(Rest()).GetManagedDevicesAsync(Context()));

        Assert.Contains("Intune is not available", ex.Message);
        Assert.Equal(["Product: Microsoft Intune"], ex.Missing);
    }

    [Fact]
    public async Task Throttling_is_not_misreported_as_missing_permission()
    {
        Stub("/deviceManagement/managedDevices", GraphError("TooManyRequests", "slow down"), status: 429);

        await Assert.ThrowsAsync<GraphRequestException>(() => new GraphAuditDeviceData(Rest()).GetManagedDevicesAsync(Context()));
    }

    [Fact]
    public async Task Compliance_policies_map_platform_and_assignment_count()
    {
        Stub("/deviceManagement/deviceCompliancePolicies", new
        {
            value = new object[]
            {
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.windows10CompliancePolicy", ["id"] = "p1", ["displayName"] = "Win", ["assignments"] = new[] { new { id = "a" } } },
                new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.androidWorkProfileCompliancePolicy", ["id"] = "p2", ["displayName"] = "Droid", ["assignments"] = Array.Empty<object>() }
            }
        });

        var policies = await new GraphAuditDeviceData(Rest()).GetCompliancePoliciesAsync(Context());

        Assert.Equal([("Windows", 1), ("Android", 0)], policies.Select(p => (p.Platform, p.AssignmentCount)));
    }

    [Fact]
    public async Task Conditional_access_maps_conditions_and_resolves_excluded_names()
    {
        Stub("/identity/conditionalAccess/policies", new
        {
            value = new[]
            {
                new
                {
                    id = "ca1", displayName = "Require MFA", state = "enabled",
                    conditions = new
                    {
                        clientAppTypes = new[] { "all" },
                        applications = new { includeApplications = new[] { "All" } },
                        users = new
                        {
                            includeUsers = new[] { "All" }, excludeUsers = new[] { "aaaaaaaa-0000-0000-0000-000000000001" },
                            includeGroups = Array.Empty<string>(), excludeGroups = Array.Empty<string>(),
                            includeRoles = Array.Empty<string>(), excludeRoles = Array.Empty<string>(),
                            excludeGuestsOrExternalUsers = new { guestOrExternalUserTypes = "b2bCollaborationGuest" }
                        }
                    },
                    grantControls = new { @operator = "OR", builtInControls = new[] { "mfa" } }
                }
            }
        });
        Stub("/directoryObjects/aaaaaaaa-0000-0000-0000-000000000001", new { id = "aaaaaaaa-0000-0000-0000-000000000001", displayName = "Break Glass", userPrincipalName = "bg@contoso.com" });

        var ca = await new GraphAuditSecurityData(Rest()).GetConditionalAccessAsync(Context());

        var p = Assert.Single(ca.Policies);
        Assert.True(p.IsEnabled && p.TargetsAllUsers && p.TargetsAllApps && p.RequiresMfa && p.ExcludesGuestsOrExternalUsers);
        Assert.Equal("Break Glass (bg@contoso.com)", ca.Names["aaaaaaaa-0000-0000-0000-000000000001"]);
    }

    [Fact]
    public async Task Authorization_policy_reads_the_beta_collection_shape()
    {
        Stub("/policies/authorizationPolicy", new
        {
            value = new[]
            {
                new
                {
                    allowInvitesFrom = "everyone",
                    permissionGrantPolicyIdsAssignedToDefaultUserRole = new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-legacy" },
                    defaultUserRolePermissions = new { allowedToCreateApps = true, allowedToCreateTenants = false, allowedToCreateSecurityGroups = true }
                }
            }
        });

        var p = await new GraphAuditSecurityData(Rest()).GetAuthorizationPolicyAsync(Context());

        Assert.Equal("everyone", p.AllowInvitesFrom);
        Assert.Equal(["ManagePermissionGrantsForSelf.microsoft-user-default-legacy"], p.UserConsentPolicies);
        Assert.Equal((true, false, true), (p.UsersCanRegisterApps, p.UsersCanCreateTenants, p.UsersCanCreateSecurityGroups));
    }

    [Fact]
    public async Task Delegated_grants_resolve_apps_and_flag_microsoft_first_party()
    {
        Stub("/oauth2PermissionGrants", new
        {
            value = new[]
            {
                new { id = "g1", clientId = "sp-app", consentType = "Principal", principalId = "u1", resourceId = "sp-graph", scope = " Mail.Read  offline_access " }
            }
        });
        Stub("/servicePrincipals/sp-app", new { id = "sp-app", displayName = "Shady Mail Helper", appOwnerOrganizationId = "99999999-0000-0000-0000-000000000000", publisherName = "Shady Inc" });
        Stub("/servicePrincipals/sp-graph", new { id = "sp-graph", displayName = "Microsoft Graph", appOwnerOrganizationId = "f8cdef31-a31e-4b4a-93e4-5f571e91255a" });

        var g = Assert.Single(await new GraphAuditSecurityData(Rest()).GetDelegatedPermissionGrantsAsync(Context()));

        Assert.Equal(("Shady Mail Helper", "Shady Inc", false, "Microsoft Graph"), (g.ClientName, g.ClientPublisher, g.ClientIsMicrosoft, g.ResourceName));
        Assert.Equal(["Mail.Read", "offline_access"], g.Scopes);
    }

    [Fact]
    public async Task Audit_providers_only_ever_issue_GET_requests()
    {
        _server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithBodyAsJson(new { value = Array.Empty<object>() }));
        var ctx = Context();
        var dir = new GraphAuditDirectoryData(Rest());
        var dev = new GraphAuditDeviceData(Rest());
        var sec = new GraphAuditSecurityData(Rest());

        await dir.GetUsersAsync(ctx); await dir.GetSubscribedSkusAsync(ctx); await dir.GetDirectoryRoleMembersAsync(ctx); await dir.GetAuthMethodRegistrationsAsync(ctx);
        await dev.GetManagedDevicesAsync(ctx); await dev.GetCompliancePoliciesAsync(ctx); await dev.GetDeviceManagementSettingsAsync(ctx);
        await sec.GetConditionalAccessAsync(ctx); await sec.GetSecurityDefaultsEnabledAsync(ctx); await sec.GetAuthorizationPolicyAsync(ctx); await sec.GetDelegatedPermissionGrantsAsync(ctx);

        Assert.NotEmpty(_server.LogEntries);
        Assert.All(_server.LogEntries, e => Assert.Equal("GET", e.RequestMessage.Method));
    }

    private sealed class ThrowingTokenProvider : PartnerCenterBridge.PartnerCenter.ITokenProvider
    {
        public Task<string> GetAccessTokenAsync(string tenantId, string resource, CancellationToken ct = default) =>
            throw new InvalidOperationException("Microsoft sign-in required for admin@contoso.com. Open Tenants and choose Reconnect.");
    }
}
