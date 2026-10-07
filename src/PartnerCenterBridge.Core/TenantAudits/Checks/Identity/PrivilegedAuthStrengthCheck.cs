namespace PartnerCenterBridge.Core.TenantAudits.Checks.Identity;

/// <summary>How strong the methods are that admins have registered.</summary>
public sealed class PrivilegedAuthStrengthCheck(IAuditDirectoryData directory) : ITenantAuditCheck
{
    /// <summary>Methods resistant to phishing (bound to the device or origin).</summary>
    public static readonly IReadOnlySet<string> PhishingResistant = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "windowsHelloForBusiness", "fido2SecurityKey", "fido2", "passKeyDeviceBound", "passKeyDeviceBoundAuthenticator",
        "passKeyDeviceBoundWindowsHello", "macOsSecureEnclaveKey", "x509CertificateMultiFactor", "x509Certificate"
    };

    /// <summary>Phone- and mail-based methods that can be intercepted or redirected (SIM swap, mailbox compromise).</summary>
    public static readonly IReadOnlySet<string> Weak = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mobilePhone", "alternateMobilePhone", "officePhone", "email", "securityQuestion", "sms", "voice"
    };

    /// <summary>Registered values that are not a second factor and are ignored when grading.</summary>
    private static readonly IReadOnlySet<string> NotAFactor = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "temporaryAccessPass", "appPassword", "password"
    };

    private static readonly AuditColumn[] Columns = [new("methods", "Registered methods"), new("strength", "Strongest method class")];

    public AuditCheckDescriptor Descriptor { get; } = new()
    {
        Id = "privileged-auth-strength",
        Name = "Admin authentication strength",
        Category = AuditCategories.Identity,
        Description = "Grades the methods each admin has registered: phishing-resistant, app-based, or phone/email only.",
        BusinessImpact = "Admins are the most phished accounts; SMS, voice and email codes can be intercepted or redirected.",
        Recommendation = "Move admins to phishing-resistant methods (FIDO2 security keys, passkeys, Windows Hello for Business) and require them with an authentication strength policy.",
        SeverityRules =
        [
            "Fail: an admin has no second factor registered.",
            "Warn: an admin has only phone- or email-based methods.",
            "Info: an admin has app-based methods but nothing phishing-resistant.",
            "Pass: every admin has a phishing-resistant method registered."
        ],
        Requirements = [AuditRequirement.Graph("AuditLog.Read.All", "Authentication methods registration report."), AuditRequirement.License("Microsoft Entra ID P1 or P2")],
        Limitations = ["Admins are identified by the registration report's isAdmin flag. A registered strong method does not mean it is required at sign-in."]
    };

    public async Task<AuditCheckOutput> RunAsync(AuditCheckContext ctx)
    {
        var d = Descriptor;
        var admins = (await directory.GetAuthMethodRegistrationsAsync(ctx)).Where(r => r.IsAdmin).ToList();
        var output = new AuditCheckOutput();
        var none = new List<AuditSubject>();
        var weak = new List<AuditSubject>();
        var appOnly = new List<AuditSubject>();
        foreach (var r in admins)
        {
            var factors = r.MethodsRegistered.Where(m => !NotAFactor.Contains(m)).ToList();
            var cls = factors.Count == 0 ? "None"
                : factors.Any(PhishingResistant.Contains) ? "Phishing-resistant"
                : factors.All(Weak.Contains) ? "Phone or email only"
                : "App-based";
            if (cls == "Phishing-resistant") continue;
            var s = new AuditSubject
            {
                Type = "user", Id = r.UserId, Name = r.DisplayName ?? r.UserPrincipalName ?? r.UserId, Upn = r.UserPrincipalName,
                Evidence = $"Strongest registered: {cls}.",
                Properties = { ["methods"] = string.Join("; ", r.MethodsRegistered), ["strength"] = cls }
            };
            (cls == "None" ? none : cls == "App-based" ? appOnly : weak).Add(s);
        }
        if (none.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "none", AuditSeverity.Fail, "Admins with no second factor",
                $"{AuditFindingBuilder.Count(none.Count, "admin")} {(none.Count == 1 ? "has" : "have")} no second factor registered.", Columns, none));
        if (weak.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "weak", AuditSeverity.Warn, "Admins with phone or email methods only",
                $"{AuditFindingBuilder.Count(weak.Count, "admin")} can only use SMS, voice or email codes.", Columns, weak));
        if (appOnly.Count > 0)
            output.Add(AuditFindingBuilder.Create(d, "not-phishing-resistant", AuditSeverity.Info, "Admins without a phishing-resistant method",
                $"{AuditFindingBuilder.Count(appOnly.Count, "admin")} {(appOnly.Count == 1 ? "uses" : "use")} app-based methods but {(appOnly.Count == 1 ? "has" : "have")} nothing phishing-resistant.", Columns, appOnly));
        if (output.Findings.Count == 0)
            output.Add(AuditFindingBuilder.Pass(d, admins.Count == 0
                ? "The registration report lists no admins."
                : $"All {AuditFindingBuilder.Count(admins.Count, "admin")} have a phishing-resistant method registered."));
        output.Note($"Evaluated {AuditFindingBuilder.Count(admins.Count, "admin")}.");
        return output;
    }
}
