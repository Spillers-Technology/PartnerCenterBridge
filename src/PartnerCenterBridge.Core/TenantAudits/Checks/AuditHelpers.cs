namespace PartnerCenterBridge.Core.TenantAudits.Checks;

/// <summary>What a user's sign-in activity proves, relative to an inactivity threshold.</summary>
public enum SignInStanding
{
    /// <summary>A successful sign-in (or, failing that, sign-in activity) within the threshold.</summary>
    Active,
    /// <summary>Last successful sign-in is at least threshold days ago.</summary>
    InactiveSinceSuccess,
    /// <summary>No successful sign-in is recorded and the last recorded attempt of any kind is at least threshold days ago.</summary>
    InactiveSinceAttempt,
    /// <summary>Recent sign-in attempts exist but no successful sign-in is recorded -- PCB cannot tell whether the account is in use.</summary>
    AttemptsWithoutSuccess,
    /// <summary>No sign-in of any kind is recorded, and the account is older than the threshold.</summary>
    NeverSignedIn,
    /// <summary>No sign-in recorded, but the account was created within the threshold (too new to call inactive).</summary>
    NewAccount
}

/// <param name="Basis">Which field the result rests on: "last successful sign-in", "last sign-in attempt", or "none recorded".</param>
public sealed record SignInAssessment(SignInStanding Standing, DateTimeOffset? LastSuccessful, DateTimeOffset? LastAttempt, int? DaysInactive, string Basis)
{
    public bool IsInactive => Standing is SignInStanding.InactiveSinceSuccess or SignInStanding.InactiveSinceAttempt or SignInStanding.NeverSignedIn;
}

/// <summary>
/// The one place inactivity is decided, so every check states it the same way and never claims
/// more than the data proves. Precedence: a recorded successful sign-in decides; without one, the
/// most recent attempt (interactive or non-interactive, which may have failed) can show that
/// <em>nothing at all</em> happened for N days, but a recent attempt cannot prove use; with no
/// record at all, the account's age decides between "never signed in" and "too new to say".
/// </summary>
public static class SignInEvaluator
{
    public static SignInAssessment Assess(AuditUser user, DateTimeOffset now, int thresholdDays)
    {
        var s = user.SignIn ?? new AuditSignInActivity(null, null, null);
        if (s.LastSuccessful is { } success)
        {
            var days = DaysBetween(success, now);
            return new(days >= thresholdDays ? SignInStanding.InactiveSinceSuccess : SignInStanding.Active,
                success, s.LastAttempt, days, "last successful sign-in");
        }
        if (s.LastAttempt is { } attempt)
        {
            var days = DaysBetween(attempt, now);
            return days >= thresholdDays
                ? new(SignInStanding.InactiveSinceAttempt, null, attempt, days, "last sign-in attempt (no success recorded)")
                : new(SignInStanding.AttemptsWithoutSuccess, null, attempt, null, "recent attempts, no success recorded");
        }
        var age = user.CreatedAt is { } created ? DaysBetween(created, now) : (int?)null;
        // An account with an unknown creation date and no sign-in record is treated as old enough:
        // the finding says "no sign-in recorded", which is true either way.
        return age is not null && age < thresholdDays
            ? new(SignInStanding.NewAccount, null, null, null, "none recorded (new account)")
            : new(SignInStanding.NeverSignedIn, null, null, age, "none recorded");
    }

    /// <summary>Whole days from <paramref name="from"/> to <paramref name="now"/>, never negative.</summary>
    public static int DaysBetween(DateTimeOffset from, DateTimeOffset now) => Math.Max(0, (int)Math.Floor((now - from).TotalDays));

    /// <summary>Standard sign-in columns for user subject tables.</summary>
    public static readonly AuditColumn[] Columns =
    [
        new("lastSuccessfulSignIn", "Last successful sign-in"),
        new("lastSignInAttempt", "Last sign-in attempt"),
        new("daysInactive", "Days inactive"),
        new("basis", "Basis")
    ];

    public static void Fill(AuditSubject subject, SignInAssessment a)
    {
        subject.Properties["lastSuccessfulSignIn"] = AuditFindingBuilder.Date(a.LastSuccessful);
        subject.Properties["lastSignInAttempt"] = AuditFindingBuilder.Date(a.LastAttempt);
        subject.Properties["daysInactive"] = a.DaysInactive?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        subject.Properties["basis"] = a.Basis;
    }

    /// <summary>Note added to every result that relies on sign-in activity.</summary>
    public const string SemanticsNote =
        "Inactivity is measured from Microsoft Entra signInActivity: the last successful sign-in when recorded (Microsoft records it since December 2023), otherwise the last interactive or non-interactive sign-in attempt. It shows sign-in to Microsoft Entra only; it does not prove whether someone used Microsoft 365 in other ways (for example mail delivered to a shared mailbox).";
}

/// <summary>License naming and the conservative "no-cost SKU" list.</summary>
public static class AuditLicenses
{
    /// <summary>
    /// SKUs that are free, viral or self-service sign-up licenses. A user holding only these is not
    /// treated as consuming paid licenses. Deliberately conservative: an unlisted SKU is assumed
    /// to possibly carry cost, so the list can only under-claim savings, never over-claim them.
    /// </summary>
    public static readonly IReadOnlySet<string> NoCostSkuPartNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FLOW_FREE", "POWER_BI_STANDARD", "POWERAPPS_VIRAL", "POWERAPPS_DEV", "TEAMS_EXPLORATORY",
        "WINDOWS_STORE", "STREAM", "CCIBOTS_PRIVPREV_VIRAL", "RIGHTSMANAGEMENT_ADHOC", "MICROSOFT_BUSINESS_CENTER"
    };

    /// <summary>SKU part numbers for a user's assigned SKU ids (falls back to the id when the SKU list is unavailable).</summary>
    public static IReadOnlyList<string> Names(AuditUser user, IReadOnlyDictionary<string, string> skuNames) =>
        user.AssignedSkuIds
            .Select(id => skuNames.TryGetValue(id, out var name) ? name : id)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Licenses not on the no-cost list.</summary>
    public static IReadOnlyList<string> PossiblyPaid(AuditUser user, IReadOnlyDictionary<string, string> skuNames) =>
        Names(user, skuNames).Where(n => !NoCostSkuPartNumbers.Contains(n)).ToList();

    /// <summary>
    /// skuId -> part number, or an empty map when the SKU list cannot be read (licenses then
    /// display as SKU ids and every SKU is treated as possibly paid). The note explains why.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, string> Names, string? Note)> SkuNamesAsync(IAuditDirectoryData data, AuditCheckContext ctx)
    {
        try
        {
            var skus = await data.GetSubscribedSkusAsync(ctx);
            return (skus.GroupBy(s => s.SkuId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SkuPartNumber, StringComparer.OrdinalIgnoreCase), null);
        }
        catch (AuditUnavailableException ex)
        {
            return (new Dictionary<string, string>(), $"License names could not be read ({ex.Message}); licenses are shown as SKU ids and none are treated as no-cost.");
        }
    }
}

/// <summary>
/// Directory roles PCB treats as privileged: the built-in roles that can change security
/// configuration, credentials, mail, devices, apps or billing across the tenant. Matched by role
/// template id (stable across tenants and languages).
/// </summary>
public static class PrivilegedRoles
{
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";

    public static readonly IReadOnlyDictionary<string, string> ByTemplateId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [GlobalAdministrator] = "Global Administrator",
        ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"] = "Conditional Access Administrator",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Exchange Administrator",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "SharePoint Administrator",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["c4e39bd9-1100-46d3-8c65-fb160da0071f"] = "Authentication Administrator",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Helpdesk Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
        ["3a2c62db-5318-420d-8d74-23affee5d9d5"] = "Intune Administrator",
        ["8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2"] = "Hybrid Identity Administrator",
        ["b0f54661-2d74-4c50-afa3-1ec803f12efe"] = "Billing Administrator"
    };

    public static bool IsPrivileged(string? roleTemplateId) => roleTemplateId is not null && ByTemplateId.ContainsKey(roleTemplateId);
}

/// <summary>Standard columns and subject construction for user rows.</summary>
public static class AuditUserRows
{
    public static AuditSubject Subject(AuditUser u, string? evidence = null) => new()
    {
        Type = "user",
        Id = u.Id,
        Name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserPrincipalName ?? u.Id : u.DisplayName,
        Upn = u.UserPrincipalName,
        Evidence = evidence,
        Properties =
        {
            ["enabled"] = AuditFindingBuilder.YesNo(u.AccountEnabled),
            ["userType"] = u.UserType,
            ["created"] = AuditFindingBuilder.Date(u.CreatedAt)
        }
    };
}
