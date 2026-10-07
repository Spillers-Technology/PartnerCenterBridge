namespace PartnerCenterBridge.Core.TenantAudits.Checks.Security;

/// <summary>Users, groups and roles excluded from enabled Conditional Access policies.</summary>
public sealed class ConditionalAccessExclusionsCheck(IAuditSecurityData security) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns = [new("kind", "Excluded"), new("policy", "Policy"), new("policyRequires", "Policy enforces")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "conditional-access-exclusions",
        Name = "Conditional Access exclusions",
        Category = AuditCategories.Security,
        Description = "Every user, group, role and guest exclusion on enabled Conditional Access policies.",
        BusinessImpact = "Excluded accounts skip the policy entirely; exclusions are where MFA gaps hide.",
        Recommendation = "Keep exclusions to documented emergency-access accounts; remove the rest or replace them with narrower policies.",
        SeverityRules =
        [
            "Warn: an exclusion on an enabled policy that requires MFA or blocks access for all users.",
            "Info: exclusions on other enabled policies.",
            "Pass: no enabled policy has exclusions."
        ],
        Requirements = [AuditRequirement.Graph("Policy.Read.All"), AuditRequirement.License("Microsoft Entra ID P1 or P2"), AuditRequirement.Graph("Directory.Read.All", "Optional: names of excluded users and groups.")],
        Limitations = ["Group exclusions are listed as the group; its members are not expanded."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var ca = await security.GetConditionalAccessAsync(ctx);
        var output = new AuditCheckOutput();
        var important = new List<AuditSubject>();
        var other = new List<AuditSubject>();
        foreach (var p in ca.Policies.Where(p => p.IsEnabled))
        {
            var critical = p.TargetsAllUsers && (p.RequiresMfa || p.Blocks);
            var enforces = p.Blocks ? "Block" : p.RequiresMfa ? "MFA" : string.Join("; ", p.BuiltInControls);
            IEnumerable<(string Kind, string Id)> exclusions =
                p.ExcludeUsers.Select(id => ("User", id))
                    .Concat(p.ExcludeGroups.Select(id => ("Group", id)))
                    .Concat(p.ExcludeRoles.Select(id => ("Role", id)));
            if (p.ExcludesGuestsOrExternalUsers) exclusions = exclusions.Append(("Guests or external users", "guests"));
            foreach (var (kind, id) in exclusions)
            {
                var name = ca.Names.TryGetValue(id, out var n) ? n
                    : kind == "Role" && PrivilegedRoles.ByTemplateId.TryGetValue(id, out var r) ? r
                    : kind == "Guests or external users" ? "Guests or external users" : id;
                var s = new AuditSubject
                {
                    Type = kind.ToLowerInvariant(), Id = $"{p.Id}|{id}", Name = name,
                    Evidence = $"Excluded from '{p.DisplayName}' ({enforces}).",
                    Properties = { ["kind"] = kind, ["policy"] = p.DisplayName, ["policyRequires"] = enforces }
                };
                (critical ? important : other).Add(s);
            }
        }
        if (important.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "baseline-exclusions", AuditSeverity.Warn, "Exclusions from all-user MFA or block policies",
                $"{AuditFindingBuilder.Count(important.Count, "exclusion")} on enabled policies that require MFA or block access for all users.", Columns, important));
        if (other.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "other-exclusions", AuditSeverity.Info, "Exclusions on other policies",
                $"{AuditFindingBuilder.Count(other.Count, "exclusion")} on other enabled policies.", Columns, other));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, "No enabled Conditional Access policy has exclusions."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(ca.Policies.Count(p => p.IsEnabled), "enabled policy", "enabled policies")}.");
        return output;
    }
}
