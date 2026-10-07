namespace PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;

/// <summary>User mailboxes without an online archive, and holds in place.</summary>
public sealed class MailboxArchiveCheck(IAuditMailboxData mailboxes) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns = [new("archive", "Archive"), new("autoExpanding", "Auto-expanding"), new("litigationHold", "Litigation hold")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "mailbox-archive-state",
        Name = "Mailbox archive state",
        Category = AuditCategories.Exchange,
        Description = "User mailboxes with no online archive, and mailboxes on litigation hold.",
        BusinessImpact = "Without an archive, a full mailbox stops receiving mail; holds keep data (and may require a license) long after a user leaves.",
        Recommendation = "Enable the archive where the license includes it, using the mailbox archive known fix.",
        SeverityRules =
        [
            "Info: user mailboxes without an archive; mailboxes on litigation hold.",
            "Pass: every user mailbox has an archive."
        ],
        Requirements = [ExchangeRequirements.AppOnly],
        Limitations = ["Mailbox sizes are not read, so this does not say which mailboxes are close to full; open the mailbox archive known fix for a specific mailbox's numbers."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var report = await mailboxes.GetMailboxReportAsync(ctx);
        var output = new AuditCheckOutput();
        AuditSubject Row(AuditMailbox m) => new()
        {
            Type = "mailbox", Id = m.ObjectId ?? m.UserPrincipalName, Name = m.DisplayName, Upn = m.UserPrincipalName,
            Evidence = m.ArchiveEnabled ? $"Archive {m.ArchiveStatus}" : "No archive",
            Properties =
            {
                ["archive"] = AuditFindingBuilder.YesNo(m.ArchiveEnabled),
                ["autoExpanding"] = AuditFindingBuilder.YesNo(m.AutoExpandingArchiveEnabled),
                ["litigationHold"] = AuditFindingBuilder.YesNo(m.LitigationHoldEnabled)
            }
        };

        var userMailboxes = report.Mailboxes.Where(m => m.RecipientTypeDetails.Equals("UserMailbox", StringComparison.OrdinalIgnoreCase)).ToList();
        var noArchive = userMailboxes.Where(m => !m.ArchiveEnabled).Select(m =>
        {
            var s = Row(m);
            s.Remediation = new AuditRemediation("workflow", "mailbox-archive", "Open mailbox archive fix", m.UserPrincipalName);
            return s;
        }).ToList();
        if (noArchive.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "no-archive", AuditSeverity.Info, "User mailboxes without an archive",
                $"{AuditFindingBuilder.Count(noArchive.Count, "user mailbox", "user mailboxes")} of {userMailboxes.Count} {(noArchive.Count == 1 ? "has" : "have")} no online archive.",
                Columns, noArchive));
        var holds = report.Mailboxes.Where(m => m.LitigationHoldEnabled).Select(Row).ToList();
        if (holds.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "litigation-hold", AuditSeverity.Info, "Mailboxes on litigation hold",
                $"{AuditFindingBuilder.Count(holds.Count, "mailbox", "mailboxes")} {(holds.Count == 1 ? "is" : "are")} on litigation hold.",
                Columns, holds,
                businessImpact: "Held mailboxes keep all content and need a license that includes holds.",
                recommendation: "Confirm each hold is still required by the customer's legal or retention obligations."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(userMailboxes.Count, "user mailbox", "user mailboxes")} have an archive."));
        ExchangeRequirements.AddPartialErrors(output, report);
        return output;
    }
}
