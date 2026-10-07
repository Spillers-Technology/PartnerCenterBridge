namespace PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;

/// <summary>Shared mailboxes (from Exchange Online) whose user object holds licenses (from Graph).</summary>
public sealed class SharedMailboxLicensesCheck(IAuditDirectoryData directory, IAuditMailboxData mailboxes) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "shared-mailbox-licenses",
        Name = "Licensed shared mailboxes",
        Category = AuditCategories.Licensing,
        Description = "Shared mailboxes whose account holds licenses that may carry cost.",
        BusinessImpact = "A shared mailbox usually needs no license; one assigned anyway is recurring spend.",
        Recommendation = "Remove the license unless the mailbox is over 50 GB, has an archive, or is on litigation hold.",
        SeverityRules =
        [
            "Warn: a shared mailbox with no archive and no litigation hold holds a possibly paid license.",
            "Info: a licensed shared mailbox that has an archive or litigation hold (the license is likely needed).",
            "Pass: no shared mailbox holds a possibly paid license."
        ],
        Requirements =
        [
            AuditRequirement.Dependency("Exchange Online app-only access", "Configured under Settings > Workbench; the app needs Exchange.ManageAsApp and a role that can read mailboxes."),
            AuditRequirement.Graph("User.Read.All"),
            AuditRequirement.Graph("Organization.Read.All", "Optional: license names.")
        ],
        Limitations = ["Mailbox size is not read, so a shared mailbox over 50 GB (which does need a license) can appear in the Warn finding."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var report = await mailboxes.GetMailboxReportAsync(ctx);
        var users = await directory.GetUsersAsync(ctx);
        var output = new AuditCheckOutput();
        var (skuNames, note) = await AuditLicenses.SkuNamesAsync(directory, ctx);
        if (note is not null) output.Note(note);
        var byId = users.Users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);

        var plain = new List<AuditSubject>();
        var justified = new List<AuditSubject>();
        var shared = report.Mailboxes.Where(m => m.IsShared).ToList();
        foreach (var m in shared)
        {
            if (m.ObjectId is null || !byId.TryGetValue(m.ObjectId, out var u)) continue;
            var paid = AuditLicenses.PossiblyPaid(u, skuNames);
            if (paid.Count == 0) continue;
            var s = new AuditSubject
            {
                Type = "mailbox", Id = m.ObjectId, Name = m.DisplayName, Upn = m.UserPrincipalName,
                Properties =
                {
                    ["licenses"] = string.Join("; ", paid),
                    ["archive"] = AuditFindingBuilder.YesNo(m.ArchiveEnabled),
                    ["litigationHold"] = AuditFindingBuilder.YesNo(m.LitigationHoldEnabled),
                    ["signInEnabled"] = AuditFindingBuilder.YesNo(u.AccountEnabled)
                }
            };
            if (m.ArchiveEnabled || m.LitigationHoldEnabled)
            {
                s.Evidence = "Licensed; has an archive or litigation hold, which needs a license.";
                justified.Add(s);
            }
            else
            {
                s.Evidence = $"Licensed ({string.Join(", ", paid)}) with no archive and no litigation hold.";
                plain.Add(s);
            }
        }

        AuditColumn[] columns = [new("licenses", "License / SKU(s)"), new("archive", "Archive"), new("litigationHold", "Litigation hold"), new("signInEnabled", "Sign-in enabled")];
        if (plain.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "unneeded", AuditSeverity.Warn, d.Name,
                $"{AuditFindingBuilder.Count(plain.Count, "shared mailbox", "shared mailboxes")} without an archive or hold {(plain.Count == 1 ? "holds" : "hold")} licenses that may carry cost.",
                columns, plain));
        if (justified.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "justified", AuditSeverity.Info, "Licensed shared mailboxes with archive or hold",
                $"{AuditFindingBuilder.Count(justified.Count, "licensed shared mailbox", "licensed shared mailboxes")} {(justified.Count == 1 ? "has" : "have")} an archive or litigation hold, which needs a license.",
                columns, justified,
                recommendation: "No action needed if the archive or hold is still required; confirm the license is the cheapest one that covers it."));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"None of the {AuditFindingBuilder.Count(shared.Count, "shared mailbox", "shared mailboxes")} holds a license that may carry cost."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(shared.Count, "shared mailbox", "shared mailboxes")}.");
        foreach (var e in report.PartialErrors) output.Note($"Exchange read incomplete: {e}");
        return output;
    }
}
