namespace PartnerCenterBridge.Core.TenantAudits.Checks.Security;

/// <summary>
/// Whether sign-ins are protected by a baseline at all: security defaults, or Conditional Access
/// that requires MFA for everyone and blocks legacy authentication.
/// </summary>
public sealed class ConditionalAccessBaselineCheck(IAuditSecurityData security) : ITenantAuditCheck
{
    private static readonly AuditColumn[] PolicyColumns = [new("state", "State"), new("users", "Users"), new("controls", "Grant controls")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "conditional-access-baseline",
        Name = "Sign-in protection baseline",
        Category = AuditCategories.Security,
        Description = "Whether security defaults are on, or enabled Conditional Access policies require MFA for all users and block legacy authentication.",
        BusinessImpact = "Without an MFA requirement, one stolen password is enough to get into an account; legacy protocols skip MFA entirely.",
        Recommendation = "Turn on security defaults, or (with Entra ID P1) enforce Conditional Access requiring MFA for all users and blocking legacy authentication.",
        SeverityRules =
        [
            "Pass: security defaults are enabled.",
            "Fail: security defaults are off and no enabled policy requires MFA for all users (or there are no enabled policies).",
            "Warn: MFA is only required for some users or roles; legacy authentication is not blocked for all users.",
            "Info: report-only policies (evaluated but not enforced)."
        ],
        Requirements =
        [
            AuditRequirement.Graph("Policy.Read.All"),
            AuditRequirement.License("Microsoft Entra ID P1 or P2", "Only for Conditional Access; security defaults need no license.")
        ],
        Limitations =
        [
            "A policy is graded on its users, apps and grant controls; conditions such as locations, platforms or risk levels can narrow it further and are not evaluated.",
            "Exclusions are reported by the Conditional Access exclusions check."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var output = new AuditCheckOutput();
        if (await security.GetSecurityDefaultsEnabledAsync(ctx))
        {
            output.Add(AuditFindingBuilder.Pass(d, "Security defaults are enabled: MFA registration and legacy authentication blocking apply tenant-wide."));
            return output.Note("With security defaults on, Conditional Access policies cannot be enabled; none were graded.");
        }

        var ca = await security.GetConditionalAccessAsync(ctx);
        var enabled = ca.Policies.Where(p => p.IsEnabled).ToList();
        var reportOnly = ca.Policies.Where(p => p.IsReportOnly).Select(p => Row(p)).ToList();

        if (enabled.Count == 0)
        {
            output.Add(AuditFindingBuilder.Create(d, "no-protection", AuditSeverity.Fail, "No sign-in protection baseline",
                $"Security defaults are off and none of the {AuditFindingBuilder.Count(ca.Policies.Count, "Conditional Access policy", "Conditional Access policies")} is enabled."));
        }
        else
        {
            var mfaAll = enabled.Where(p => p.TargetsAllUsers && p.TargetsAllApps && p.RequiresMfa).ToList();
            var mfaSome = enabled.Where(p => p.RequiresMfa && !(p.TargetsAllUsers && p.TargetsAllApps)).ToList();
            if (mfaAll.Count == 0 && mfaSome.Count == 0)
                output.Add(AuditFindingBuilder.Create(d, "no-mfa", AuditSeverity.Fail, "No policy requires MFA",
                    $"Security defaults are off and none of the {AuditFindingBuilder.Count(enabled.Count, "enabled policy", "enabled policies")} requires MFA."));
            else if (mfaAll.Count == 0)
                output.Add(AuditFindingBuilder.Create(d, "mfa-partial", AuditSeverity.Warn, "MFA required for only some users or apps",
                    $"{AuditFindingBuilder.Count(mfaSome.Count, "enabled policy", "enabled policies")} require MFA, but none for all users on all cloud apps.",
                    PolicyColumns, mfaSome.Select(p => Row(p)),
                    recommendation: "Add a policy requiring MFA for all users and all cloud apps, excluding only emergency-access accounts."));

            var legacyBlocked = enabled.Any(p => p.Blocks && p.TargetsAllUsers
                && p.ClientAppTypes.Any(t => t is "exchangeActiveSync" or "other"));
            if (!legacyBlocked)
                output.Add(AuditFindingBuilder.Create(d, "legacy-auth", AuditSeverity.Warn, "Legacy authentication not blocked",
                    "No enabled policy blocks legacy authentication clients (Exchange ActiveSync and other clients) for all users.",
                    businessImpact: "Legacy protocols cannot do MFA, so they are the usual path for password-spray attacks.",
                    recommendation: "Add a Conditional Access policy that blocks 'Exchange ActiveSync clients' and 'Other clients' for all users."));

            if (mfaAll.Count > 0 && legacyBlocked)
                output.Add(AuditFindingBuilder.Create(d, "baseline", AuditSeverity.Pass, d.Name,
                    $"MFA is required for all users on all cloud apps ({string.Join(", ", mfaAll.Select(p => p.DisplayName))}) and legacy authentication is blocked.",
                    PolicyColumns, mfaAll.Select(p => Row(p))));
        }

        if (reportOnly.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "report-only", AuditSeverity.Info, "Report-only policies",
                $"{AuditFindingBuilder.Count(reportOnly.Count, "policy", "policies")} {(reportOnly.Count == 1 ? "is" : "are")} evaluated but not enforced.",
                PolicyColumns, reportOnly,
                businessImpact: "Report-only policies show what would happen but protect nothing yet.",
                recommendation: "Review the policy's sign-in impact and turn it on, or delete it."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(ca.Policies.Count, "Conditional Access policy", "Conditional Access policies")} ({enabled.Count} enabled).");
        return output;
    }

    private static AuditSubject Row(AuditConditionalAccessPolicy p) => new()
    {
        Type = "policy", Id = p.Id, Name = p.DisplayName, Evidence = $"State {p.State}.",
        Properties =
        {
            ["state"] = p.State,
            ["users"] = p.TargetsAllUsers ? "All users" : $"{p.IncludeUsers.Count + p.IncludeGroups.Count + p.IncludeRoles.Count} specific users/groups/roles",
            ["controls"] = string.Join("; ", p.BuiltInControls.Concat(p.RequiresAuthenticationStrength ? ["authenticationStrength"] : []))
        }
    };
}
