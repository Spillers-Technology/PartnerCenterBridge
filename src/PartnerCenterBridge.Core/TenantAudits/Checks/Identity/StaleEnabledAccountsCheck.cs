namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Enabled member accounts, licensed or not, with no successful sign-in within the threshold.</summary>
public sealed class StaleEnabledAccountsCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("licensed", "Licensed"), .. SignInEvaluator.Columns, new("created", "Created"), new("onPremisesSynced", "Synced from on-premises")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "stale-enabled-accounts",
        Name = "Stale enabled accounts",
        Category = AuditCategories.Identity,
        Description = "Enabled member accounts (licensed or not) with no successful sign-in within the inactivity threshold.",
        BusinessImpact = "Unused accounts that can still sign in are an easy target for password spraying and a sign of incomplete offboarding.",
        Recommendation = "Confirm each account is still needed; block sign-in or delete the ones that are not, and document service accounts that are.",
        SeverityRules =
        [
            "Warn: no successful sign-in for at least the threshold, or no sign-in recorded on an account older than the threshold.",
            "Unknown: recent sign-in attempts but no recorded success.",
            "Pass: every enabled member account signed in within the threshold (new accounts excluded)."
        ],
        Requirements =
        [
            AuditRequirement.Graph("User.Read.All"),
            AuditRequirement.Graph("AuditLog.Read.All", "Needed to read signInActivity."),
            AuditRequirement.License("Microsoft Entra ID P1 or P2")
        ],
        Parameters = [AuditParameterKeys.InactiveDays],
        Limitations = ["Guests are covered by the stale guest check. Service accounts that only authenticate with app credentials never sign in as users and will appear here."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var threshold = ctx.Parameters.Get(AuditParameterKeys.InactiveDays);
        var users = await directory.GetUsersAsync(ctx);
        users.RequireSignIn();
        var output = new AuditCheckOutput();
        var candidates = users.Users.Where(u => u.AccountEnabled && !u.IsGuest).ToList();

        var stale = new List<AuditSubject>();
        var unconfirmed = new List<AuditSubject>();
        foreach (var u in candidates)
        {
            var a = SignInEvaluator.Assess(u, ctx.Now, threshold);
            if (!a.IsInactive && a.Standing != SignInStanding.AttemptsWithoutSuccess) continue;
            var s = AuditUserRows.Subject(u, a.Standing == SignInStanding.NeverSignedIn ? "No sign-in recorded." : $"Basis: {a.Basis}.");
            s.Properties["licensed"] = AuditFindingBuilder.YesNo(u.AssignedSkuIds.Count > 0);
            s.Properties["onPremisesSynced"] = AuditFindingBuilder.YesNo(u.OnPremisesSynced);
            SignInEvaluator.Fill(s, a);
            (a.IsInactive ? stale : unconfirmed).Add(s);
        }

        if (stale.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "stale", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(stale.Count, "enabled account")} {(stale.Count == 1 ? "has" : "have")} no successful sign-in recorded in the last {threshold} days.",
                Columns, stale));
        if (unconfirmed.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unconfirmed", AuditSeverity.Unknown, "Sign-in success not confirmed",
                $"{AuditFindingBuilder.Count(unconfirmed.Count, "enabled account")} had recent sign-in attempts but no recorded successful sign-in.",
                Columns, unconfirmed, recommendation: "Review these accounts' sign-in logs for repeated failures."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(candidates.Count, "enabled member account")} signed in within the last {threshold} days or are newer than that."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(candidates.Count, "enabled member account")}; threshold {threshold} days.");
        output.Note(SignInEvaluator.SemanticsNote);
        return output;
    }
}
