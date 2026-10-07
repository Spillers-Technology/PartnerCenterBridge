namespace PartnerCenterBridge.Core.TenantAudits.Checks.Security;

/// <summary>Directory-wide defaults for ordinary users: app consent, guest invitations, app registration, tenant creation.</summary>
public sealed class DirectoryDefaultsCheck(IAuditSecurityData security) : ITenantAuditCheck
{
    private const string LegacyConsent = "ManagePermissionGrantsForSelf.microsoft-user-default-legacy";
    private const string LowRiskConsent = "ManagePermissionGrantsForSelf.microsoft-user-default-low";

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "directory-defaults",
        Name = "User consent and directory defaults",
        Category = AuditCategories.Security,
        Description = "What ordinary users may do by default: consent to apps, invite guests, register apps, create tenants.",
        BusinessImpact = "Permissive defaults let any user hand company data to a third-party app or invite outsiders without review.",
        Recommendation = "Restrict user consent to verified publishers and low-risk permissions (or require admin consent), and limit guest invitations to admins and guest inviters.",
        SeverityRules =
        [
            "Warn: users can consent to apps for any permission; anyone, including guests, can invite guests.",
            "Info: users can consent to low-risk permissions from verified publishers; users can register apps or create tenants.",
            "Pass: user consent is off or limited, and guest invitations are restricted."
        ],
        Requirements = [AuditRequirement.Graph("Policy.Read.All")]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var p = await security.GetAuthorizationPolicyAsync(ctx);
        var output = new AuditCheckOutput();
        AuditSubject Setting(string id, string name, string value) => new()
        {
            Type = "setting", Id = id, Name = name, Evidence = value, Properties = { ["value"] = value }
        };
        AuditColumn[] columns = [new("value", "Value")];

        if (p.UserConsentPolicies.Any(x => x.Equals(LegacyConsent, StringComparison.OrdinalIgnoreCase)))
            output.Add(AuditFindingBuilder.Create(d, "user-consent", AuditSeverity.Warn, "Users can consent to any app",
                "Users can grant third-party apps access to their data with any permission, without admin review.",
                columns, [Setting("permissionGrantPolicyIdsAssignedToDefaultUserRole", "User consent policy", LegacyConsent)],
                businessImpact: "Consent phishing: a user clicks 'Accept' and a malicious app can read their mail and files.",
                recommendation: "Limit user consent to verified publishers for low-risk permissions, or require admin consent with an approval workflow."));
        else if (p.UserConsentPolicies.Any(x => x.Equals(LowRiskConsent, StringComparison.OrdinalIgnoreCase)))
            output.Add(AuditFindingBuilder.Create(d, "user-consent-low", AuditSeverity.Info, "Users can consent to low-risk permissions",
                "Users can consent to apps from verified publishers for permissions Microsoft classifies as low risk (Microsoft's recommended setting).",
                columns, [Setting("permissionGrantPolicyIdsAssignedToDefaultUserRole", "User consent policy", LowRiskConsent)],
                businessImpact: "Low exposure; apps outside this scope still need an admin.", recommendation: "No change needed."));

        if (string.Equals(p.AllowInvitesFrom, "everyone", StringComparison.OrdinalIgnoreCase))
            output.Add(AuditFindingBuilder.Create(d, "guest-invites", AuditSeverity.Warn, "Anyone can invite guests",
                "Every user, including existing guests, can invite new guest accounts.",
                columns, [Setting("allowInvitesFrom", "Guest invite restrictions", p.AllowInvitesFrom!)],
                businessImpact: "Outsiders can be added to the directory, and invite further outsiders, with no admin involvement.",
                recommendation: "Restrict invitations to members, or to admins and users in the Guest Inviter role."));

        var info = new List<AuditSubject>();
        if (p.UsersCanRegisterApps == true) info.Add(Setting("allowedToCreateApps", "Users can register applications", "Yes"));
        if (p.UsersCanCreateTenants == true) info.Add(Setting("allowedToCreateTenants", "Users can create tenants", "Yes"));
        if (p.UsersCanCreateSecurityGroups == true) info.Add(Setting("allowedToCreateSecurityGroups", "Users can create security groups", "Yes"));
        if (info.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "user-permissions", AuditSeverity.Info, "Default user permissions",
                $"{AuditFindingBuilder.Count(info.Count, "default user permission")} that many organizations turn off.", columns, info,
                businessImpact: "Users can create apps, tenants or groups outside IT's view.",
                recommendation: "Turn these off unless the customer relies on them."));

        if (output.Findings.All(f => f.Severity < AuditSeverity.Warn))
            output.Add(AuditFindingBuilder.Pass(d, "User consent is restricted and guest invitations are limited."));
        output.Note($"User consent policies: {(p.UserConsentPolicies.Count == 0 ? "none (users cannot consent)" : string.Join(", ", p.UserConsentPolicies))}. Guest invitations: {p.AllowInvitesFrom ?? "not reported"}.");
        return output;
    }
}
