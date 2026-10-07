namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Guest inventory and invitations that were never accepted.</summary>
public sealed class GuestAccountsCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    /// <summary>An invitation pending longer than this is reported.</summary>
    public const int PendingInvitationDays = 30;

    private static readonly AuditColumn[] Columns =
        [new("enabled", "Enabled"), new("invitationState", "Invitation state"), new("stateChanged", "State changed"), new("created", "Created")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "guest-accounts",
        Name = "Guest account review",
        Category = AuditCategories.Identity,
        Description = "Every guest (external) account in the directory, with invitations that were never accepted.",
        BusinessImpact = "Guests can reach shared files, Teams and apps; forgotten guests keep that access after the project ends.",
        Recommendation = "Review the guest list with the customer; remove guests nobody can vouch for and set up guest access reviews.",
        SeverityRules =
        [
            $"Warn: an invitation has been pending for more than 30 days.",
            "Info: the guest inventory itself.",
            "Pass: there are no guest accounts."
        ],
        Requirements = [AuditRequirement.Graph("User.Read.All")],
        Limitations = ["What each guest can reach (teams, sites, apps) is not evaluated."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var guests = (await directory.GetUsersAsync(ctx)).Users.Where(u => u.IsGuest).ToList();
        var output = new AuditCheckOutput();
        if (guests.Count == 0) return output.Add(AuditFindingBuilder.Pass(d, "There are no guest accounts."));

        AuditSubject Row(AuditUser u)
        {
            var s = AuditUserRows.Subject(u, u.ExternalUserState is null ? null : $"Invitation {u.ExternalUserState}.");
            s.Properties["invitationState"] = u.ExternalUserState;
            s.Properties["stateChanged"] = AuditFindingBuilder.Date(u.ExternalUserStateChangedAt);
            return s;
        }

        var pending = guests.Where(u => string.Equals(u.ExternalUserState, "PendingAcceptance", StringComparison.OrdinalIgnoreCase)
                                        && (u.ExternalUserStateChangedAt ?? u.CreatedAt) is { } since
                                        && SignInEvaluator.DaysBetween(since, ctx.Now) > PendingInvitationDays).ToList();
        if (pending.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "pending", AuditSeverity.Warn, "Guest invitations never accepted",
                $"{AuditFindingBuilder.Count(pending.Count, "invitation")} {(pending.Count == 1 ? "has" : "have")} been pending for more than {PendingInvitationDays} days.",
                Columns, pending.Select(Row),
                businessImpact: "An unaccepted invitation can still be redeemed by whoever holds the invitation link or mailbox.",
                recommendation: "Delete invitations that are no longer expected to be accepted."));
        output.Add(AuditFindingBuilder.Create(d, "inventory", AuditSeverity.Info, "Guest accounts",
            $"{AuditFindingBuilder.Count(guests.Count, "guest account")} ({guests.Count(g => g.AccountEnabled)} enabled).",
            Columns, guests.Select(Row)));
        return output;
    }
}
