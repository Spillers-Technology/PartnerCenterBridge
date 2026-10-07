namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Enabled guests with no successful sign-in within the threshold.</summary>
public sealed class StaleGuestAccountsCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("invitationState", "Invitation state"), .. SignInEvaluator.Columns, new("created", "Created")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "stale-guest-accounts",
        Name = "Stale guest accounts",
        Category = AuditCategories.Identity,
        Description = "Enabled guest accounts with no successful sign-in within the inactivity threshold.",
        BusinessImpact = "Unused guest accounts keep access to shared data with no business reason.",
        Recommendation = "Remove stale guests, or set up a guest access review so this happens automatically.",
        SeverityRules =
        [
            "Warn: an enabled guest has no successful sign-in for at least the threshold, or never signed in and is older than the threshold.",
            "Pass: every enabled guest signed in within the threshold."
        ],
        Requirements =
        [
            AuditRequirement.Graph("User.Read.All"),
            AuditRequirement.Graph("AuditLog.Read.All", "Needed to read signInActivity."),
            AuditRequirement.License("Microsoft Entra ID P1 or P2")
        ],
        Parameters = [AuditParameterKeys.InactiveDays]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var threshold = ctx.Parameters.Get(AuditParameterKeys.InactiveDays);
        var users = await directory.GetUsersAsync(ctx);
        users.RequireSignIn();
        var guests = users.Users.Where(u => u.IsGuest && u.AccountEnabled).ToList();
        var output = new AuditCheckOutput();
        var stale = new List<AuditSubject>();
        foreach (var u in guests)
        {
            var a = SignInEvaluator.Assess(u, ctx.Now, threshold);
            if (!a.IsInactive) continue;
            var s = AuditUserRows.Subject(u, a.Standing == SignInStanding.NeverSignedIn ? "No sign-in recorded." : $"Basis: {a.Basis}.");
            s.Properties["invitationState"] = u.ExternalUserState;
            SignInEvaluator.Fill(s, a);
            stale.Add(s);
        }
        if (stale.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "stale", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(stale.Count, "enabled guest")} {(stale.Count == 1 ? "has" : "have")} no successful sign-in recorded in the last {threshold} days.",
                Columns, stale));
        else
            output.Add(AuditFindingBuilder.Pass(d, guests.Count == 0 ? "There are no enabled guest accounts." : $"All {AuditFindingBuilder.Count(guests.Count, "enabled guest")} signed in within the last {threshold} days or are newer than that."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(guests.Count, "enabled guest")}; threshold {threshold} days.");
        output.Note(SignInEvaluator.SemanticsNote);
        return output;
    }
}
