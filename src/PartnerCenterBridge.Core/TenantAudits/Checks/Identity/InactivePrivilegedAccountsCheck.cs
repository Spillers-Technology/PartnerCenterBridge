namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Users holding directory roles who have not signed in successfully within the threshold.</summary>
public sealed class InactivePrivilegedAccountsCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("roles", "Roles"), new("enabled", "Enabled"), .. SignInEvaluator.Columns];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "inactive-privileged-accounts",
        Name = "Inactive admin accounts",
        Category = AuditCategories.Identity,
        Description = "Users with active directory role assignments and no successful sign-in within the inactivity threshold.",
        BusinessImpact = "An unused admin account keeps full power over the tenant with nobody watching it.",
        Recommendation = "Remove the role from accounts nobody uses; keep documented emergency-access accounts and monitor their sign-ins.",
        SeverityRules =
        [
            "Fail: a privileged role holder (for example Global Administrator) is inactive or has never signed in.",
            "Warn: a holder of another directory role is inactive or has never signed in.",
            "Unknown: recent sign-in attempts without a recorded success.",
            "Pass: every role holder signed in within the threshold."
        ],
        Requirements =
        [
            AuditRequirement.Graph("RoleManagement.Read.Directory"),
            AuditRequirement.Graph("User.Read.All"),
            AuditRequirement.Graph("AuditLog.Read.All", "Needed to read signInActivity."),
            AuditRequirement.License("Microsoft Entra ID P1 or P2")
        ],
        Parameters = [AuditParameterKeys.InactiveDays],
        Limitations = ["Only active role assignments are read; PIM-eligible assignments that are not activated are not included."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var threshold = ctx.Parameters.Get(AuditParameterKeys.InactiveDays);
        var members = await directory.GetDirectoryRoleMembersAsync(ctx);
        var users = await directory.GetUsersAsync(ctx);
        users.RequireSignIn();
        var byId = users.Users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var output = new AuditCheckOutput();

        var privileged = new List<AuditSubject>();
        var other = new List<AuditSubject>();
        var unconfirmed = new List<AuditSubject>();
        var holders = members.Where(m => m.PrincipalType == "user").GroupBy(m => m.PrincipalId, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var g in holders)
        {
            if (!byId.TryGetValue(g.Key, out var u)) continue;
            var a = SignInEvaluator.Assess(u, ctx.Now, threshold);
            if (!a.IsInactive && a.Standing != SignInStanding.AttemptsWithoutSuccess) continue;
            var s = AuditUserRows.Subject(u, $"Basis: {a.Basis}.");
            s.Properties["roles"] = string.Join("; ", g.Select(m => m.RoleName + (m.ViaGroup is null ? "" : $" (via {m.ViaGroup})")).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            SignInEvaluator.Fill(s, a);
            if (!a.IsInactive) unconfirmed.Add(s);
            else if (g.Any(m => m.IsPrivileged)) privileged.Add(s);
            else other.Add(s);
        }

        if (privileged.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "privileged", AuditSeverity.Fail, "Inactive privileged admins",
                $"{AuditFindingBuilder.Count(privileged.Count, "privileged admin")} {(privileged.Count == 1 ? "has" : "have")} no successful sign-in recorded in the last {threshold} days.",
                Columns, privileged));
        if (other.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "other-roles", AuditSeverity.Warn, "Inactive holders of other admin roles",
                $"{AuditFindingBuilder.Count(other.Count, "user")} with other directory roles {(other.Count == 1 ? "has" : "have")} no successful sign-in in the last {threshold} days.",
                Columns, other));
        if (unconfirmed.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unconfirmed", AuditSeverity.Unknown, "Admin sign-in success not confirmed",
                $"{AuditFindingBuilder.Count(unconfirmed.Count, "role holder")} had recent sign-in attempts but no recorded success.",
                Columns, unconfirmed, recommendation: "Check these admins' sign-in logs for repeated failures."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(holders.Count, "role holder")} signed in within the last {threshold} days."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(holders.Count, "user")} with directory roles; threshold {threshold} days.");
        output.Note(SignInEvaluator.SemanticsNote);
        return output;
    }
}
