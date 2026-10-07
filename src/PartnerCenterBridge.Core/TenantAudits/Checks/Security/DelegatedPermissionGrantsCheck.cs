namespace PartnerCenterBridge.Core.TenantAudits.Checks.Security;

/// <summary>Third-party apps holding high-impact delegated permissions (OAuth consent grants).</summary>
public sealed class DelegatedPermissionGrantsCheck(IAuditSecurityData security) : ITenantAuditCheck
{
    /// <summary>Delegated scopes that reach mail, files, the directory or other people's data.</summary>
    public static readonly IReadOnlySet<string> HighImpactScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Mail.Read", "Mail.ReadWrite", "Mail.Send", "Mail.Read.Shared", "Mail.ReadWrite.Shared", "Mail.Send.Shared",
        "MailboxSettings.ReadWrite", "EWS.AccessAsUser.All", "full_access_as_user", "IMAP.AccessAsUser.All", "POP.AccessAsUser.All", "SMTP.Send",
        "Files.Read.All", "Files.ReadWrite.All", "Sites.Read.All", "Sites.ReadWrite.All", "Sites.Manage.All", "Sites.FullControl.All",
        "Directory.ReadWrite.All", "Directory.AccessAsUser.All", "User.ReadWrite.All", "Group.ReadWrite.All",
        "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All", "RoleManagement.ReadWrite.Directory",
        "Calendars.ReadWrite", "Contacts.ReadWrite", "Notes.ReadWrite.All", "Chat.ReadWrite", "ChannelMessage.Read.All"
    };

    private static readonly AuditColumn[] Columns =
        [new("app", "App"), new("publisher", "Publisher"), new("consent", "Consent"), new("resource", "API"), new("scopes", "High-impact permissions")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "delegated-permission-grants",
        Name = "App consent review",
        Category = AuditCategories.Security,
        Description = "Non-Microsoft apps granted high-impact delegated permissions, by an admin for everyone or by individual users.",
        BusinessImpact = "A consented app keeps access to the data it was granted until someone revokes it, even after the user who consented leaves.",
        Recommendation = "Confirm each app is known and still used; revoke consent for anything unrecognised and review sign-ins for it.",
        SeverityRules =
        [
            "Warn: an individual user consented to high-impact permissions for a non-Microsoft app.",
            "Info: an admin granted high-impact permissions to a non-Microsoft app for everyone.",
            "Pass: no non-Microsoft app holds high-impact delegated permissions."
        ],
        Requirements = [AuditRequirement.Graph("Directory.Read.All", "Delegated permission grants and the apps they belong to.")],
        Limitations =
        [
            "Application (app-only) permissions are not reviewed here; only delegated consents are.",
            "Apps published by Microsoft are excluded."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var grants = await security.GetDelegatedPermissionGrantsAsync(ctx);
        var output = new AuditCheckOutput();
        var userConsented = new List<AuditSubject>();
        var adminConsented = new List<AuditSubject>();
        foreach (var g in grants.Where(g => !g.ClientIsMicrosoft))
        {
            var risky = g.Scopes.Where(HighImpactScopes.Contains).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (risky.Count == 0) continue;
            var everyone = g.ConsentType.Equals("AllPrincipals", StringComparison.OrdinalIgnoreCase);
            var s = new AuditSubject
            {
                Type = "servicePrincipal", Id = g.Id, Name = g.ClientName,
                Evidence = $"{(everyone ? "Admin consent for all users" : $"User consent by {g.PrincipalId}")}: {string.Join(", ", risky)} on {g.ResourceName}.",
                Properties =
                {
                    ["app"] = g.ClientName, ["publisher"] = g.ClientPublisher, ["consent"] = everyone ? "All users (admin)" : $"One user ({g.PrincipalId})",
                    ["resource"] = g.ResourceName, ["scopes"] = string.Join("; ", risky)
                }
            };
            (everyone ? adminConsented : userConsented).Add(s);
        }
        if (userConsented.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "user-consented", AuditSeverity.Warn, "High-impact permissions consented by users",
                $"{AuditFindingBuilder.Count(userConsented.Count, "user consent grant")} {(userConsented.Count == 1 ? "gives" : "give")} a non-Microsoft app high-impact access.",
                Columns, userConsented));
        if (adminConsented.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "admin-consented", AuditSeverity.Info, "High-impact permissions consented for everyone",
                $"{AuditFindingBuilder.Count(adminConsented.Count, "tenant-wide grant")} {(adminConsented.Count == 1 ? "gives" : "give")} a non-Microsoft app high-impact access on behalf of every user.",
                Columns, adminConsented, recommendation: "Confirm each app is still in use and its vendor is approved."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, "No non-Microsoft app holds high-impact delegated permissions."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(grants.Count, "delegated permission grant")} ({grants.Count(g => g.ClientIsMicrosoft)} for Microsoft apps, excluded).");
        return output;
    }
}
