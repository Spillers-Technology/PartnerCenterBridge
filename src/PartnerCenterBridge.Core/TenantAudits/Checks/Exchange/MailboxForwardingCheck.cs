namespace PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;

/// <summary>Mailbox-level forwarding to addresses outside the organization, and the tenant's automatic-forwarding policy.</summary>
public sealed class MailboxForwardingCheck(IAuditMailboxData mailboxes) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("forwardTo", "Forwards to"), new("forwardingType", "Forwarding setting"), new("keepsCopy", "Keeps a copy"), new("mailboxType", "Mailbox type")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "mailbox-forwarding",
        Name = "External mailbox forwarding",
        Category = AuditCategories.Exchange,
        Description = "Mailboxes forwarding mail outside the organization, and whether the outbound policy allows automatic external forwarding.",
        BusinessImpact = "Forwarding to an outside address quietly copies company mail out of the tenant; it is a common sign of a compromised account.",
        Recommendation = "Confirm each external forward with the mailbox owner; remove any that are not documented and check the account for compromise.",
        SeverityRules =
        [
            "Fail: a mailbox forwards to an address outside the organization's accepted domains.",
            "Warn: the default outbound spam policy sets automatic forwarding to On.",
            "Info: mailboxes forwarding to internal addresses.",
            "Pass: no mailbox forwards externally and automatic external forwarding is not enabled."
        ],
        Requirements = [ExchangeRequirements.AppOnly],
        Limitations =
        [
            "Inbox rules that forward or redirect mail are not read (that needs a per-mailbox read); only the mailbox forwarding settings are.",
            "When automatic forwarding is blocked by policy, a configured forward may not actually deliver; it is still reported."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var report = await mailboxes.GetMailboxReportAsync(ctx);
        var output = new AuditCheckOutput();
        var domains = report.AcceptedDomains.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var external = new List<AuditSubject>();
        var internalFwd = new List<AuditSubject>();
        foreach (var m in report.Mailboxes)
        {
            foreach (var (address, kind) in Targets(m))
            {
                var isExternal = IsExternal(address, domains);
                var s = new AuditSubject
                {
                    Type = "mailbox", Id = m.ObjectId ?? m.UserPrincipalName, Name = m.DisplayName, Upn = m.UserPrincipalName,
                    Evidence = $"{kind} = {address}{(m.DeliverToMailboxAndForward ? " (keeps a copy)" : " (no copy kept)")}",
                    Properties =
                    {
                        ["forwardTo"] = address, ["forwardingType"] = kind,
                        ["keepsCopy"] = AuditFindingBuilder.YesNo(m.DeliverToMailboxAndForward), ["mailboxType"] = m.RecipientTypeDetails
                    }
                };
                (isExternal ? external : internalFwd).Add(s);
            }
        }

        if (external.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "external", AuditSeverity.Fail, "Mailboxes forwarding externally",
                $"{AuditFindingBuilder.Count(external.Count, "mailbox forward")} {(external.Count == 1 ? "sends" : "send")} mail to an address outside the accepted domains.",
                Columns, external));
        if (string.Equals(report.AutoForwardingMode, "On", StringComparison.OrdinalIgnoreCase))
            output.Add(AuditFindingBuilder.Create(d, "policy", AuditSeverity.Warn, "Automatic external forwarding allowed",
                "The default outbound spam filter policy sets automatic forwarding to On, so any user can forward mail outside the organization.",
                recommendation: "Set AutoForwardingMode to Off (or Automatic) and allow exceptions through a dedicated policy for the mailboxes that need it."));
        if (internalFwd.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "internal", AuditSeverity.Info, "Mailboxes forwarding internally",
                $"{AuditFindingBuilder.Count(internalFwd.Count, "mailbox forward")} {(internalFwd.Count == 1 ? "sends" : "send")} mail to another address in the organization.",
                Columns, internalFwd, businessImpact: "Internal forwards are usually intentional but are easy to forget after a role change.",
                recommendation: "Confirm the forwards are still wanted."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"None of the {AuditFindingBuilder.Count(report.Mailboxes.Count, "mailbox", "mailboxes")} forward mail, and automatic external forwarding is not enabled."));
        if (report.AutoForwardingMode is null) output.Note("The outbound spam filter policy could not be read, so automatic forwarding policy is not graded.");
        else output.Note($"Default outbound policy AutoForwardingMode: {report.AutoForwardingMode}.");
        output.Note($"Evaluated {AuditFindingBuilder.Count(report.Mailboxes.Count, "mailbox", "mailboxes")} against {AuditFindingBuilder.Count(domains.Count, "accepted domain")}.");
        ExchangeRequirements.AddPartialErrors(output, report);
        return output;
    }

    private static IEnumerable<(string Address, string Kind)> Targets(AuditMailbox m)
    {
        if (!string.IsNullOrWhiteSpace(m.ForwardingSmtpAddress))
            yield return (StripSmtp(m.ForwardingSmtpAddress), "ForwardingSmtpAddress");
        if (!string.IsNullOrWhiteSpace(m.ForwardingAddress))
            yield return (string.IsNullOrWhiteSpace(m.ForwardingAddressSmtp) ? m.ForwardingAddress : StripSmtp(m.ForwardingAddressSmtp), "ForwardingAddress");
    }

    private static string StripSmtp(string address) =>
        address.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;

    /// <summary>External when the domain is not an accepted domain. An address with no domain (an unresolved recipient name) is not called external.</summary>
    public static bool IsExternal(string address, IReadOnlySet<string> acceptedDomains)
    {
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1) return false;
        var domain = address[(at + 1)..].Trim();
        return !acceptedDomains.Contains(domain);
    }
}
