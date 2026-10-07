namespace PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;

/// <summary>
/// Licensed users with no successful sign-in within the inactivity threshold -- the flagship
/// license-spend check. Users are split by what the sign-in data actually proves (see
/// <see cref="SignInEvaluator"/>) rather than lumped into one "inactive" bucket, and shared or
/// resource mailboxes (which never sign in by design) are listed separately when Exchange Online
/// data is available.
/// </summary>
public sealed class DormantLicensedUsersCheck : ITenantAuditCheck
{
    public const string Id = "dormant-licensed-users";

    private static readonly AuditColumn[] UserColumns =
    [
        new("enabled", "Enabled"),
        new("licenses", "License / SKU(s)"),
        .. SignInEvaluator.Columns,
        new("created", "Created"),
        new("adminRoles", "Admin roles"),
        new("mailboxType", "Mailbox type")
    ];

    private readonly IAuditDirectoryData _directory;
    private readonly IAuditMailboxData _mailboxes;

    public DormantLicensedUsersCheck(IAuditDirectoryData directory, IAuditMailboxData mailboxes)
    {
        _directory = directory;
        _mailboxes = mailboxes;
    }

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        Name = "Dormant licensed users",
        Category = AuditCategories.Licensing,
        Description = "Users holding licenses that may carry cost who have not signed in successfully within the inactivity threshold.",
        BusinessImpact = "Potential unnecessary recurring Microsoft 365 spend and possible incomplete offboarding.",
        Recommendation = "Review these users for license removal, downgrade, offboarding, or documented retention.",
        SeverityRules =
        [
            "Warn: no successful sign-in recorded for at least the threshold, or no sign-in recorded at all on an account older than the threshold.",
            "Unknown: recent sign-in attempts, but no successful sign-in recorded -- PCB cannot tell whether the account is in use.",
            "Info: licensed accounts created within the threshold that have not signed in yet; licensed shared/resource mailboxes.",
            "Pass: every licensed user signed in successfully within the threshold."
        ],
        Requirements =
        [
            AuditRequirement.Graph("User.Read.All"),
            AuditRequirement.Graph("AuditLog.Read.All", "Needed to read signInActivity."),
            AuditRequirement.License("Microsoft Entra ID P1 or P2", "signInActivity is only returned for tenants with Entra ID P1/P2."),
            AuditRequirement.Graph("Organization.Read.All", "Optional: license names (subscribedSkus)."),
            AuditRequirement.Graph("RoleManagement.Read.Directory", "Optional: admin role indicator."),
            AuditRequirement.Dependency("Exchange Online (optional)", "Distinguishes shared and resource mailboxes.")
        ],
        Parameters = [AuditParameterKeys.InactiveDays],
        Limitations =
        [
            "Licenses on PCB's no-cost list (for example FLOW_FREE, POWER_BI_STANDARD, TEAMS_EXPLORATORY) are ignored; any other SKU is treated as possibly paid.",
            "Group-based licensing is reported as assigned; removing it means changing group membership."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var threshold = ctx.Parameters.Get(AuditParameterKeys.InactiveDays);
        var users = await _directory.GetUsersAsync(ctx);
        users.RequireSignIn();

        var output = new AuditCheckOutput();
        var (skuNames, skuNote) = await AuditLicenses.SkuNamesAsync(_directory, ctx);
        if (skuNote is not null) output.Note(skuNote);
        var adminRoles = await AdminRolesAsync(ctx, output);
        var mailboxTypes = await MailboxTypesAsync(ctx, output);

        var licensed = users.Users
            .Select(u => (User: u, Licenses: AuditLicenses.PossiblyPaid(u, skuNames)))
            .Where(x => x.Licenses.Count > 0)
            .ToList();

        var dormant = new List<AuditSubject>();
        var never = new List<AuditSubject>();
        var unconfirmed = new List<AuditSubject>();
        var newAccounts = new List<AuditSubject>();
        var sharedOrResource = new List<AuditSubject>();

        foreach (var (user, licenses) in licensed)
        {
            var a = SignInEvaluator.Assess(user, ctx.Now, threshold);
            var subject = AuditUserRows.Subject(user);
            subject.Properties["licenses"] = string.Join("; ", licenses);
            subject.Properties["adminRoles"] = adminRoles?.GetValueOrDefault(user.Id);
            var mailboxType = mailboxTypes?.GetValueOrDefault(user.Id);
            subject.Properties["mailboxType"] = mailboxTypes is null ? null : mailboxType ?? "None";
            SignInEvaluator.Fill(subject, a);

            if (mailboxType is "SharedMailbox" or "RoomMailbox" or "EquipmentMailbox")
            {
                subject.Evidence = $"{mailboxType} holding {AuditFindingBuilder.Count(licenses.Count, "license")}";
                sharedOrResource.Add(subject);
                continue;
            }

            switch (a.Standing)
            {
                case SignInStanding.InactiveSinceSuccess:
                    subject.Evidence = $"Last successful sign-in {AuditFindingBuilder.Date(a.LastSuccessful)} ({a.DaysInactive} days ago).";
                    Offboard(subject, user);
                    dormant.Add(subject);
                    break;
                case SignInStanding.InactiveSinceAttempt:
                    subject.Evidence = $"No successful sign-in recorded; last attempt {AuditFindingBuilder.Date(a.LastAttempt)} ({a.DaysInactive} days ago).";
                    Offboard(subject, user);
                    dormant.Add(subject);
                    break;
                case SignInStanding.NeverSignedIn:
                    subject.Evidence = "No sign-in of any kind recorded by Microsoft Entra.";
                    Offboard(subject, user);
                    never.Add(subject);
                    break;
                case SignInStanding.AttemptsWithoutSuccess:
                    subject.Evidence = $"Sign-in attempt on {AuditFindingBuilder.Date(a.LastAttempt)}, but no successful sign-in is recorded.";
                    unconfirmed.Add(subject);
                    break;
                case SignInStanding.NewAccount:
                    subject.Evidence = $"Created {AuditFindingBuilder.Date(user.CreatedAt)}; no sign-in yet.";
                    newAccounts.Add(subject);
                    break;
            }
        }

        if (dormant.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "dormant", AuditSeverity.Warn, "Dormant licensed users",
                $"{AuditFindingBuilder.Count(dormant.Count, "licensed user")} {(dormant.Count == 1 ? "has" : "have")} no successful sign-in recorded in the last {threshold} days.",
                UserColumns, dormant));
        if (never.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "never-signed-in", AuditSeverity.Warn, "Licensed users with no recorded sign-in",
                $"{AuditFindingBuilder.Count(never.Count, "licensed user")} older than {threshold} days {(never.Count == 1 ? "has" : "have")} no sign-in recorded (never signed in, or not since Microsoft began recording sign-in activity).",
                UserColumns, never));
        if (unconfirmed.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unconfirmed", AuditSeverity.Unknown, "Sign-in success not confirmed",
                $"{AuditFindingBuilder.Count(unconfirmed.Count, "licensed user")} had recent sign-in attempts, but no successful sign-in is recorded, so PCB cannot tell whether the account is in use.",
                UserColumns, unconfirmed,
                recommendation: "Check these accounts' sign-in logs: repeated failures can mean a forgotten account, or someone trying to get into it."));
        if (newAccounts.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "new-accounts", AuditSeverity.Info, "New licensed accounts not used yet",
                $"{AuditFindingBuilder.Count(newAccounts.Count, "licensed account")} created in the last {threshold} days {(newAccounts.Count == 1 ? "has" : "have")} not signed in yet.",
                UserColumns, newAccounts,
                businessImpact: "Licenses are being paid for accounts nobody has used yet.",
                recommendation: "Confirm the new hires have started; otherwise hold the license until they do."));
        if (sharedOrResource.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "shared-or-resource", AuditSeverity.Info, "Licensed shared or resource mailboxes",
                $"{AuditFindingBuilder.Count(sharedOrResource.Count, "shared or resource mailbox", "shared or resource mailboxes")} {(sharedOrResource.Count == 1 ? "holds" : "hold")} licenses. They do not sign in, so they are not graded on sign-in activity.",
                UserColumns, sharedOrResource,
                businessImpact: "Shared and resource mailboxes often do not need a paid license.",
                recommendation: "See the 'Licensed shared mailboxes' check: a license is only needed for a larger mailbox, an archive, or a hold."));

        if (dormant.Count + never.Count + unconfirmed.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d,
                licensed.Count == 0
                    ? "No users hold licenses that may carry cost."
                    : $"All {AuditFindingBuilder.Count(licensed.Count, "licensed user")} not otherwise listed signed in successfully within the last {threshold} days."));

        output.Note($"Evaluated {AuditFindingBuilder.Count(licensed.Count, "user")} holding at least one license that may carry cost (of {AuditFindingBuilder.Count(users.Users.Count, "user")} in the directory); threshold {threshold} days.");
        output.Note(SignInEvaluator.SemanticsNote);
        return output;
    }

    private static void Offboard(AuditSubject subject, AuditUser user) =>
        subject.Remediation = new AuditRemediation("offboarding", user.Id, "Plan offboarding", user.UserPrincipalName);

    /// <summary>userId -> "Role A; Role B", or null (with a note) when roles cannot be read.</summary>
    private async Task<Dictionary<string, string>?> AdminRolesAsync(AuditCheckContext ctx, AuditCheckOutput output)
    {
        try
        {
            var members = await _directory.GetDirectoryRoleMembersAsync(ctx);
            return members.Where(m => m.PrincipalType == "user")
                .GroupBy(m => m.PrincipalId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(m => m.RoleName).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (AuditUnavailableException ex)
        {
            output.Note($"Admin roles are not shown: {ex.Message}");
            return null;
        }
    }

    /// <summary>userId -> RecipientTypeDetails, or null (with a note) when Exchange data is not available.</summary>
    private async Task<Dictionary<string, string>?> MailboxTypesAsync(AuditCheckContext ctx, AuditCheckOutput output)
    {
        if (_mailboxes.UnavailableReason is { } reason)
        {
            output.Note($"Shared and resource mailboxes are not distinguished: {reason}");
            return null;
        }
        try
        {
            var report = await _mailboxes.GetMailboxReportAsync(ctx);
            return report.Mailboxes.Where(m => !string.IsNullOrEmpty(m.ObjectId))
                .GroupBy(m => m.ObjectId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().RecipientTypeDetails, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enrichment only: a failed Exchange read must not take the license check down with it.
            output.Note($"Shared and resource mailboxes are not distinguished: Exchange Online data could not be read ({ex.Message}).");
            return null;
        }
    }
}
