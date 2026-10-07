namespace PartnerCenterBridge.Core.TenantAudits.Checks.Licensing;

/// <summary>Sign-in-blocked accounts that still hold licenses that may carry cost.</summary>
public sealed class DisabledAccountsWithLicensesCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "disabled-accounts-with-licenses",
        Name = "Disabled accounts with licenses",
        Category = AuditCategories.Licensing,
        Description = "Accounts blocked from signing in that still hold licenses that may carry cost.",
        BusinessImpact = "Paying for licenses on accounts nobody can sign in to; often a sign of offboarding left half done.",
        Recommendation = "Remove the licenses unless a documented reason applies (mailbox conversion in progress, litigation hold, or retention).",
        SeverityRules =
        [
            "Warn: a disabled account holds a license that is not on the no-cost list.",
            "Pass: no disabled account holds such a license."
        ],
        Requirements = [AuditRequirement.Graph("User.Read.All"), AuditRequirement.Graph("Organization.Read.All", "Optional: license names.")],
        Limitations = ["A license can be legitimately kept on a disabled account, for example to keep a mailbox on litigation hold; PCB cannot see why it was kept."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var users = await directory.GetUsersAsync(ctx);
        var output = new AuditCheckOutput();
        var (skuNames, note) = await AuditLicenses.SkuNamesAsync(directory, ctx);
        if (note is not null) output.Note(note);

        var subjects = new List<AuditSubject>();
        foreach (var u in users.Users.Where(u => !u.AccountEnabled))
        {
            var paid = AuditLicenses.PossiblyPaid(u, skuNames);
            if (paid.Count == 0) continue;
            var s = AuditUserRows.Subject(u, $"Sign-in blocked; holds {string.Join(", ", paid)}.");
            s.Properties["licenses"] = string.Join("; ", paid);
            s.Properties["onPremisesSynced"] = AuditFindingBuilder.YesNo(u.OnPremisesSynced);
            s.Remediation = new AuditRemediation("offboarding", u.Id, "Plan offboarding", u.UserPrincipalName);
            subjects.Add(s);
        }

        var disabled = users.Users.Count(u => !u.AccountEnabled);
        output.Note($"Evaluated {AuditFindingBuilder.Count(disabled, "disabled account")}.");
        if (subjects.Count == 0)
            return output.Add(AuditFindingBuilder.Pass(d, "No disabled account holds a license that may carry cost."));
        return output.Add(AuditFindingBuilder.Create(d, "licensed", AuditSeverity.Warn, d.Name,
            $"{AuditFindingBuilder.Count(subjects.Count, "disabled account")} still {(subjects.Count == 1 ? "holds" : "hold")} licenses that may carry cost.",
            [new("licenses", "License / SKU(s)"), new("created", "Created"), new("onPremisesSynced", "Synced from on-premises")],
            subjects));
    }
}
