namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Who holds privileged directory roles, with the structural red flags called out.</summary>
public sealed class PrivilegedRoleInventoryCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns =
        [new("role", "Role"), new("principalType", "Type"), new("viaGroup", "Via group"), new("enabled", "Enabled")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "privileged-role-inventory",
        Name = "Privileged role review",
        Category = AuditCategories.Identity,
        Description = "Every active holder of a privileged directory role, with guest, disabled and Global Administrator count findings.",
        BusinessImpact = "Each privileged account can change security settings or read everyone's data; fewer, known holders mean less exposure.",
        Recommendation = "Confirm every holder still needs the role; keep two to four Global Administrators including emergency-access accounts.",
        SeverityRules =
        [
            "Fail: a guest account holds a privileged role.",
            "Warn: more than four Global Administrators; a disabled account still holds a privileged role.",
            "Info: the inventory itself; fewer than two Global Administrators; apps (service principals) holding privileged roles.",
        ],
        Requirements = [AuditRequirement.Graph("RoleManagement.Read.Directory"), AuditRequirement.Graph("User.Read.All", "Optional: guest and enabled state.")],
        Limitations = ["Only active role assignments are read; PIM-eligible assignments that are not activated are not included."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var members = (await directory.GetDirectoryRoleMembersAsync(ctx)).Where(m => m.IsPrivileged).ToList();
        var output = new AuditCheckOutput();
        Dictionary<string, AuditUser>? users = null;
        try { users = (await directory.GetUsersAsync(ctx)).Users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase); }
        catch (AuditUnavailableException ex) { output.Note($"Guest and disabled-account findings are not evaluated: {ex.Message}"); }

        AuditSubject Row(AuditRoleMember m)
        {
            AuditUser? u = null;
            users?.TryGetValue(m.PrincipalId, out u);
            return new AuditSubject
            {
                Type = m.PrincipalType, Id = m.PrincipalId, Name = m.PrincipalName, Upn = m.UserPrincipalName,
                Evidence = $"{m.RoleName}{(m.ViaGroup is null ? "" : $" via group {m.ViaGroup}")}",
                Properties =
                {
                    ["role"] = m.RoleName, ["principalType"] = m.PrincipalType, ["viaGroup"] = m.ViaGroup,
                    ["enabled"] = u is null ? null : AuditFindingBuilder.YesNo(u.AccountEnabled)
                }
            };
        }

        AuditUser? UserOf(AuditRoleMember m) => users is not null && users.TryGetValue(m.PrincipalId, out var u) ? u : null;

        var guests = members.Where(m => UserOf(m)?.IsGuest == true).Select(Row).ToList();
        if (guests.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "guests", AuditSeverity.Fail, "Guests holding privileged roles",
                $"{AuditFindingBuilder.Count(guests.Count, "guest role assignment")} {(guests.Count == 1 ? "grants" : "grant")} privileged access to an account from another organization.",
                Columns, guests, businessImpact: "Someone outside the organization, under another organization's security controls, can administer this tenant.",
                recommendation: "Remove the role, or replace the guest with an internal account if the access is genuinely needed."));

        var disabled = members.Where(m => UserOf(m) is { AccountEnabled: false }).Select(Row).ToList();
        if (disabled.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "disabled", AuditSeverity.Warn, "Disabled accounts still holding privileged roles",
                $"{AuditFindingBuilder.Count(disabled.Count, "privileged role assignment")} {(disabled.Count == 1 ? "belongs" : "belong")} to disabled accounts.",
                Columns, disabled, recommendation: "Remove the roles so re-enabling the account does not silently restore admin access."));

        var globalAdmins = members.Where(m => m.RoleTemplateId.Equals(PrivilegedRoles.GlobalAdministrator, StringComparison.OrdinalIgnoreCase) && m.PrincipalType == "user")
            .DistinctBy(m => m.PrincipalId).ToList();
        if (globalAdmins.Count > 4)
            output.Add(AuditFindingBuilder.Create(d, "too-many-global-admins", AuditSeverity.Warn, "More than four Global Administrators",
                $"{globalAdmins.Count} users hold Global Administrator; Microsoft recommends fewer than five.",
                Columns, globalAdmins.Select(Row),
                recommendation: "Move day-to-day admins to narrower roles (User, Exchange, Intune Administrator) and keep Global Administrator for a few accounts."));
        else if (globalAdmins.Count < 2)
            output.Add(AuditFindingBuilder.Create(d, "too-few-global-admins", AuditSeverity.Info, "Fewer than two Global Administrators",
                $"{AuditFindingBuilder.Count(globalAdmins.Count, "user")} {(globalAdmins.Count == 1 ? "holds" : "hold")} Global Administrator.",
                Columns, globalAdmins.Select(Row),
                businessImpact: "If the only admin is locked out, nobody can recover the tenant without Microsoft support.",
                recommendation: "Keep a second, emergency-access Global Administrator account."));

        var apps = members.Where(m => m.PrincipalType == "servicePrincipal").Select(Row).ToList();
        if (apps.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "apps", AuditSeverity.Info, "Apps holding privileged roles",
                $"{AuditFindingBuilder.Count(apps.Count, "app role assignment")} {(apps.Count == 1 ? "gives" : "give")} an application privileged directory access.",
                Columns, apps, recommendation: "Confirm each app is known and still in use; its credentials are as powerful as an admin account."));

        output.Add(AuditFindingBuilder.Create(d, "inventory", AuditSeverity.Info, "Privileged role holders",
            $"{AuditFindingBuilder.Count(members.Count, "privileged role assignment")} across {AuditFindingBuilder.Count(members.Select(m => m.RoleName).Distinct().Count(), "role")}.",
            Columns, members.Select(Row),
            businessImpact: "This is the list to review: every entry can change tenant-wide settings.",
            recommendation: "Review this list with the customer at least quarterly."));
        return output;
    }
}
