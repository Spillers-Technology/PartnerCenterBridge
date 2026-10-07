namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>Enabled users with no multifactor method registered.</summary>
public sealed class MfaRegistrationCoverageCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    private static readonly AuditColumn[] Columns = [new("isAdmin", "Admin"), new("userType", "User type"), new("methods", "Registered methods")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "mfa-registration-coverage",
        Name = "MFA registration coverage",
        Category = AuditCategories.Identity,
        Description = "Enabled member users who have not registered a method that can satisfy multifactor authentication.",
        BusinessImpact = "An account without MFA can be taken over with a stolen or guessed password.",
        Recommendation = "Require registration (Conditional Access or security defaults) and follow up with these users.",
        SeverityRules =
        [
            "Fail: an admin has no MFA-capable method registered.",
            "Warn: an enabled member user has no MFA-capable method registered.",
            "Pass: every enabled member user is MFA capable."
        ],
        Requirements =
        [
            AuditRequirement.Graph("AuditLog.Read.All", "Authentication methods registration report."),
            AuditRequirement.License("Microsoft Entra ID P1 or P2"),
            AuditRequirement.Graph("User.Read.All", "Optional: excludes disabled accounts.")
        ],
        Limitations =
        [
            "Registration is not enforcement: a registered user is only prompted when Conditional Access or security defaults require it (see the Conditional Access baseline check).",
            "The registration report is refreshed by Microsoft periodically, not in real time."
        ]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var registrations = await directory.GetAuthMethodRegistrationsAsync(ctx);
        var output = new AuditCheckOutput();
        HashSet<string>? disabled = null;
        try
        {
            disabled = (await directory.GetUsersAsync(ctx)).Users.Where(u => !u.AccountEnabled).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (AuditUnavailableException ex) { output.Note($"Disabled accounts are not excluded: {ex.Message}"); }

        var members = registrations
            .Where(r => !string.Equals(r.UserType, "guest", StringComparison.OrdinalIgnoreCase))
            .Where(r => disabled is null || !disabled.Contains(r.UserId))
            .ToList();
        AuditSubject Row(AuditAuthRegistration r) => new()
        {
            Type = "user", Id = r.UserId, Name = r.DisplayName ?? r.UserPrincipalName ?? r.UserId, Upn = r.UserPrincipalName,
            Evidence = r.MethodsRegistered.Count == 0 ? "No authentication methods registered." : $"Registered: {string.Join(", ", r.MethodsRegistered)} (none MFA capable).",
            Properties = { ["isAdmin"] = AuditFindingBuilder.YesNo(r.IsAdmin), ["userType"] = r.UserType, ["methods"] = string.Join("; ", r.MethodsRegistered) }
        };

        var admins = members.Where(r => r.IsAdmin && !r.IsMfaCapable).Select(Row).ToList();
        var users = members.Where(r => !r.IsAdmin && !r.IsMfaCapable).Select(Row).ToList();
        if (admins.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "admins", AuditSeverity.Fail, "Admins without MFA",
                $"{AuditFindingBuilder.Count(admins.Count, "admin")} {(admins.Count == 1 ? "has" : "have")} no MFA-capable method registered.",
                Columns, admins, businessImpact: "An admin account protected only by a password can be used to take over the whole tenant."));
        if (users.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "users", AuditSeverity.Warn, "Users without MFA",
                $"{AuditFindingBuilder.Count(users.Count, "enabled user")} of {members.Count} {(users.Count == 1 ? "has" : "have")} no MFA-capable method registered.",
                Columns, users));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, $"All {AuditFindingBuilder.Count(members.Count, "enabled member user")} have an MFA-capable method registered."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(members.Count, "member user")} from the registration report.");
        return output;
    }
}
