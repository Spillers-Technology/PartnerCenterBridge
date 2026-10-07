namespace PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;

/// <summary>Who can open or send as other people's mailboxes.</summary>
public sealed class MailboxDelegationCheck(IAuditMailboxData mailboxes) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns = [new("right", "Permission"), new("trustee", "Granted to"), new("mailboxType", "Mailbox type")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "mailbox-delegation",
        Name = "Mailbox delegation review",
        Category = AuditCategories.Exchange,
        Description = "Full Access, Send As and Send on Behalf permissions on user mailboxes, and the same on shared mailboxes for reference.",
        BusinessImpact = "Delegated access to a person's mailbox lets someone read or send their mail; old grants survive role changes.",
        Recommendation = "Review delegations on user mailboxes with the customer and remove ones that are no longer needed.",
        SeverityRules =
        [
            "Info: delegations on user mailboxes (worth reviewing) and on shared mailboxes (expected).",
            "Pass: no delegations found."
        ],
        Requirements = [ExchangeRequirements.AppOnly],
        Limitations =
        [
            "Full Access is read one mailbox at a time and is bounded in large tenants; the notes say when it was not read for every mailbox.",
            "Folder-level permissions (for example calendar delegates) are not read."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var report = await mailboxes.GetMailboxReportAsync(ctx);
        var output = new AuditCheckOutput();
        // Send As grants name the mailbox by its recipient identity (often the display name or
        // alias), Full Access by UPN: index every name a grant may use.
        var byName = new Dictionary<string, AuditMailbox>(StringComparer.OrdinalIgnoreCase);
        foreach (var mbx in report.Mailboxes)
            foreach (var key in new[] { mbx.UserPrincipalName, mbx.PrimarySmtpAddress, mbx.DisplayName })
                if (!string.IsNullOrEmpty(key)) byName.TryAdd(key, mbx);

        var grants = report.Permissions.Select(p => (Mailbox: p.Mailbox, p.Trustee, p.Right))
            .Concat(report.Mailboxes.SelectMany(m => m.GrantSendOnBehalfTo.Select(t => (Mailbox: m.UserPrincipalName, Trustee: t, Right: "SendOnBehalf"))))
            .ToList();

        var onUsers = new List<AuditSubject>();
        var onShared = new List<AuditSubject>();
        foreach (var g in grants)
        {
            byName.TryGetValue(g.Mailbox, out var m);
            var type = m?.RecipientTypeDetails ?? "Unknown";
            var s = new AuditSubject
            {
                Type = "mailbox", Id = $"{g.Mailbox}|{g.Right}|{g.Trustee}", Name = m?.DisplayName ?? g.Mailbox, Upn = m?.UserPrincipalName ?? g.Mailbox,
                Evidence = $"{g.Trustee} has {g.Right}",
                Properties = { ["right"] = g.Right, ["trustee"] = g.Trustee, ["mailboxType"] = type }
            };
            (m is { IsShared: true } || m is { IsResource: true } ? onShared : onUsers).Add(s);
        }

        if (onUsers.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "user-mailboxes", AuditSeverity.Info, "Delegations on user mailboxes",
                $"{AuditFindingBuilder.Count(onUsers.Count, "delegation")} on user mailboxes.", Columns, onUsers));
        if (onShared.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "shared-mailboxes", AuditSeverity.Info, "Delegations on shared and resource mailboxes",
                $"{AuditFindingBuilder.Count(onShared.Count, "delegation")} on shared or resource mailboxes.", Columns, onShared,
                businessImpact: "Expected for shared mailboxes; the list shows who can read and send from each one.",
                recommendation: "Confirm the members are still right for each shared mailbox."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, "No mailbox delegations were found."));
        output.Note(report.FullAccessComplete
            ? $"Full Access was read for all {AuditFindingBuilder.Count(report.FullAccessEvaluated, "mailbox", "mailboxes")}."
            : $"Full Access was read for {report.FullAccessEvaluated} of {AuditFindingBuilder.Count(report.Mailboxes.Count, "mailbox", "mailboxes")}; delegations on the rest are not shown.");
        ExchangeRequirements.AddPartialErrors(output, report);
        return output;
    }
}
