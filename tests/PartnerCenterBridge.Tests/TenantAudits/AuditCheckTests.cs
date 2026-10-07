using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Core.TenantAudits.Checks;
using PartnerCenterBridge.Core.TenantAudits.Checks.Devices;
using PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;
using PartnerCenterBridge.Core.TenantAudits.Checks.Identity;
using PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;
using PartnerCenterBridge.Core.TenantAudits.Checks.Security;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;
using static PartnerCenterBridge.Tests.TenantAudits.DormantLicensedUsersCheckTests;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class IdentityCheckTests
{
    private readonly FakeDirectoryData _d = new();
    private const string Ga = PrivilegedRoles.GlobalAdministrator;

    private static AuditRoleMember Role(string userId, string role = "Global Administrator", string template = Ga, bool privileged = true, string type = "user") =>
        new(template, role, privileged, type, userId, userId, null, null);

    [Fact]
    public void Stale_enabled_accounts_cover_unlicensed_members_but_not_guests_or_disabled()
    {
        _d.Users.AddRange([
            User("Unlicensed old", successDaysAgo: 200, skus: []),
            User("Disabled old", successDaysAgo: 200, enabled: false),
            User("Guest old", successDaysAgo: 200, guest: true),
            User("Fresh", successDaysAgo: 3)
        ]);

        var r = Run(new StaleEnabledAccountsCheck(_d));

        Assert.Equal(["Unlicensed old"], Names(Finding(r, "stale")));
        Assert.Equal("No", Finding(r, "stale").Subjects[0].Properties["licensed"]);
    }

    [Fact]
    public void Inactive_global_admin_fails_and_other_roles_warn()
    {
        _d.Users.AddRange([User("Old GA", successDaysAgo: 200), User("Old reader", successDaysAgo: 200), User("Active GA", successDaysAgo: 1)]);
        _d.Roles.AddRange([Role("id-Old GA"), Role("id-Old reader", "Global Reader", "f2ef992c-3afb-46b9-b7cf-a126ee74c451", privileged: false), Role("id-Active GA")]);

        var r = Run(new InactivePrivilegedAccountsCheck(_d));

        Assert.Equal(AuditSeverity.Fail, Finding(r, "privileged").Severity);
        Assert.Equal(["Old GA"], Names(Finding(r, "privileged")));
        Assert.Equal(["Old reader"], Names(Finding(r, "other-roles")));
        Assert.Equal(AuditSeverity.Fail, r.WorstSeverity);
    }

    [Fact]
    public void Inactive_admins_needs_roles_permission_and_says_so()
    {
        _d.RolesError = new AuditUnavailableException("cannot read directory roles", ["Graph permission RoleManagement.Read.Directory"]);

        var r = Run(new InactivePrivilegedAccountsCheck(_d));

        Assert.Equal(AuditCheckStatus.Unavailable, r.Status);
        Assert.Equal(["Graph permission RoleManagement.Read.Directory"], r.MissingRequirements);
    }

    [Fact]
    public void Role_inventory_flags_guests_disabled_holders_and_too_many_global_admins()
    {
        _d.Users.AddRange([User("Guest admin", guest: true), User("Disabled admin", enabled: false)]);
        _d.Users.AddRange(Enumerable.Range(1, 5).Select(i => User($"GA{i}")));
        _d.Roles.AddRange([Role("id-Guest admin", "Exchange Administrator", "29232cdf-9323-42fd-ade2-1d097af3e4de"), Role("id-Disabled admin", "User Administrator", "fe930be7-5e62-47db-91af-98c3a49a38b1")]);
        _d.Roles.AddRange(Enumerable.Range(1, 5).Select(i => Role($"id-GA{i}")));
        _d.Roles.Add(Role("sp-1", "Global Administrator", Ga, type: "servicePrincipal"));

        var r = Run(new PrivilegedRoleInventoryCheck(_d));

        Assert.Equal(AuditSeverity.Fail, Finding(r, "guests").Severity);
        Assert.Equal(AuditSeverity.Warn, Finding(r, "disabled").Severity);
        Assert.Equal(5, Finding(r, "too-many-global-admins").SubjectCount);
        Assert.Equal(AuditSeverity.Info, Finding(r, "apps").Severity);
        Assert.Equal(8, Finding(r, "inventory").SubjectCount);
    }

    [Fact]
    public void Mfa_coverage_fails_for_admins_warns_for_users_and_skips_guests_and_disabled()
    {
        _d.Users.Add(User("Disabled", enabled: false));
        _d.Registrations.AddRange([
            new("id-Admin", "admin@contoso.com", "Admin", "member", true, false, false, false, []),
            new("id-User", "user@contoso.com", "User", "member", false, false, false, false, ["email"]),
            new("id-Guest", "g@x.com", "Guest", "guest", false, false, false, false, []),
            new("id-Disabled", "d@contoso.com", "Disabled", "member", false, false, false, false, []),
            new("id-Good", "good@contoso.com", "Good", "member", false, true, true, false, ["microsoftAuthenticatorPush"])
        ]);

        var r = Run(new MfaRegistrationCoverageCheck(_d));

        Assert.Equal(["Admin"], Names(Finding(r, "admins")));
        Assert.Equal(["User"], Names(Finding(r, "users")));
        Assert.Contains("1 enabled user of 3", Finding(r, "users").Summary);
    }

    [Theory]
    [InlineData(new string[0], "none", AuditSeverity.Fail)]
    [InlineData(new[] { "mobilePhone", "email" }, "weak", AuditSeverity.Warn)]
    [InlineData(new[] { "mobilePhone", "microsoftAuthenticatorPush" }, "not-phishing-resistant", AuditSeverity.Info)]
    [InlineData(new[] { "temporaryAccessPass" }, "none", AuditSeverity.Fail)]
    public void Admin_method_strength_is_graded(string[] methods, string key, AuditSeverity expected)
    {
        _d.Registrations.Add(new("id-A", "a@contoso.com", "A", "member", true, methods.Length > 0, false, false, methods));

        Assert.Equal(expected, Finding(Run(new PrivilegedAuthStrengthCheck(_d)), key).Severity);
    }

    [Fact]
    public void Admins_with_a_phishing_resistant_method_pass()
    {
        _d.Registrations.Add(new("id-A", "a@contoso.com", "A", "member", true, true, true, true, ["mobilePhone", "fido2SecurityKey"]));

        Assert.Equal(AuditSeverity.Pass, Assert.Single(Run(new PrivilegedAuthStrengthCheck(_d)).Findings).Severity);
    }

    [Fact]
    public void Guest_review_flags_old_pending_invitations()
    {
        _d.Users.AddRange([
            User("Old invite", guest: true, externalState: "PendingAcceptance", stateChangedDaysAgo: 60),
            User("New invite", guest: true, externalState: "PendingAcceptance", stateChangedDaysAgo: 5),
            User("Accepted", guest: true, externalState: "Accepted", stateChangedDaysAgo: 100)
        ]);

        var r = Run(new GuestAccountsCheck(_d));

        Assert.Equal(["Old invite"], Names(Finding(r, "pending")));
        Assert.Equal(3, Finding(r, "inventory").SubjectCount);
    }

    [Fact]
    public void Stale_guests_use_the_shared_threshold()
    {
        _d.Users.AddRange([User("Old guest", guest: true, successDaysAgo: 100), User("Recent guest", guest: true, successDaysAgo: 10)]);

        Assert.Equal(["Old guest"], Names(Finding(Run(new StaleGuestAccountsCheck(_d)), "stale")));
        Assert.Equal(AuditSeverity.Pass, Run(new StaleGuestAccountsCheck(_d), Context(inactiveDays: 180)).Findings.Single().Severity);
    }
}

public class LicensingCheckTests
{
    private readonly FakeDirectoryData _d = new();
    private readonly FakeMailboxData _m = new();

    public LicensingCheckTests() => _d.Skus.AddRange([Sku(E3, "SPE_E3"), Sku(FlowFree, "FLOW_FREE", 10000, 3)]);

    [Fact]
    public void Disabled_accounts_with_paid_licenses_warn_with_an_offboarding_hint()
    {
        _d.Users.AddRange([User("Leaver", enabled: false), User("Free leaver", enabled: false, skus: [FlowFree]), User("Active")]);

        var f = Finding(Run(new DisabledAccountsWithLicensesCheck(_d)), "licensed");

        Assert.Equal(["Leaver"], Names(f));
        Assert.Equal("offboarding", f.Subjects[0].Remediation!.Kind);
    }

    [Fact]
    public void Subscription_utilization_finds_unused_over_assigned_and_lapsing_seats_ignoring_free_skus()
    {
        _d.Skus.Clear();
        _d.Skus.AddRange([
            Sku("a", "SPE_E3", enabled: 20, consumed: 15),
            Sku("b", "EXCHANGESTANDARD", enabled: 5, consumed: 7),
            Sku("c", "SPB", enabled: 10, consumed: 10, warning: 10),
            Sku(FlowFree, "FLOW_FREE", enabled: 10000, consumed: 3)
        ]);

        var r = Run(new SubscriptionUtilizationCheck(_d));

        Assert.Equal(["EXCHANGESTANDARD"], Names(Finding(r, "over-assigned")));
        Assert.Equal(["SPB"], Names(Finding(r, "lapsing")));
        Assert.Equal(["SPE_E3"], Names(Finding(r, "unassigned")));
        Assert.StartsWith("5 seats", Finding(r, "unassigned").Summary);
    }

    [Fact]
    public void Shared_mailbox_licenses_separate_likely_unneeded_from_archive_or_hold()
    {
        _d.Users.AddRange([User("info"), User("legal"), User("free")]);
        _m.Report = new AuditMailboxReport { Mailboxes = [Mailbox("id-info", "SharedMailbox"), Mailbox("id-legal", "SharedMailbox", hold: true), Mailbox("id-free", "UserMailbox")] };

        var r = Run(new SharedMailboxLicensesCheck(_d, _m));

        Assert.Equal(["id-info"], Names(Finding(r, "unneeded")));
        Assert.Equal(["id-legal"], Names(Finding(r, "justified")));
    }

    [Fact]
    public void Shared_mailbox_licenses_are_unavailable_without_exchange()
    {
        _m.UnavailableReason = "Exchange Online is not configured.";

        var r = Run(new SharedMailboxLicensesCheck(_d, _m));

        Assert.Equal(AuditCheckStatus.Unavailable, r.Status);
        Assert.Equal("Exchange Online is not configured.", r.StatusReason);
    }
}

public class ExchangeCheckTests
{
    private readonly FakeMailboxData _m = new();

    private static AuditMailbox Fwd(string id, string? smtp = null, string? addr = null, string? addrSmtp = null) =>
        new(id, id + "@contoso.com", id, id + "@contoso.com", "UserMailbox", smtp, addr, addrSmtp, true, false, "None", false, false, []);

    [Fact]
    public void External_forwarding_fails_internal_is_info_and_an_open_policy_warns()
    {
        _m.Report = new AuditMailboxReport
        {
            Mailboxes = [Fwd("ext", smtp: "smtp:someone@gmail.com"), Fwd("int", smtp: "smtp:boss@contoso.com"), Fwd("contact", addr: "Gmail Contact", addrSmtp: "x@outlook.com"), Fwd("none")],
            AcceptedDomains = ["contoso.com"],
            AutoForwardingMode = "On"
        };

        var r = Run(new MailboxForwardingCheck(_m));

        Assert.Equal(["contact", "ext"], Names(Finding(r, "external")));
        Assert.Equal("someone@gmail.com", Finding(r, "external").Subjects.Single(s => s.Name == "ext").Properties["forwardTo"]);
        Assert.Equal(["int"], Names(Finding(r, "internal")));
        Assert.Equal(AuditSeverity.Warn, Finding(r, "policy").Severity);
    }

    [Fact]
    public void Default_outbound_policy_and_no_forwards_pass()
    {
        _m.Report = new AuditMailboxReport { Mailboxes = [Fwd("none")], AcceptedDomains = ["contoso.com"], AutoForwardingMode = "Automatic" };

        Assert.Equal(AuditSeverity.Pass, Run(new MailboxForwardingCheck(_m)).Findings.Single().Severity);
    }

    [Theory]
    [InlineData("a@gmail.com", true)]
    [InlineData("a@CONTOSO.com", false)]
    [InlineData("Sales Team", false)]
    public void External_means_outside_the_accepted_domains(string address, bool external) =>
        Assert.Equal(external, MailboxForwardingCheck.IsExternal(address, new HashSet<string>(["contoso.com"], StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public void Delegations_match_send_as_by_display_name_and_split_user_from_shared_mailboxes()
    {
        _m.Report = new AuditMailboxReport
        {
            Mailboxes = [Mailbox("ceo", "UserMailbox"), Mailbox("info", "SharedMailbox") with { DisplayName = "Info Desk" }],
            Permissions = [new("ceo@contoso.com", "pa@contoso.com", "FullAccess"), new("Info Desk", "ada@contoso.com", "SendAs")],
            FullAccessEvaluated = 1, FullAccessComplete = false
        };

        var r = Run(new MailboxDelegationCheck(_m));

        Assert.Equal("FullAccess", Finding(r, "user-mailboxes").Subjects.Single().Properties["right"]);
        Assert.Equal("SharedMailbox", Finding(r, "shared-mailboxes").Subjects.Single().Properties["mailboxType"]);
        Assert.Contains(r.Notes, n => n.StartsWith("Full Access was read for 1 of 2"));
    }

    [Fact]
    public void Shared_mailboxes_that_can_sign_in_warn()
    {
        var d = new FakeDirectoryData();
        d.Users.AddRange([User("open"), User("closed", enabled: false)]);
        _m.Report = new AuditMailboxReport { Mailboxes = [Mailbox("id-open", "SharedMailbox"), Mailbox("id-closed", "SharedMailbox")] };

        Assert.Equal(["id-open"], Names(Finding(Run(new SharedMailboxSignInCheck(_m, d)), "enabled")));
    }

    [Fact]
    public void Missing_archives_link_to_the_mailbox_archive_known_fix()
    {
        _m.Report = new AuditMailboxReport { Mailboxes = [Mailbox("a", "UserMailbox"), Mailbox("b", "UserMailbox", archive: true), Mailbox("s", "SharedMailbox")] };

        var f = Finding(Run(new MailboxArchiveCheck(_m)), "no-archive");

        Assert.Equal(["a"], Names(f));
        Assert.Equal(("workflow", "mailbox-archive", "a@contoso.com"), (f.Subjects[0].Remediation!.Kind, f.Subjects[0].Remediation!.Target, f.Subjects[0].Remediation!.Identity));
    }
}

public class DeviceCheckTests
{
    private readonly FakeDeviceData _d = new();

    private static AuditDevice Device(string name, int? syncDaysAgo = 1, string os = "Windows", string compliance = "compliant", bool? encrypted = true, string? serial = null) =>
        new($"dev-{name}", name, os, "10.0", compliance, syncDaysAgo is null ? null : Now.AddDays(-syncDaysAgo.Value), encrypted, "u@contoso.com", "company", Now.AddDays(-400), serial, "mdm");

    [Fact]
    public void Stale_devices_respect_the_device_threshold_and_unknown_sync_is_unknown()
    {
        _d.Devices.AddRange([Device("old", 45), Device("recent", 2), Device("never", null)]);

        var r = Run(new StaleDevicesCheck(_d));
        Assert.Equal(["old"], Names(Finding(r, "stale")));
        Assert.Equal(AuditSeverity.Unknown, Finding(r, "no-sync").Severity);
        Assert.DoesNotContain(Run(new StaleDevicesCheck(_d), Context(deviceStaleDays: 60)).Findings, f => f.Id.EndsWith(":stale"));
    }

    [Fact]
    public void Compliance_states_map_to_severities()
    {
        _d.Devices.AddRange([Device("bad", compliance: "noncompliant"), Device("grace", compliance: "inGracePeriod"), Device("err", compliance: "conflict"), Device("ok")]);

        var r = Run(new NoncompliantDevicesCheck(_d));

        Assert.Equal((AuditSeverity.Warn, AuditSeverity.Info, AuditSeverity.Unknown),
            (Finding(r, "noncompliant").Severity, Finding(r, "grace").Severity, Finding(r, "error").Severity));
    }

    [Fact]
    public void Coverage_flags_uncovered_platforms_unassigned_policies_and_permissive_default()
    {
        _d.Devices.AddRange([Device("pc"), Device("phone", os: "iOS"), Device("tablet", os: "iPadOS")]);
        _d.Policies.AddRange([new("p1", "Windows baseline", "Windows", 1), new("p2", "Old iOS", "iOS", 0)]);
        _d.Settings = new(false);

        var r = Run(new CompliancePolicyCoverageCheck(_d));

        Assert.Equal(["iOS"], Names(Finding(r, "platform-gaps")));
        Assert.Equal("2", Finding(r, "platform-gaps").Subjects[0].Properties["devices"]);
        Assert.Equal(["Old iOS"], Names(Finding(r, "unassigned")));
        Assert.Equal(AuditSeverity.Warn, Finding(r, "not-secure-by-default").Severity);
    }

    [Fact]
    public void No_policies_at_all_with_managed_devices_fails()
    {
        _d.Devices.Add(Device("pc"));

        Assert.Equal(AuditSeverity.Fail, Finding(Run(new CompliancePolicyCoverageCheck(_d)), "none").Severity);
    }

    [Fact]
    public void Intune_missing_makes_device_checks_unavailable()
    {
        _d.Error = new AuditUnavailableException("Microsoft Intune is not available in this tenant", ["Product: Microsoft Intune"]);

        Assert.All(new ITenantAuditCheck[] { new StaleDevicesCheck(_d), new DeviceEncryptionCheck(_d), new DuplicateDeviceRecordsCheck(_d) },
            c => Assert.Equal(AuditCheckStatus.Unavailable, Run(c).Status));
    }

    [Fact]
    public void Encryption_and_duplicates()
    {
        _d.Devices.AddRange([
            Device("plain", encrypted: false), Device("quiet", encrypted: null),
            Device("old-copy", syncDaysAgo: 90, serial: "SN1"), Device("current", syncDaysAgo: 1, serial: "SN1"), Device("vm", serial: "0"), Device("vm2", serial: "0")
        ]);

        var enc = Run(new DeviceEncryptionCheck(_d));
        Assert.Equal(["plain"], Names(Finding(enc, "unencrypted")));
        Assert.Equal(["quiet"], Names(Finding(enc, "unknown")));
        Assert.Equal(["old-copy"], Names(Finding(Run(new DuplicateDeviceRecordsCheck(_d)), "duplicates")));
    }
}

public class SecurityCheckTests
{
    private readonly FakeSecurityData _s = new();

    private static AuditConditionalAccessPolicy Policy(string name, string state = "enabled", string[]? users = null, string[]? apps = null,
        string[]? controls = null, string[]? clients = null, string[]? excludeUsers = null, string[]? excludeGroups = null, bool excludeGuests = false) =>
        new(name, name, state, users ?? ["All"], excludeUsers ?? [], [], excludeGroups ?? [], [], [], excludeGuests, apps ?? ["All"],
            clients ?? ["all"], controls ?? ["mfa"], false);

    [Fact]
    public void Security_defaults_pass_without_reading_conditional_access()
    {
        _s.SecurityDefaults = true;
        _s.CaError = new AuditUnavailableException("should not be read");

        Assert.Equal(AuditSeverity.Pass, Run(new ConditionalAccessBaselineCheck(_s)).Findings.Single().Severity);
    }

    [Fact]
    public void No_defaults_and_no_enabled_policy_fails()
    {
        _s.Policies.Add(Policy("Draft", state: "enabledForReportingButNotEnforced"));

        var r = Run(new ConditionalAccessBaselineCheck(_s));

        Assert.Equal(AuditSeverity.Fail, Finding(r, "no-protection").Severity);
        Assert.Equal(AuditSeverity.Info, Finding(r, "report-only").Severity);
    }

    [Fact]
    public void Mfa_for_all_plus_legacy_block_is_the_baseline()
    {
        _s.Policies.AddRange([Policy("MFA all"), Policy("Block legacy", controls: ["block"], clients: ["exchangeActiveSync", "other"])]);

        var r = Run(new ConditionalAccessBaselineCheck(_s));

        Assert.Equal(AuditSeverity.Pass, Finding(r, "baseline").Severity);
    }

    [Fact]
    public void Admin_only_mfa_warns_and_unblocked_legacy_auth_warns()
    {
        _s.Policies.Add(Policy("MFA admins", users: [], apps: ["All"]));

        var r = Run(new ConditionalAccessBaselineCheck(_s));

        Assert.Equal(AuditSeverity.Warn, Finding(r, "mfa-partial").Severity);
        Assert.Equal(AuditSeverity.Warn, Finding(r, "legacy-auth").Severity);
    }

    [Fact]
    public void Conditional_access_needs_a_license_and_says_so()
    {
        _s.CaError = new AuditUnavailableException("The tenant does not have the license", ["License: Microsoft Entra ID P1 or P2"]);

        var r = Run(new ConditionalAccessBaselineCheck(_s));

        Assert.Equal(AuditCheckStatus.Unavailable, r.Status);
        Assert.Contains("License: Microsoft Entra ID P1 or P2", r.MissingRequirements);
    }

    [Fact]
    public void Exclusions_on_baseline_policies_warn_and_names_are_resolved()
    {
        _s.Policies.AddRange([
            Policy("MFA all", excludeUsers: ["u-bg"], excludeGuests: true),
            Policy("Compliant device for finance", users: [], controls: ["compliantDevice"], excludeGroups: ["g-1"]),
            Policy("Disabled", state: "disabled", excludeUsers: ["u-x"])
        ]);
        _s.Names["u-bg"] = "Break Glass";

        var r = Run(new ConditionalAccessExclusionsCheck(_s));

        Assert.Equal(["Break Glass", "Guests or external users"], Names(Finding(r, "baseline-exclusions")));
        Assert.Equal(["g-1"], Names(Finding(r, "other-exclusions")));
    }

    [Fact]
    public void Open_consent_and_open_invitations_warn()
    {
        _s.Authorization = new("everyone", true, false, false, ["ManagePermissionGrantsForSelf.microsoft-user-default-legacy"], null);

        var r = Run(new DirectoryDefaultsCheck(_s));

        Assert.Equal(AuditSeverity.Warn, Finding(r, "user-consent").Severity);
        Assert.Equal(AuditSeverity.Warn, Finding(r, "guest-invites").Severity);
        Assert.Equal(AuditSeverity.Info, Finding(r, "user-permissions").Severity);
        Assert.DoesNotContain(r.Findings, f => f.Severity == AuditSeverity.Pass);
    }

    [Fact]
    public void Restricted_defaults_pass()
    {
        Assert.Contains(Run(new DirectoryDefaultsCheck(_s)).Findings, f => f.Severity == AuditSeverity.Pass);
    }

    [Fact]
    public void User_consented_high_impact_scopes_on_third_party_apps_warn()
    {
        _s.Grants.AddRange([
            new("g1", "c1", "Shady Mail", "Shady", false, "Principal", "u1", "Microsoft Graph", ["Mail.Read", "openid"]),
            new("g2", "c2", "CRM", "Contoso CRM", false, "AllPrincipals", null, "Microsoft Graph", ["Files.ReadWrite.All"]),
            new("g3", "c3", "Teams", "Microsoft", true, "Principal", "u1", "Microsoft Graph", ["Mail.ReadWrite"]),
            new("g4", "c4", "Harmless", "x", false, "Principal", "u1", "Microsoft Graph", ["User.Read", "openid"])
        ]);

        var r = Run(new DelegatedPermissionGrantsCheck(_s));

        Assert.Equal(["Shady Mail"], Names(Finding(r, "user-consented")));
        Assert.Equal("Mail.Read", Finding(r, "user-consented").Subjects[0].Properties["scopes"]);
        Assert.Equal(["CRM"], Names(Finding(r, "admin-consented")));
    }
}
