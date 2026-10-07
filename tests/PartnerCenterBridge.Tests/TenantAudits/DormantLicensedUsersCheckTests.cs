using PartnerCenterBridge.Core.TenantAudits;
using PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;
using static PartnerCenterBridge.Tests.TenantAudits.AuditTest;

namespace PartnerCenterBridge.Tests.TenantAudits;

public class DormantLicensedUsersCheckTests
{
    private readonly FakeDirectoryData _directory = new();
    private readonly FakeMailboxData _mailboxes = new() { UnavailableReason = "Exchange Online is not configured: Exchange:AppId is not set." };

    private DormantLicensedUsersCheck Check() => new(_directory, _mailboxes);

    public DormantLicensedUsersCheckTests()
    {
        _directory.Skus.Add(Sku(E3, "SPE_E3"));
        _directory.Skus.Add(Sku(FlowFree, "FLOW_FREE"));
    }

    [Fact]
    public void Splits_licensed_users_by_what_the_sign_in_data_proves()
    {
        _directory.Users.AddRange(
        [
            User("Active", successDaysAgo: 5),
            User("Dormant", successDaysAgo: 120),
            User("Attempt only old", attemptDaysAgo: 150),
            User("Never", createdDaysAgo: 500),
            User("Failing", attemptDaysAgo: 2),
            User("New hire", createdDaysAgo: 5)
        ]);

        var r = Run(Check());

        Assert.Equal(AuditCheckStatus.Completed, r.Status);
        Assert.Equal(["Attempt only old", "Dormant"], Names(Finding(r, "dormant")));
        Assert.Equal(AuditSeverity.Warn, Finding(r, "dormant").Severity);
        Assert.Equal(["Never"], Names(Finding(r, "never-signed-in")));
        Assert.Equal(AuditSeverity.Unknown, Finding(r, "unconfirmed").Severity);
        Assert.Equal(["Failing"], Names(Finding(r, "unconfirmed")));
        Assert.Equal(AuditSeverity.Info, Finding(r, "new-accounts").Severity);
        Assert.DoesNotContain(r.Findings, f => f.Severity == AuditSeverity.Pass);
        Assert.DoesNotContain(r.Findings.SelectMany(f => f.Subjects), s => s.Name == "Active");
    }

    [Fact]
    public void Dormant_rows_carry_the_reviewable_columns_and_an_offboarding_hint()
    {
        _directory.Users.Add(User("Dormant", successDaysAgo: 120, attemptDaysAgo: 100));
        _directory.Roles.Add(new("62e90394-69f5-4237-9190-012177145e10", "Global Administrator", true, "user", "id-Dormant", "Dormant", null, null));

        var row = Assert.Single(Finding(Run(Check()), "dormant").Subjects);

        Assert.Equal("dormant@contoso.com", row.Upn);
        Assert.Equal("Yes", row.Properties["enabled"]);
        Assert.Equal("SPE_E3", row.Properties["licenses"]);
        Assert.Equal("2026-06-03", row.Properties["lastSuccessfulSignIn"]);
        Assert.Equal("2026-06-23", row.Properties["lastSignInAttempt"]);
        Assert.Equal("120", row.Properties["daysInactive"]);
        Assert.Equal("last successful sign-in", row.Properties["basis"]);
        Assert.Equal("Global Administrator", row.Properties["adminRoles"]);
        Assert.Equal("offboarding", row.Remediation!.Kind);
        Assert.Equal("id-Dormant", row.Remediation.Target);
    }

    [Theory]
    [InlineData(30, new[] { "Forty", "Hundred", "Two hundred" })]
    [InlineData(45, new[] { "Hundred", "Two hundred" })] // a custom value, not just the presets
    [InlineData(90, new[] { "Hundred", "Two hundred" })]
    [InlineData(180, new[] { "Two hundred" })]
    public void The_threshold_parameter_moves_the_line(int threshold, string[] expected)
    {
        _directory.Users.AddRange([User("Forty", successDaysAgo: 40), User("Hundred", successDaysAgo: 100), User("Two hundred", successDaysAgo: 200)]);

        var r = Run(Check(), Context(inactiveDays: threshold));

        Assert.Equal(expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Names(Finding(r, "dormant")));
        Assert.Contains(r.Notes, n => n.Contains($"threshold {threshold} days"));
    }

    [Fact]
    public void Users_holding_only_no_cost_licenses_are_not_counted()
    {
        _directory.Users.Add(User("Free only", successDaysAgo: 300, skus: [FlowFree]));
        _directory.Users.Add(User("Unlicensed", successDaysAgo: 300, skus: []));

        var r = Run(Check());

        var pass = Assert.Single(r.Findings);
        Assert.Equal(AuditSeverity.Pass, pass.Severity);
        Assert.Contains("No users hold licenses", pass.Summary);
    }

    [Fact]
    public void Missing_sign_in_data_makes_the_check_unavailable_with_the_reason_not_a_pass()
    {
        _directory.Users.Add(User("Dormant", successDaysAgo: 300));
        _directory.SignInUnavailable = "Sign-in activity is only available in tenants with Microsoft Entra ID P1 or P2.";

        var r = Run(Check());

        Assert.Equal(AuditCheckStatus.Unavailable, r.Status);
        Assert.Contains("Entra ID P1 or P2", r.StatusReason);
        Assert.Contains("Graph permission AuditLog.Read.All", r.MissingRequirements);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void Optional_enrichment_failing_does_not_fail_the_check()
    {
        _directory.Users.Add(User("Dormant", successDaysAgo: 300));
        _directory.RolesError = new AuditUnavailableException("roles denied");
        _directory.SkusError = new AuditUnavailableException("skus denied");

        var r = Run(Check());

        Assert.Equal(AuditCheckStatus.Completed, r.Status);
        Assert.Contains(r.Notes, n => n.Contains("Admin roles are not shown: roles denied"));
        Assert.Contains(r.Notes, n => n.Contains("shown as SKU ids"));
        Assert.Contains(r.Notes, n => n.Contains("Shared and resource mailboxes are not distinguished: Exchange Online is not configured"));
        // Without SKU names the id is shown and treated as possibly paid.
        Assert.Equal(E3, Finding(r, "dormant").Subjects[0].Properties["licenses"]);
    }

    [Fact]
    public void Shared_and_resource_mailboxes_are_listed_apart_when_exchange_data_is_available()
    {
        _directory.Users.Add(User("Reception", createdDaysAgo: 900));
        _directory.Users.Add(User("Room 1", createdDaysAgo: 900));
        _directory.Users.Add(User("Dormant", successDaysAgo: 300));
        _mailboxes.UnavailableReason = null;
        _mailboxes.Report = new AuditMailboxReport
        {
            Mailboxes =
            [
                Mailbox("id-Reception", "SharedMailbox"),
                Mailbox("id-Room 1", "RoomMailbox"),
                Mailbox("id-Dormant", "UserMailbox")
            ]
        };

        var r = Run(Check());

        Assert.Equal(["Reception", "Room 1"], Names(Finding(r, "shared-or-resource")));
        Assert.Equal(AuditSeverity.Info, Finding(r, "shared-or-resource").Severity);
        Assert.Equal(["Dormant"], Names(Finding(r, "dormant")));
        Assert.Equal("UserMailbox", Finding(r, "dormant").Subjects[0].Properties["mailboxType"]);
        Assert.DoesNotContain(r.Findings, f => f.Id.EndsWith(":never-signed-in"));
    }

    [Fact]
    public void All_active_is_a_pass_with_the_scope_in_the_notes()
    {
        _directory.Users.AddRange([User("A", successDaysAgo: 1), User("B", successDaysAgo: 30)]);

        var r = Run(Check());

        Assert.Equal(AuditSeverity.Pass, Assert.Single(r.Findings).Severity);
        Assert.Contains(r.Notes, n => n.StartsWith("Evaluated 2 users holding"));
        Assert.Contains(r.Notes, n => n.Contains("does not prove whether someone used Microsoft 365"));
    }

    internal static AuditMailbox Mailbox(string objectId, string type, string? upn = null, string? fwdSmtp = null, bool archive = false, bool hold = false) =>
        new(objectId, upn ?? objectId + "@contoso.com", objectId, upn ?? objectId + "@contoso.com", type, fwdSmtp, null, null, false, archive,
            archive ? "Active" : "None", false, hold, []);
}
