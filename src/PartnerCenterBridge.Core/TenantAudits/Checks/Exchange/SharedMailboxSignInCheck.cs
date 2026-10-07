namespace PartnerCenterBridge.Core.TenantAudits.Checks.Exchange;

/// <summary>Shared mailboxes whose underlying account can still sign in.</summary>
public sealed class SharedMailboxSignInCheck(IAuditMailboxData mailboxes, IAuditDirectoryData directory) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "shared-mailbox-sign-in",
        Name = "Shared mailbox sign-in",
        Category = AuditCategories.Exchange,
        Description = "Shared mailboxes whose user account is not blocked from signing in.",
        BusinessImpact = "A shared mailbox account that can sign in has a password nobody owns, and no one notices if it is used.",
        Recommendation = "Block sign-in on shared mailbox accounts; people reach the mailbox through their own delegated access.",
        SeverityRules =
        [
            "Warn: a shared mailbox's account is enabled for sign-in.",
            "Pass: every shared mailbox account is blocked from sign-in."
        ],
        Requirements = [ExchangeRequirements.AppOnly, AuditRequirement.Graph("User.Read.All")]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var report = await mailboxes.GetMailboxReportAsync(ctx);
        var users = (await directory.GetUsersAsync(ctx)).Users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var output = new AuditCheckOutput();
        var shared = report.Mailboxes.Where(m => m.IsShared).ToList();
        var enabled = shared
            .Where(m => m.ObjectId is not null && users.TryGetValue(m.ObjectId, out var u) && u.AccountEnabled)
            .Select(m => new AuditSubject
            {
                Type = "mailbox", Id = m.ObjectId!, Name = m.DisplayName, Upn = m.UserPrincipalName,
                Evidence = "Shared mailbox account has accountEnabled = true.",
                Properties = { ["primarySmtp"] = m.PrimarySmtpAddress }
            }).ToList();
        if (enabled.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "enabled", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(enabled.Count, "shared mailbox", "shared mailboxes")} can be signed in to directly.",
                [new("primarySmtp", "Primary address")], enabled));
        else
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(shared.Count, "shared mailbox", "shared mailboxes")} are blocked from sign-in."));
        var unmatched = shared.Count(m => m.ObjectId is null || !users.ContainsKey(m.ObjectId));
        if (unmatched > 0) output.Note($"{AuditFindingBuilder.Count(unmatched, "shared mailbox", "shared mailboxes")} could not be matched to a directory account and {(unmatched == 1 ? "was" : "were")} not graded.");
        ExchangeRequirements.AddPartialErrors(output, report);
        return output;
    }
}
